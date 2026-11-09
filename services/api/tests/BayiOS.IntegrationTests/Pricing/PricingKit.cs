using System.Net;
using System.Net.Http.Json;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Infrastructure;
using BayiOS.Modules.Customers.Contracts;
using BayiOS.Modules.Pricing.Contracts;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Persistence;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace BayiOS.IntegrationTests.Pricing;

/// <summary>
/// A fresh tenant with an owner, an "adet" unit and helpers for variants, price lists, items, dealer groups, customers
/// and quotes. Synthetic motorcycle-parts / LED data only — no real prices or customers.
/// </summary>
internal sealed class PricingKit
{
    private readonly ApiFixture _fixture;
    private readonly Guid _unitId;
    private int _sequence;

    private PricingKit(ApiFixture fixture, CatalogClient owner, Guid unitId)
    {
        _fixture = fixture;
        Owner = owner;
        _unitId = unitId;
    }

    public CatalogClient Owner { get; }

    public Guid TenantId => Owner.TenantId;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<PricingKit> CreateAsync(ApiFixture fixture, string sector = "motorcycle_parts")
    {
        var owner = await CatalogClient.NewTenantOwnerAsync(fixture, sector);
        var unit = await owner.CreateUnitAsync("adet", "Adet");
        return new PricingKit(fixture, owner, unit.Id);
    }

    public async Task<Guid> VariantAsync(string? sku = null)
    {
        var code = sku ?? $"SKU-{Interlocked.Increment(ref _sequence):D4}";
        var product = await Owner.CreateSimpleProductAsync(_unitId, "Sentetik ürün " + code, code);
        return Assert.Single(product.Variants).Id;
    }

    public Task<PriceListResponse> PriceListAsync(
        string name, Guid? basePriceListId = null, decimal? discountPercent = null, bool? isDefault = null, string? kind = null, bool? pricesIncludeTax = null) =>
        Owner.CreatedAsync<PriceListResponse>("/api/v1/price-lists", new { name, basePriceListId, discountPercent, isDefault, kind, pricesIncludeTax });

    public async Task<UpsertPriceListItemsResponse> ItemsAsync(Guid priceListId, params (Guid VariantId, decimal MinQuantity, decimal UnitPrice)[] items)
    {
        var response = await Owner.SendAsync(
            HttpMethod.Put,
            $"/api/v1/price-lists/{priceListId}/items",
            new { items = items.Select(i => new { variantId = i.VariantId, minQuantity = i.MinQuantity, unitPrice = i.UnitPrice }) },
            idempotencyKey: Guid.NewGuid().ToString());
        return await Owner.OkAsync<UpsertPriceListItemsResponse>(response);
    }

    public Task<DealerGroupResponse> GroupAsync(string name, Guid? priceListId = null, decimal? defaultDiscountPercent = null) =>
        Owner.CreatedAsync<DealerGroupResponse>("/api/v1/dealer-groups", new { name, priceListId, defaultDiscountPercent });

    public async Task<CustomerResponse> CustomerAsync(string? code = null, Guid? dealerGroupId = null, Guid? priceListId = null, string? name = null)
    {
        code ??= $"C{Interlocked.Increment(ref _sequence):D4}";
        var detail = await Owner.CreatedAsync<CustomerDetailResponse>(
            "/api/v1/customers", new { code, name = name ?? "Sentetik Motor Ltd. " + code, dealerGroupId, priceListId });
        return detail.Customer;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Reads as kit.QuoteResponseAsync at call sites.")]
    public Task<HttpResponseMessage> QuoteResponseAsync(CatalogClient client, Guid? customerId, params (Guid VariantId, decimal Quantity)[] lines) =>
        client.SendAsync(HttpMethod.Post, "/api/v1/pricing/quote", new
        {
            customerId,
            date = "2026-11-05",
            lines = lines.Select(l => new { variantId = l.VariantId, quantity = l.Quantity }),
        });

    public async Task<PriceQuote> QuoteAsync(Guid? customerId, params (Guid VariantId, decimal Quantity)[] lines)
    {
        var response = await QuoteResponseAsync(Owner, customerId, lines);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"quote: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return (await response.Content.ReadFromJsonAsync<PriceQuote>(Ct))!;
    }

    /// <summary>Calls <see cref="IPriceResolver"/> as Orders will: inside a request-like UnitOfWork of this tenant.</summary>
    public async Task<PricingResult<PriceQuote>> ResolveInUnitOfWorkAsync(PriceQuoteRequest request)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
        context.UserId = Owner.UserId;
        context.TenantId = TenantId;
        context.Role = Roles.Owner;
        var unitOfWork = scope.ServiceProvider.GetRequiredService<UnitOfWork>();
        await unitOfWork.BeginAsync(readOnly: true, Ct);
        var result = await scope.ServiceProvider.GetRequiredService<IPriceResolver>().QuoteAsync(request, Ct);
        await unitOfWork.CommitAsync(Ct);
        return result;
    }
}
