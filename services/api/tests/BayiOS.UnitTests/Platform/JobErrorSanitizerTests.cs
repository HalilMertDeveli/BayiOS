using System.Text.Json;
using BayiOS.Api.Platform.Audit;
using BayiOS.Api.Platform.Jobs;
using Npgsql;

namespace BayiOS.UnitTests.Platform;

/// <summary><c>last_error</c> sanitizer (#89, data-policy §8): exception type + scrubbed message, no values.</summary>
public class JobErrorSanitizerTests
{
    [Fact]
    public void Keeps_type_and_plain_message()
    {
        Assert.Equal("InvalidOperationException: synthetic failure", JobErrorSanitizer.Sanitize(new InvalidOperationException("synthetic failure")));
    }

    [Theory]
    [InlineData("Customer ayse.yilmaz@example.com not found", "ayse.yilmaz@example.com", "[email]")]
    [InlineData("Phone +90 555 123 45 67 is invalid", "555 123", "[number]")]
    [InlineData("TCKN 12345678901 rejected", "12345678901", "[number]")]
    [InlineData("Value 'Ahmet Yılmaz' is too long", "Ahmet", "[value]")]
    [InlineData("Column \"Mehmet\" mismatch", "Mehmet", "[value]")]
    [InlineData("GET https://x.supabase.co/storage/v1/object/sign/imports/a.xlsx?token=abc failed", "token=abc", "[url]")]
    [InlineData("Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2lnbmF0dXJl rejected", "eyJhbGci", "[token]")]
    [InlineData("Client 192.168.10.24 refused", "192.168.10.24", "[ip]")]
    [InlineData("Opaque value c3ludGhldGljLXRlc3Qtb25seS1ub3QtYS1zZWNyZXQ leaked", "c3ludGhldGlj", "[token]")]
    public void Scrubs_personal_data_and_secrets(string message, string forbidden, string marker)
    {
        var sanitized = JobErrorSanitizer.Sanitize(new ArgumentException(message));
        Assert.StartsWith("ArgumentException: ", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain(forbidden, sanitized, StringComparison.Ordinal);
        Assert.Contains(marker, sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void Postgres_error_keeps_sqlstate_and_identifiers_but_drops_detail_and_query()
    {
        var error = new PostgresException(
            messageText: "duplicate key value violates unique constraint \"ux_customers_tenant_code\"",
            severity: "ERROR",
            invariantSeverity: "ERROR",
            sqlState: PostgresErrorCodes.UniqueViolation,
            detail: "Key (tenant_id, code)=(5e0c…, ayse@example.com) already exists.",
            internalQuery: "INSERT INTO customers.customers (email) VALUES ('ayse@example.com')",
            where: "SQL statement \"INSERT … 'ayse@example.com'\"",
            schemaName: "customers",
            tableName: "customers",
            constraintName: "ux_customers_tenant_code");

        var sanitized = JobErrorSanitizer.Sanitize(error);

        Assert.StartsWith("PostgresException [23505]: duplicate key value violates unique constraint [value]", sanitized, StringComparison.Ordinal);
        Assert.Contains("constraint=ux_customers_tenant_code", sanitized, StringComparison.Ordinal);
        Assert.Contains("table=customers.customers", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("ayse", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("Key (", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void Wrapped_postgres_error_is_reported_through_its_wrapper()
    {
        var inner = new PostgresException("invalid input syntax for type uuid: \"0555 123 45 67\"", "ERROR", "ERROR", PostgresErrorCodes.InvalidTextRepresentation);
        var sanitized = JobErrorSanitizer.Sanitize(new InvalidOperationException("The import failed for 'Veli'.", inner));

        Assert.StartsWith("InvalidOperationException [22P02]: invalid input syntax for type uuid: [value]", sanitized, StringComparison.Ordinal);
        Assert.EndsWith("<- PostgresException", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("0555", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("Veli", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void Client_side_database_errors_do_not_name_hosts()
    {
        var sanitized = JobErrorSanitizer.Sanitize(new NpgsqlException("Failed to connect to db.internal.example:5432"));
        Assert.Equal("NpgsqlException: database error", sanitized);
    }

    [Fact]
    public void Inner_exceptions_contribute_type_names_only_and_length_is_capped()
    {
        var sanitized = JobErrorSanitizer.Sanitize(new InvalidOperationException(
            new string('x', 2000),
            new JsonException("Path $.customer.email: 'ayse@example.com'", new FormatException("bad 'Ali'"))));

        Assert.True(sanitized.Length <= JobErrorSanitizer.MaxLength, sanitized.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.DoesNotContain("ayse", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("Ali", sanitized, StringComparison.Ordinal);

        var short1 = JobErrorSanitizer.Sanitize(new InvalidOperationException("outer", new JsonException("'ayse'", new FormatException("'Ali'"))));
        Assert.Equal("InvalidOperationException: outer <- JsonException <- FormatException", short1);
    }

    [Fact]
    public void Control_characters_are_flattened()
    {
        Assert.Equal("FormatException: line one line two", JobErrorSanitizer.Sanitize(new FormatException("line one\r\n\tline two")));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("too-short-key", false)]
    [InlineData("0123456789abcdef0123456789abcde", false)] // 31 bytes
    [InlineData("0123456789abcdef0123456789abcdef", true)] // 32 bytes
    public void Ip_hash_key_is_absent_or_at_least_32_bytes(string? key, bool valid)
    {
        Assert.Equal(valid, AuditOptions.IsValidIpHashKey(key));
    }
}
