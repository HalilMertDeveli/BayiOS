using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

namespace BayiOS.Api.Platform.Jobs;

/// <summary>
/// Turns a job/outbox failure into the text stored in <c>last_error</c> (#89, data-policy §8). The column is read by
/// operators across tenants, so it carries the exception type and a scrubbed message only:
/// <list type="bullet">
/// <item>PostgreSQL errors: SQLSTATE, the server's primary message (scrubbed) and the constraint/table identifiers —
/// never <c>Detail</c>/<c>Where</c>/<c>InternalQuery</c> (they echo row values) nor the command text or parameters.</item>
/// <item>Every message: quoted literals, e-mail addresses, URLs, IP addresses, tokens and long digit runs (phone, TCKN,
/// tax numbers) are replaced by markers; control characters removed; length capped.</item>
/// <item>Inner exceptions contribute only their type names.</item>
/// </list>
/// The full exception still goes to the structured log, which has its own redaction rules.
/// </summary>
internal static partial class JobErrorSanitizer
{
    public const int MaxLength = 500;

    public static string Sanitize(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var builder = new StringBuilder(exception.GetType().Name);
        var postgres = exception as PostgresException ?? exception.InnerException as PostgresException;
        if (postgres is not null)
        {
            builder.Append(CultureInfo.InvariantCulture, $" [{postgres.SqlState}]: ").Append(Scrub(postgres.MessageText));
            AppendIdentifier(builder, "constraint", postgres.ConstraintName);
            AppendIdentifier(builder, "table", postgres.TableName is null ? null : $"{postgres.SchemaName}.{postgres.TableName}");
        }
        else if (exception is NpgsqlException)
        {
            // Client-side failures (connection, timeout) name hosts and ports; the type is enough.
            builder.Append(": database error");
        }
        else
        {
            builder.Append(": ").Append(Scrub(exception.Message));
        }

        var inner = exception.InnerException;
        for (var depth = 0; inner is not null && depth < 3; depth++, inner = inner.InnerException)
        {
            builder.Append(" <- ").Append(inner.GetType().Name);
        }

        var text = builder.ToString();
        return text.Length <= MaxLength ? text : text[..(MaxLength - 1)] + "…";
    }

    /// <summary>Replaces values that may be personal data or secrets with markers.</summary>
    public static string Scrub(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        var text = new string([.. message.Select(c => char.IsControl(c) ? ' ' : c)]);
        text = Url().Replace(text, "[url]");
        text = Email().Replace(text, "[email]");
        text = Jwt().Replace(text, "[token]");
        text = Quoted().Replace(text, "[value]");
        text = IpAddress().Replace(text, "[ip]");
        text = LongToken().Replace(text, "[token]");
        text = Digits().Replace(text, "[number]");
        text = Whitespace().Replace(text, " ").Trim();
        return text.Length <= MaxLength ? text : text[..MaxLength];
    }

    private static void AppendIdentifier(StringBuilder builder, string name, string? value)
    {
        // Identifiers come from the schema, not from data; still keep only identifier characters.
        if (!string.IsNullOrEmpty(value) && Identifier().IsMatch(value))
        {
            builder.Append(' ').Append(name).Append('=').Append(value);
        }
    }

    [GeneratedRegex(@"\b[a-z][a-z0-9+.-]*://\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Url();

    [GeneratedRegex(@"[^\s@'""<>()]+@[^\s@'""<>()]+\.[a-z]{2,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    [GeneratedRegex(@"eyJ[\w-]+\.[\w-]+\.[\w-]*", RegexOptions.CultureInvariant)]
    private static partial Regex Jwt();

    /// <summary>'…', "…", `…` and «…» literals — PostgreSQL and .NET put input values there.</summary>
    [GeneratedRegex(@"'[^']*'|""[^""]*""|`[^`]*`|«[^»]*»", RegexOptions.CultureInvariant)]
    private static partial Regex Quoted();

    [GeneratedRegex(@"\b\d{1,3}(?:\.\d{1,3}){3}\b|\b(?:[0-9a-f]{0,4}:){2,7}[0-9a-f]{0,4}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IpAddress();

    /// <summary>Secrets, hashes and compact ids (32+ characters of a token alphabet).</summary>
    [GeneratedRegex(@"[A-Za-z0-9+/_=-]{32,}", RegexOptions.CultureInvariant)]
    private static partial Regex LongToken();

    /// <summary>Phone numbers, TCKN/VKN, card numbers: 5+ digits, optionally separated by spaces, dots or dashes.</summary>
    [GeneratedRegex(@"\+?\d(?:[\s.-]?\d){4,}", RegexOptions.CultureInvariant)]
    private static partial Regex Digits();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.$]{0,126}$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();
}
