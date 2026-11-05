namespace BayiOS.Modules.Integrations.Parsing;

// ------------------------------------------------------------------------------------------------------------------
// The parse contract (#46, ADR 0010 §2.2). What a parser — the Claude model or the deterministic rules — may say about
// a pasted WhatsApp message: only what the text itself contains. There is deliberately no field for a price, a stock
// level, a catalog id or an approval: a parser cannot express them, so nothing downstream can act on them. The JSON
// form is order-parse.schema.json (sent to the model as the structured-output format and enforced again on the
// server by ParsedOrderValidator).
// ------------------------------------------------------------------------------------------------------------------

/// <summary>One order line as written in the message.</summary>
/// <param name="RawText">The span of the message this line was read from (verbatim, trimmed).</param>
/// <param name="ProductCode">A product code / SKU / barcode written in the message, else null. Never invented.</param>
/// <param name="ProductText">The product as described (name, spec, model), without quantity and unit.</param>
/// <param name="Quantity">Positive quantity as written ("iki" → 2), null when the message gives none.</param>
/// <param name="Unit">Normalised unit code (<see cref="ParseUnits"/>), null when none is written.</param>
/// <param name="BrandModel">Brand / vehicle model / series mentioned for the product (e.g. "CG 125"), else null.</param>
/// <param name="Confidence">The parser's own certainty that this is an order line read correctly (0–1).</param>
/// <param name="Notes">Remarks the reviewer should see (e.g. a price expression, "geçen seferki gibi"), else null.</param>
public sealed record ParsedLine(
    string RawText,
    string? ProductCode,
    string ProductText,
    decimal? Quantity,
    string? Unit,
    string? BrandModel,
    decimal Confidence,
    string? Notes);

/// <summary>The whole parse of one message.</summary>
/// <param name="UnparsedFragments">Parts of the message that looked relevant but were not read as order lines.</param>
/// <param name="Warnings">Codes from <see cref="ParseWarnings.ParserCodes"/>.</param>
public sealed record ParsedOrder(
    IReadOnlyList<ParsedLine> Lines,
    IReadOnlyList<string> UnparsedFragments,
    IReadOnlyList<string> Warnings);

/// <summary>Unit codes a parser may emit (ASCII; Turkish spellings and abbreviations are normalised to these).</summary>
public static class ParseUnits
{
    public const string Adet = "adet";
    public const string Koli = "koli";
    public const string Kutu = "kutu";
    public const string Paket = "paket";
    public const string Takim = "takim";
    public const string Set = "set";
    public const string Metre = "metre";
    public const string Rulo = "rulo";
    public const string Cift = "cift";
    public const string Kg = "kg";
    public const string Litre = "litre";

    public static readonly IReadOnlyList<string> All = [Adet, Koli, Kutu, Paket, Takim, Set, Metre, Rulo, Cift, Kg, Litre];
}

/// <summary>Warning codes. Parser codes may come from the model; server codes are only ever added by BayiOS.</summary>
public static class ParseWarnings
{
    // ---- Parser codes (allowed in the model output) ----
    public const string PriceMentioned = "price_mentioned";
    public const string InstructionIgnored = "instruction_ignored";
    public const string QuantityMissing = "quantity_missing";
    public const string UnitUnclear = "unit_unclear";
    public const string CustomerInfoPresent = "customer_info_present";
    public const string DeliveryNote = "delivery_note";
    public const string ReferenceToPreviousOrder = "reference_to_previous_order";
    public const string UnreadableText = "unreadable_text";

    public static readonly IReadOnlyList<string> ParserCodes =
    [
        PriceMentioned, InstructionIgnored, QuantityMissing, UnitUnclear, CustomerInfoPresent, DeliveryNote,
        ReferenceToPreviousOrder, UnreadableText,
    ];

    // ---- Server codes ----

    /// <summary>The AI parser is switched off or has no API key; the rules parser was used.</summary>
    public const string AiDisabled = "ai_disabled";

    /// <summary>The tenant's daily AI quota is used up; the rules parser was used.</summary>
    public const string AiQuotaExceeded = "ai_quota_exceeded";

    /// <summary>The AI provider failed (error, timeout, refusal, truncated or invalid output); the rules parser was used.</summary>
    public const string AiUnavailable = "ai_unavailable";

    /// <summary>A model line whose text is not in the message was dropped (hallucination guard).</summary>
    public const string LineNotInMessage = "line_not_in_message";

    /// <summary>A product code the model reported that does not occur in the message was removed.</summary>
    public const string CodeNotInMessage = "code_not_in_message";

    /// <summary>The tenant catalog is larger than the matcher loads; fuzzy matching saw only part of it.</summary>
    public const string CatalogTruncated = "catalog_truncated";

    /// <summary>Phone numbers / e-mail addresses were masked before parsing (data-policy §3).</summary>
    public const string PersonalDataMasked = "personal_data_masked";

    public static readonly IReadOnlyList<string> ServerCodes =
    [
        AiDisabled, AiQuotaExceeded, AiUnavailable, LineNotInMessage, CodeNotInMessage, CatalogTruncated, PersonalDataMasked,
    ];
}
