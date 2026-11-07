using System.Text.Json;
using BayiOS.Api.Platform.Observability;
using Microsoft.Extensions.Logging;

namespace BayiOS.UnitTests.Platform;

/// <summary>
/// The log redaction rule (data-policy §8.3, #55): the production JSON formatter never writes e-mail addresses,
/// tokens, Authorization/Cookie values, request bodies, raw paths/query strings or secrets — whatever a log call
/// passes in — and keeps the allowlisted fields (route, status, duration, tenant/user/correlation ids).
/// </summary>
public class LogRedactionTests
{
    // Built at runtime: a synthetic token, never a literal that secret scanners would have to triage.
    private static readonly string Jwt = string.Join('.', B64("{\"alg\":\"RS256\",\"kid\":\"test\"}"), B64("{\"sub\":\"1234567890\"}"), B64("signature-value-that-is-long"));
    private static readonly Guid TenantId = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");
    private static readonly Guid UserId = Guid.Parse("55512345-6789-4abc-8def-555123456789");

    [Theory]
    [InlineData("user ali.veli@example.com signed in", "ali.veli@example.com")]
    [InlineData("Authorization: Bearer {jwt}", "{jwt}")]
    [InlineData("token {jwt} rejected", "{jwt}")]
    [InlineData("Host=db;Username=bayios_app;Password=s3cr3t-P4ss;Database=bayios", "s3cr3t-P4ss")]
    [InlineData("GET https://x.supabase.co/storage/v1/object/sign/imports/a.xlsx?token=abc123SIGNED&expires=1", "abc123SIGNED")]
    [InlineData("api_key=sk-ant-test-0000 used", "sk-ant-test-0000")]
    [InlineData("Cookie: sb-access-token=zzzSECRETzzz", "zzzSECRETzzz")]
    [InlineData("call +90 555 000 00 12 now", "555 000 00 12")]
    [InlineData("call 0555-000-00-12 now", "0555-000-00-12")]
    [InlineData("call 5550000012 now", "5550000012")]
    public void Redact_removes_personal_data_and_secrets(string input, string secret)
    {
        input = input.Replace("{jwt}", Jwt, StringComparison.Ordinal);
        secret = secret.Replace("{jwt}", Jwt, StringComparison.Ordinal);
        var redacted = LogRedactor.Redact(input);

        Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("HTTP GET /api/v1/products/{id} responded 200 in 12.5 ms")]
    [InlineData("tenant 7c9e6679-7425-40de-944b-e07fc1f90ae7 user 55512345-6789-4abc-8def-555123456789")]
    [InlineData("correlation 0f8fad5bd9cb469fa16570867728950e")]
    [InlineData("Job 0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b (platform.queue_cleanup) attempt 2/5 failed")]
    public void Redact_keeps_allowlisted_operational_text(string input)
    {
        Assert.Equal(input, LogRedactor.Redact(input));
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("Cookie")]
    [InlineData("Email")]
    [InlineData("userEmail")]
    [InlineData("Phone")]
    [InlineData("password")]
    [InlineData("AccessToken")]
    [InlineData("invite_token")]
    [InlineData("RequestBody")]
    [InlineData("Body")]
    [InlineData("ApiKey")]
    [InlineData("ConnectionString")]
    [InlineData("DeliveryAddress")]
    [InlineData("Name")]
    public void Sensitive_field_names_are_recognized(string key)
    {
        Assert.True(LogRedactor.IsSensitiveKey(key));
    }

    [Theory]
    [InlineData("TenantId")]
    [InlineData("UserId")]
    [InlineData("CorrelationId")]
    [InlineData("Route")]
    [InlineData("StatusCode")]
    [InlineData("DurationMs")]
    [InlineData("JobType")]
    [InlineData("InputTokens")]
    public void Allowlisted_field_names_are_kept(string key)
    {
        Assert.False(LogRedactor.IsSensitiveKey(key));
    }

    [Fact]
    public void Formatter_redacts_state_scopes_message_and_exception_but_keeps_allowlisted_fields()
    {
        var scopes = new LoggerExternalScopeProvider();
        using var correlation = scopes.Push(new Dictionary<string, object> { ["CorrelationId"] = "0f8fad5bd9cb469fa16570867728950e" });
        using var hosting = scopes.Push(new Dictionary<string, object?>
        {
            ["RequestPath"] = "/api/v1/customers/lookup/ali.veli@example.com",
            ["QueryString"] = "?q=ali.veli@example.com",
            ["TraceId"] = "4bf92f3577b34da6a3ce929d0e0e4736",
        });
        using var plain = scopes.Push("HTTP GET /api/v1/customers?email=ali.veli@example.com");
        var state = new List<KeyValuePair<string, object?>>
        {
            new("Method", "GET"),
            new("Route", "/api/v1/products"),
            new("StatusCode", 500),
            new("DurationMs", 12.5),
            new("TenantId", TenantId),
            new("UserId", UserId),
            new("Authorization", "Bearer " + Jwt),
            new("Cookie", "sb=zzzSECRETzzz"),
            new("Email", "ali.veli@example.com"),
            new("Body", "{\"phone\":\"+90 555 000 00 12\"}"),
            new("Note", "contact ali.veli@example.com"),
            new("{OriginalFormat}", "HTTP {Method} {Route}"),
        };
        var exception = new InvalidOperationException("Duplicate customer ali.veli@example.com (Password=s3cr3t-P4ss)");

        var line = RedactingJsonConsoleFormatter.Format(
            LogLevel.Error,
            "BayiOS.Api.Platform.Observability.RequestLoggingMiddleware",
            new EventId(1),
            "HTTP GET /api/v1/products responded 500 for ali.veli@example.com with Bearer " + Jwt,
            exception,
            state,
            scopes,
            DateTimeOffset.UnixEpoch);

        foreach (var forbidden in new[] { "ali.veli@example.com", Jwt, "zzzSECRETzzz", "s3cr3t-P4ss", "555 000 00 12", "/customers", "?q=", "OriginalFormat" })
        {
            Assert.DoesNotContain(forbidden, line, StringComparison.Ordinal);
        }

        using var json = JsonDocument.Parse(line);
        var root = json.RootElement;
        Assert.Equal("Error", root.GetProperty("level").GetString());
        Assert.Equal("System.InvalidOperationException", root.GetProperty("exceptionType").GetString());
        var fields = root.GetProperty("fields");
        Assert.Equal("/api/v1/products", fields.GetProperty("Route").GetString());
        Assert.Equal(500, fields.GetProperty("StatusCode").GetInt32());
        Assert.Equal(12.5, fields.GetProperty("DurationMs").GetDouble());
        Assert.Equal(TenantId.ToString(), fields.GetProperty("TenantId").GetString());
        Assert.Equal(UserId.ToString(), fields.GetProperty("UserId").GetString());
        Assert.Equal("0f8fad5bd9cb469fa16570867728950e", fields.GetProperty("CorrelationId").GetString());
        Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", fields.GetProperty("TraceId").GetString());
        Assert.Equal(LogRedactor.Redacted, fields.GetProperty("Authorization").GetString());
        Assert.Equal(LogRedactor.Redacted, fields.GetProperty("Cookie").GetString());
        Assert.Equal(LogRedactor.Redacted, fields.GetProperty("Email").GetString());
        Assert.Equal(LogRedactor.Redacted, fields.GetProperty("Body").GetString());
        Assert.Equal("contact [email]", fields.GetProperty("Note").GetString());
        Assert.False(fields.TryGetProperty("RequestPath", out _));
        Assert.False(fields.TryGetProperty("QueryString", out _));
    }

    private static string B64(string text) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public void Formatter_writes_one_line_of_json()
    {
        var line = RedactingJsonConsoleFormatter.Format(
            LogLevel.Information, "c", default, "multi\nline", null, null, null, DateTimeOffset.UnixEpoch);

        Assert.DoesNotContain('\n', line);
        Assert.Equal("multi\nline", JsonDocument.Parse(line).RootElement.GetProperty("message").GetString());
    }
}
