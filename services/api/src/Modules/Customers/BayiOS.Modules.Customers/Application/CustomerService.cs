using BayiOS.Modules.Customers.Contracts;
using BayiOS.Modules.Customers.Domain;
using BayiOS.Modules.Customers.Infrastructure.Persistence;
using BayiOS.Modules.Pricing.Contracts;
using BayiOS.SharedKernel.Authorization;
using BayiOS.SharedKernel.Platform.Audit;
using BayiOS.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BayiOS.Modules.Customers.Application;

internal sealed record CustomerListQuery(string? Q, Guid? DealerGroupId, IReadOnlyList<string> Statuses, int Limit, NameCursor? Cursor);

/// <summary>
/// Customers / dealers and their addresses (CUS-01, CUS-02, CUS-03, CUS-05). Assigning a dealer group or a
/// customer-specific price list needs <c>pricelists.assign</c> on top of <c>customers.write</c> (a sales user may edit
/// contact data but not the price conditions). Customers are archived or anonymized, never deleted.
/// </summary>
internal sealed class CustomerService(
    CustomersDbContext db,
    IPriceListDirectory priceLists,
    ICustomerOrderPersonalData orderPersonalData,
    IAuditLog audit,
    IRequestContext requestContext,
    TimeProvider clock)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;
    public const int MaxWhatsappNumbers = 5;
    public const int MaxAddresses = 50;

    private static readonly string[] CreateMembers =
    [
        "code", "name", "contactName", "taxNumber", "taxOffice", "phone", "email", "whatsappNumbers", "dealerGroupId", "priceListId",
        "paymentTermDays", "creditLimit", "currency", "notes",
    ];

    private Guid UserId => requestContext.UserId ?? throw new InvalidOperationException("No authenticated user.");

    private Guid TenantId => requestContext.TenantId ?? throw new InvalidOperationException("No tenant resolved.");

    // ---- Customers ----

    /// <summary>
    /// Keyset-paged by (name, id). Search: name or code contains <c>q</c> (pg_trgm GIN on name, ILIKE), or tax number,
    /// phone or a WhatsApp number equals <c>q</c> (CUS-05).
    /// </summary>
    public async Task<CustomerPageResponse> ListAsync(CustomerListQuery query, CancellationToken cancellationToken)
    {
        var customers = db.Customers.AsNoTracking().Where(c => query.Statuses.Contains(c.Status));
        if (query.DealerGroupId is { } groupId)
        {
            customers = customers.Where(c => c.DealerGroupId == groupId);
        }

        if (CustomerText.Clean(query.Q) is { } term)
        {
            var pattern = CustomerText.ContainsPattern(term);
            var compact = CustomerText.NormalizeE164(term) ?? CustomerText.NormalizeTaxNumber(term) ?? term;
            customers = customers.Where(c => EF.Functions.ILike(c.Name, pattern, "\\")
                || EF.Functions.ILike(c.Code, pattern, "\\")
                || c.TaxNumber == compact
                || c.Phone == term
                || c.WhatsappNumbers.Contains(compact));
        }

        if (query.Cursor is { } cursor)
        {
            var (name, id) = (cursor.Name, cursor.Id);
            customers = customers.Where(c => EF.Functions.GreaterThan(ValueTuple.Create(c.Name, c.Id), ValueTuple.Create(name, id)));
        }

        var page = await customers.OrderBy(c => c.Name).ThenBy(c => c.Id).Take(query.Limit + 1).ToListAsync(cancellationToken);
        var hasMore = page.Count > query.Limit;
        if (hasMore)
        {
            page.RemoveAt(page.Count - 1);
        }

        return new CustomerPageResponse(
            page.Select(c => ToResponse(c, maskTaxNumber: true)).ToList(),
            new CustomerPageInfo(hasMore ? new NameCursor(page[^1].Name, page[^1].Id).Encode() : null, hasMore, query.Limit));
    }

    public async Task<AppResult<CustomerDetailResponse>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var customer = await db.Customers.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (customer is null)
        {
            return AppError.NotFound();
        }

        return new CustomerDetailResponse(ToResponse(customer, maskTaxNumber: false), await LoadAddressesAsync(id, cancellationToken));
    }

    public async Task<AppResult<CustomerDetailResponse>> CreateAsync(CreateCustomerRequest request, CancellationToken cancellationToken)
    {
        var values = new UpdateCustomerRequest(
            request.Code, request.Name, request.ContactName, request.TaxNumber, request.TaxOffice, request.Phone, request.Email,
            request.WhatsappNumbers, request.DealerGroupId, request.PriceListId, request.PaymentTermDays, request.CreditLimit,
            request.Currency, request.Notes, Status: null);
        var now = Now();
        var customer = Customer.Create(TenantId, string.Empty, UserId, now);
        if (await ApplyAsync(customer, new Patch<UpdateCustomerRequest>(values, CreateMembers.ToHashSet()), isNew: true, cancellationToken) is { } error)
        {
            return error;
        }

        db.Customers.Add(customer);
        return await SaveAsync(() => new CustomerDetailResponse(ToResponse(customer, maskTaxNumber: false), []), cancellationToken);
    }

    public async Task<AppResult<CustomerDetailResponse>> UpdateAsync(
        Guid id, Patch<UpdateCustomerRequest> patch, long expectedVersion, CancellationToken cancellationToken)
    {
        var customer = await db.Customers.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (customer is null)
        {
            return AppError.NotFound();
        }

        if (customer.RowVersion != expectedVersion)
        {
            return AppError.VersionMismatch();
        }

        if (customer.AnonymizedAt is not null)
        {
            return AppError.Conflict("customers.anonymized", "Anonymized customers cannot change");
        }

        if (await ApplyAsync(customer, patch, isNew: false, cancellationToken) is { } error)
        {
            return error;
        }

        customer.Touch(UserId, Now());
        return await SaveAsync(
            () => new CustomerDetailResponse(ToResponse(customer, maskTaxNumber: false), []),
            cancellationToken,
            reloadAddresses: id);
    }

    /// <summary>
    /// Data-subject erasure (data-policy §5): personal fields cleared, addresses removed, customer archived, and the
    /// personal fields of every order address snapshot of the customer masked through Orders (#115) — all in the
    /// request transaction. The row (id, code) stays so order history keeps its integrity. Irreversible. Idempotent:
    /// a repeat call on an anonymized customer re-runs only the order masking (catching orders anonymized before
    /// #115) and returns the customer unchanged. Audit <c>customer.anonymized</c> (counts only, no PII) whenever
    /// something changed.
    /// </summary>
    public async Task<AppResult<CustomerDetailResponse>> AnonymizeAsync(Guid id, CancellationToken cancellationToken)
    {
        var customer = await db.Customers.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (customer is null)
        {
            return AppError.NotFound();
        }

        var firstTime = customer.AnonymizedAt is null;
        var addressesRemoved = 0;
        if (firstTime)
        {
            var now = Now();
            var addresses = await db.Addresses.Where(a => a.CustomerId == id).ToListAsync(cancellationToken);
            addressesRemoved = addresses.Count;
            db.Addresses.RemoveRange(addresses);
            customer.Anonymize(now);
            customer.Touch(UserId, now);
        }

        // Orders first locks every order header of the customer, so no concurrent submit / approve can snapshot the
        // addresses removed above after this transaction commits.
        var ordersMasked = await orderPersonalData.AnonymizeOrderSnapshotsAsync(id, cancellationToken);
        if (firstTime || ordersMasked > 0)
        {
            await audit.RecordAsync(
                new AuditEvent(CustomerAuditActions.Anonymized, CustomerAuditActions.EntityType, id, new AuditChanges()
                    .Value("repeat", !firstTime)
                    .Value("addressesRemoved", addressesRemoved)
                    .Value("ordersMasked", ordersMasked)
                    .ToDictionary()),
                cancellationToken);
        }

        return await SaveAsync(() => new CustomerDetailResponse(ToResponse(customer, maskTaxNumber: false), []), cancellationToken);
    }

    // ---- Addresses ----

    public async Task<AppResult<AddressListResponse>> ListAddressesAsync(Guid customerId, CancellationToken cancellationToken) =>
        await db.Customers.AnyAsync(c => c.Id == customerId, cancellationToken)
            ? new AddressListResponse(await LoadAddressesAsync(customerId, cancellationToken))
            : AppError.NotFound();

    public async Task<AppResult<AddressResponse>> CreateAddressAsync(Guid customerId, CreateAddressRequest request, CancellationToken cancellationToken)
    {
        var values = new UpdateAddressRequest(
            request.Type ?? AddressTypes.Shipping, request.IsDefault, request.Title, request.Line1, request.Line2, request.District, request.City,
            request.PostalCode, request.Country ?? "TR", request.ContactName, request.ContactPhone);
        var errors = ValidateAddress(new Patch<UpdateAddressRequest>(values, AddressMembers), isNew: true);
        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        if (await FindWritableCustomerAsync(customerId, cancellationToken) is { Error: { } customerError })
        {
            return customerError;
        }

        if (await db.Addresses.CountAsync(a => a.CustomerId == customerId, cancellationToken) >= MaxAddresses)
        {
            return AppError.Conflict("customers.address_limit", $"A customer can have at most {MaxAddresses} addresses");
        }

        var now = Now();
        var address = CustomerAddress.Create(TenantId, customerId, UserId, now);
        Apply(address, new Patch<UpdateAddressRequest>(values, AddressMembers));
        var current = await db.Addresses.SingleOrDefaultAsync(a => a.CustomerId == customerId && a.Type == address.Type && a.IsDefault, cancellationToken);
        if (current is null)
        {
            address.IsDefault = true;
        }
        else if (request.IsDefault == true)
        {
            if (await ClearDefaultAsync(current, now, cancellationToken) is { } clearError)
            {
                return clearError;
            }

            address.IsDefault = true;
        }

        db.Addresses.Add(address);
        return await SaveAsync(() => ToResponse(address), cancellationToken);
    }

    public async Task<AppResult<AddressResponse>> UpdateAddressAsync(
        Guid customerId, Guid addressId, Patch<UpdateAddressRequest> patch, long expectedVersion, CancellationToken cancellationToken)
    {
        var errors = ValidateAddress(patch, isNew: false);
        if (patch.Has("isDefault") && patch.Values.IsDefault != true)
        {
            errors["isDefault"] = ["Only true is accepted: make another address the default instead."];
        }

        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        if (await FindWritableCustomerAsync(customerId, cancellationToken) is { Error: { } customerError })
        {
            return customerError;
        }

        var address = await db.Addresses.SingleOrDefaultAsync(a => a.Id == addressId && a.CustomerId == customerId, cancellationToken);
        if (address is null)
        {
            return AppError.NotFound();
        }

        if (address.RowVersion != expectedVersion)
        {
            return AppError.VersionMismatch();
        }

        var now = Now();
        var newType = patch.Values.Type ?? address.Type;
        if (newType != address.Type && address.IsDefault)
        {
            return AppError.Conflict("customers.default_address", "Make another address the default before changing the type of this one");
        }

        if (patch.Values.IsDefault == true && (!address.IsDefault || newType != address.Type))
        {
            var current = await db.Addresses.SingleOrDefaultAsync(
                a => a.CustomerId == customerId && a.Type == newType && a.IsDefault && a.Id != addressId, cancellationToken);
            if (current is not null && await ClearDefaultAsync(current, now, cancellationToken) is { } clearError)
            {
                return clearError;
            }

            address.IsDefault = true;
        }

        Apply(address, patch);
        address.Touch(UserId, now);
        return await SaveAsync(() => ToResponse(address), cancellationToken);
    }

    /// <summary>Removes an address. Removing the default promotes the oldest remaining address of the same type.</summary>
    public async Task<AppResult<bool>> DeleteAddressAsync(Guid customerId, Guid addressId, CancellationToken cancellationToken)
    {
        if (await FindWritableCustomerAsync(customerId, cancellationToken) is { Error: { } customerError })
        {
            return customerError;
        }

        var address = await db.Addresses.SingleOrDefaultAsync(a => a.Id == addressId && a.CustomerId == customerId, cancellationToken);
        if (address is null)
        {
            return AppError.NotFound();
        }

        db.Addresses.Remove(address);
        if (address.IsDefault)
        {
            if ((await SaveAsync(() => true, cancellationToken)).Error is { } error)
            {
                return error;
            }

            var next = await db.Addresses
                .Where(a => a.CustomerId == customerId && a.Type == address.Type)
                .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (next is not null)
            {
                next.IsDefault = true;
                next.Touch(UserId, Now());
            }
        }

        return await SaveAsync(() => true, cancellationToken);
    }

    // ---- Helpers ----

    private static readonly IReadOnlySet<string> AddressMembers = new HashSet<string>(StringComparer.Ordinal)
    {
        "type", "isDefault", "title", "line1", "line2", "district", "city", "postalCode", "country", "contactName", "contactPhone",
    };

    /// <summary>Validates and applies a (merge-patch) change; price-related fields also check <c>pricelists.assign</c>.</summary>
    private async Task<AppError?> ApplyAsync(Customer customer, Patch<UpdateCustomerRequest> patch, bool isNew, CancellationToken cancellationToken)
    {
        var v = patch.Values;
        var errors = new Dictionary<string, string[]>();

        var code = CustomerText.NormalizeCode(v.Code);
        if (patch.Has("code") && (code is null || code.Length > 30))
        {
            errors["code"] = ["Required: 1–30 characters without whitespace."];
        }

        var name = CustomerText.Clean(v.Name);
        if (patch.Has("name") && (name is null || name.Length > 200))
        {
            errors["name"] = ["Required: 1–200 characters."];
        }

        var contactName = CustomerText.Clean(v.ContactName);
        if (contactName is { Length: > 100 })
        {
            errors["contactName"] = ["Must be at most 100 characters."];
        }

        var taxNumber = CustomerText.NormalizeTaxNumber(v.TaxNumber);
        if (taxNumber is not null && !CustomerText.IsTaxNumber(taxNumber))
        {
            errors["taxNumber"] = ["Must be 10 (VKN) or 11 (TCKN) digits."];
        }

        var taxOffice = CustomerText.Clean(v.TaxOffice);
        if (taxOffice is { Length: > 100 })
        {
            errors["taxOffice"] = ["Must be at most 100 characters."];
        }

        var phone = CustomerText.Clean(v.Phone);
        if (phone is not null && !CustomerText.IsPhone(phone))
        {
            errors["phone"] = ["Must be 7–20 characters: digits, spaces, dashes, parentheses, optional leading +."];
        }

        var email = CustomerText.Clean(v.Email);
        if (email is not null && !CustomerText.IsEmail(email))
        {
            errors["email"] = ["Must be a valid e-mail address."];
        }

        var whatsapp = new List<string>();
        foreach (var number in v.WhatsappNumbers ?? [])
        {
            if (CustomerText.NormalizeE164(number) is { } normalized)
            {
                if (!whatsapp.Contains(normalized))
                {
                    whatsapp.Add(normalized);
                }
            }
            else
            {
                errors["whatsappNumbers"] = ["Each number must be E.164, e.g. +905321234567."];
            }
        }

        if (whatsapp.Count > MaxWhatsappNumbers)
        {
            errors["whatsappNumbers"] = [$"At most {MaxWhatsappNumbers} numbers."];
        }

        if (v.PaymentTermDays is < 0 or > 3650)
        {
            errors["paymentTermDays"] = ["Must be between 0 and 3650."];
        }

        if (v.CreditLimit is { } limit && !CustomerText.IsMoney(limit))
        {
            errors["creditLimit"] = ["Must be 0 or more with at most 4 decimals."];
        }

        var currency = CustomerText.Clean(v.Currency)?.ToUpperInvariant();
        if (currency is not null && (currency.Length != 3 || !currency.All(c => c is >= 'A' and <= 'Z')))
        {
            errors["currency"] = ["Must be a 3-letter ISO 4217 code."];
        }

        var notes = CustomerText.Clean(v.Notes);
        if (notes is { Length: > 2000 })
        {
            errors["notes"] = ["Must be at most 2000 characters."];
        }

        if (patch.Has("status") && (v.Status is null || !CustomerStatuses.All.Contains(v.Status)))
        {
            errors["status"] = [$"Must be one of: {string.Join(", ", CustomerStatuses.All)}."];
        }

        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        var groupChanged = patch.Has("dealerGroupId") && (isNew ? v.DealerGroupId is not null : v.DealerGroupId != customer.DealerGroupId);
        var listChanged = patch.Has("priceListId") && (isNew ? v.PriceListId is not null : v.PriceListId != customer.PriceListId);
        if ((groupChanged || listChanged) && !RolePermissions.IsGranted(requestContext.Role, Permissions.PricelistsAssign))
        {
            return AppError.Forbidden(Permissions.PricelistsAssign);
        }

        if (groupChanged && v.DealerGroupId is { } groupId
            && !await db.DealerGroups.AnyAsync(g => g.Id == groupId && g.Status == DealerGroupStatuses.Active, cancellationToken))
        {
            errors["dealerGroupId"] = ["Dealer group not found or archived."];
        }

        if (listChanged && v.PriceListId is { } priceListId && await priceLists.FindAsync(priceListId, cancellationToken) is not { IsAssignable: true })
        {
            errors["priceListId"] = ["Must be a sales price list that is not archived."];
        }

        if (errors.Count > 0)
        {
            return AppError.Validation(errors);
        }

        if (patch.Has("code"))
        {
            customer.Code = code!;
        }

        if (patch.Has("name"))
        {
            customer.Name = name!;
        }

        if (patch.Has("contactName"))
        {
            customer.ContactName = contactName;
        }

        if (patch.Has("taxNumber"))
        {
            customer.TaxNumber = taxNumber;
        }

        if (patch.Has("taxOffice"))
        {
            customer.TaxOffice = taxOffice;
        }

        if (patch.Has("phone"))
        {
            customer.Phone = phone;
        }

        if (patch.Has("email"))
        {
            customer.Email = email;
        }

        if (patch.Has("whatsappNumbers"))
        {
            customer.WhatsappNumbers = [.. whatsapp];
        }

        if (groupChanged)
        {
            customer.DealerGroupId = v.DealerGroupId;
        }

        if (listChanged)
        {
            customer.PriceListId = v.PriceListId;
        }

        if (patch.Has("paymentTermDays"))
        {
            customer.PaymentTermDays = v.PaymentTermDays;
        }

        if (patch.Has("creditLimit"))
        {
            customer.CreditLimit = v.CreditLimit;
        }

        if (patch.Has("currency"))
        {
            customer.Currency = currency;
        }

        if (patch.Has("notes"))
        {
            customer.Notes = notes;
        }

        if (patch.Has("status"))
        {
            customer.SetStatus(v.Status!, Now());
        }

        return null;
    }

    private static Dictionary<string, string[]> ValidateAddress(Patch<UpdateAddressRequest> patch, bool isNew)
    {
        var v = patch.Values;
        var errors = new Dictionary<string, string[]>();
        if (patch.Has("type") && (v.Type is null || !AddressTypes.All.Contains(v.Type)))
        {
            errors["type"] = [$"Must be one of: {string.Join(", ", AddressTypes.All)}."];
        }

        if ((isNew || patch.Has("line1")) && CustomerText.Clean(v.Line1) is not { Length: <= 200 })
        {
            errors["line1"] = ["Required: 1–200 characters."];
        }

        if ((isNew || patch.Has("city")) && CustomerText.Clean(v.City) is not { Length: <= 100 })
        {
            errors["city"] = ["Required: 1–100 characters."];
        }

        if (patch.Has("country") && CustomerText.Clean(v.Country)?.ToUpperInvariant() is var country
            && (country is null || country.Length != 2 || !country.All(char.IsAsciiLetterUpper)))
        {
            errors["country"] = ["Must be an ISO 3166-1 alpha-2 code (e.g. TR)."];
        }

        foreach (var (member, value, max) in new[]
        {
            ("title", v.Title, 100), ("line2", v.Line2, 200), ("district", v.District, 100), ("postalCode", v.PostalCode, 20),
            ("contactName", v.ContactName, 100),
        })
        {
            if (CustomerText.Clean(value) is { } text && text.Length > max)
            {
                errors[member] = [$"Must be at most {max} characters."];
            }
        }

        if (CustomerText.Clean(v.ContactPhone) is { } phone && !CustomerText.IsPhone(phone))
        {
            errors["contactPhone"] = ["Must be 7–20 characters: digits, spaces, dashes, parentheses, optional leading +."];
        }

        return errors;
    }

    private static void Apply(CustomerAddress address, Patch<UpdateAddressRequest> patch)
    {
        var v = patch.Values;
        if (patch.Has("type") && v.Type is not null)
        {
            address.Type = v.Type;
        }

        if (patch.Has("title"))
        {
            address.Title = CustomerText.Clean(v.Title);
        }

        if (patch.Has("line1"))
        {
            address.Line1 = CustomerText.Clean(v.Line1)!;
        }

        if (patch.Has("line2"))
        {
            address.Line2 = CustomerText.Clean(v.Line2);
        }

        if (patch.Has("district"))
        {
            address.District = CustomerText.Clean(v.District);
        }

        if (patch.Has("city"))
        {
            address.City = CustomerText.Clean(v.City)!;
        }

        if (patch.Has("postalCode"))
        {
            address.PostalCode = CustomerText.Clean(v.PostalCode);
        }

        if (patch.Has("country"))
        {
            address.Country = CustomerText.Clean(v.Country)!.ToUpperInvariant();
        }

        if (patch.Has("contactName"))
        {
            address.ContactName = CustomerText.Clean(v.ContactName);
        }

        if (patch.Has("contactPhone"))
        {
            address.ContactPhone = CustomerText.Clean(v.ContactPhone);
        }
    }

    private async Task<AppResult<Customer>> FindWritableCustomerAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var customer = await db.Customers.AsNoTracking().SingleOrDefaultAsync(c => c.Id == customerId, cancellationToken);
        return customer switch
        {
            null => AppError.NotFound(),
            { Status: CustomerStatuses.Archived } => AppError.Conflict("customers.archived", "Archived customers cannot change"),
            _ => customer,
        };
    }

    private async Task<AppError?> ClearDefaultAsync(CustomerAddress current, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // The partial unique index allows one default per customer and type at any time: clear the old one first.
        current.IsDefault = false;
        current.Touch(UserId, now);
        return (await SaveAsync(() => true, cancellationToken)).Error;
    }

    private async Task<IReadOnlyList<AddressResponse>> LoadAddressesAsync(Guid customerId, CancellationToken cancellationToken) =>
        (await db.Addresses.AsNoTracking()
            .Where(a => a.CustomerId == customerId)
            .OrderBy(a => a.Type).ThenByDescending(a => a.IsDefault).ThenBy(a => a.CreatedAt).ThenBy(a => a.Id)
            .ToListAsync(cancellationToken))
        .Select(ToResponse)
        .ToList();

    private async Task<AppResult<T>> SaveAsync<T>(Func<T> response, CancellationToken cancellationToken, Guid? reloadAddresses = null)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return AppError.VersionMismatch();
        }
        catch (DbUpdateException ex) when (FromUniqueViolation(ex) is { } conflict)
        {
            return conflict;
        }

        var value = response();
        if (reloadAddresses is { } customerId && value is CustomerDetailResponse detail)
        {
            return (T)(object)(detail with { Addresses = await LoadAddressesAsync(customerId, cancellationToken) });
        }

        return value;
    }

    private static AppError? FromUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
            ? pg.ConstraintName switch
            {
                CustomersDbContext.CodeUniqueIndex => AppError.Conflict("customers.code_taken", "Customer code is already used in this tenant"),
                CustomersDbContext.DefaultAddressUniqueIndex =>
                    AppError.Conflict("customers.default_address_conflict", "Another default address was set concurrently; retry"),
                _ => null,
            }
            : null;

    private DateTimeOffset Now()
    {
        var now = clock.GetUtcNow();
        return new DateTimeOffset(now.Ticks - (now.Ticks % 10), TimeSpan.Zero);
    }

    private static CustomerResponse ToResponse(Customer c, bool maskTaxNumber) => new(
        c.Id,
        c.Code,
        c.Name,
        c.ContactName,
        maskTaxNumber ? CustomerText.MaskTaxNumber(c.TaxNumber) : c.TaxNumber,
        c.TaxOffice,
        c.Phone,
        c.Email,
        c.WhatsappNumbers,
        c.DealerGroupId,
        c.PriceListId,
        c.PaymentTermDays,
        c.CreditLimit,
        c.Currency,
        c.Notes,
        c.Status,
        c.AnonymizedAt,
        c.RowVersion,
        c.CreatedAt,
        c.UpdatedAt);

    private static AddressResponse ToResponse(CustomerAddress a) => new(
        a.Id, a.CustomerId, a.Type, a.IsDefault, a.Title, a.Line1, a.Line2, a.District, a.City, a.PostalCode, a.Country, a.ContactName,
        a.ContactPhone, a.RowVersion, a.CreatedAt, a.UpdatedAt);
}
