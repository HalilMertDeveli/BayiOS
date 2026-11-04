using System.Net.Http.Json;

namespace BayiOS.IntegrationTests.Infrastructure;

internal static class IdempotentRequests
{
    public const string Header = "Idempotency-Key";

    /// <summary>POSTs JSON with an <c>Idempotency-Key</c> (a fresh one unless given).</summary>
    public static async Task<HttpResponseMessage> PostIdempotentAsync(
        this HttpClient client,
        string path,
        object body,
        string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add(Header, key ?? Guid.NewGuid().ToString());
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
