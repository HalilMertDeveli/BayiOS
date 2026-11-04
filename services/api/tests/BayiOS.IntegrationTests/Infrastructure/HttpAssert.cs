using System.Net;
using System.Text.Json;

namespace BayiOS.IntegrationTests.Infrastructure;

internal static class HttpAssert
{
    /// <summary>Asserts an RFC 9457 response with the given status and stable <c>code</c>.</summary>
    public static async Task ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
        Assert.Equal("https://bayios.app/problems/" + code, body.RootElement.GetProperty("type").GetString());
        Assert.True(body.RootElement.TryGetProperty("correlationId", out _));
    }
}
