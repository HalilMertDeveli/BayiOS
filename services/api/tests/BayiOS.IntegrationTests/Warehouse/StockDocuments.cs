using System.Net;
using System.Net.Http.Json;
using BayiOS.IntegrationTests.Catalog;
using BayiOS.IntegrationTests.Inventory;
using BayiOS.Modules.Inventory.Contracts;
using BayiOS.Modules.Warehouse.Contracts;

namespace BayiOS.IntegrationTests.Warehouse;

/// <summary>HTTP helpers for transfers and stock counts (#168). Synthetic data only.</summary>
internal static class StockDocuments
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static Task<HttpResponseMessage> CreateTransferAsync(
        CatalogClient client, Guid source, Guid destination, params (Guid VariantId, decimal Quantity, Guid? SourceLocationId, Guid? DestinationLocationId)[] lines) =>
        client.PostAsync("/api/v1/stock-transfers", new
        {
            sourceWarehouseId = source,
            destinationWarehouseId = destination,
            note = "Sentetik transfer",
            lines = lines.Select(l => new { variantId = l.VariantId, quantity = l.Quantity, sourceLocationId = l.SourceLocationId, destinationLocationId = l.DestinationLocationId }),
        });

    public static async Task<StockTransferResponse> DraftAsync(
        CatalogClient client, Guid source, Guid destination, params (Guid VariantId, decimal Quantity, Guid? SourceLocationId, Guid? DestinationLocationId)[] lines) =>
        await ReadAsync<StockTransferResponse>(await CreateTransferAsync(client, source, destination, lines), HttpStatusCode.Created);

    /// <summary>POST /stock-transfers/{id}/{action} with a fresh (or the given) Idempotency-Key.</summary>
    public static Task<HttpResponseMessage> TransferActionAsync(CatalogClient client, Guid id, string action, object? body = null, string? idempotencyKey = null) =>
        client.SendAsync(HttpMethod.Post, $"/api/v1/stock-transfers/{id}/{action}", body, idempotencyKey: idempotencyKey);

    public static async Task<StockTransferResponse> DispatchedAsync(
        CatalogClient client, Guid source, Guid destination, params (Guid VariantId, decimal Quantity, Guid? SourceLocationId, Guid? DestinationLocationId)[] lines)
    {
        var draft = await DraftAsync(client, source, destination, lines);
        return (await ReadAsync<StockTransferActionResponse>(await TransferActionAsync(client, draft.Transfer.Id, "dispatch"))).Transfer;
    }

    public static Task<HttpResponseMessage> ReceiveAsync(CatalogClient client, Guid id, params object[] lines) =>
        TransferActionAsync(client, id, "receive", new { lines });

    public static Task<HttpResponseMessage> CreateCountAsync(CatalogClient client, object body) => client.PostAsync("/api/v1/stock-counts", body);

    public static Task<HttpResponseMessage> CountActionAsync(CatalogClient client, Guid id, string action, object? body = null) =>
        client.SendAsync(HttpMethod.Post, $"/api/v1/stock-counts/{id}/{action}", body);

    public static Task<HttpResponseMessage> EnterAsync(CatalogClient client, Guid id, params object[] entries) =>
        CountActionAsync(client, id, "entries", new { entries });

    public static async Task<StockCountResponse> GetCountAsync(CatalogClient client, Guid id) =>
        await ReadAsync<StockCountResponse>(await client.GetAsync($"/api/v1/stock-counts/{id}"));

    public static async Task<StockTransferResponse> GetTransferAsync(CatalogClient client, Guid id) =>
        await ReadAsync<StockTransferResponse>(await client.GetAsync($"/api/v1/stock-transfers/{id}"));

    public static async Task<IReadOnlyList<MovementResult>> MovementsAsync(CatalogClient client, Guid sourceId) =>
        (await ReadAsync<MovementPageResponse>(await client.GetAsync($"/api/v1/inventory/movements?sourceId={sourceId}&limit=200"))).Items;

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.True(response.StatusCode == expected, $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return (await response.Content.ReadFromJsonAsync<T>(Ct))!;
    }

    /// <summary>I10 (inventory-and-orders §5.1): every transfer pair of this tenant sums to 0.</summary>
    public static async Task AssertTransferPairsBalancedAsync(InventoryKit kit) =>
        Assert.Equal(0L, await kit.CountAsync(
            """
            SELECT count(*) FROM (
                SELECT transfer_group_id FROM inventory.inventory_movements
                WHERE tenant_id = $1 AND transfer_group_id IS NOT NULL
                GROUP BY transfer_group_id HAVING sum(quantity) <> 0 OR count(*) <> 2) g
            """));

    /// <summary>Queues every worker behind one gate, opens it at once and waits for all of them.</summary>
    public static async Task<T[]> ParallelAsync<T>(int count, Func<int, Task<T>> work)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, count)
            .Select(i => Task.Run(
                async () =>
                {
                    await gate.Task;
                    return await work(i);
                },
                Ct))
            .ToArray();
        gate.SetResult();
        return await Task.WhenAll(tasks);
    }
}
