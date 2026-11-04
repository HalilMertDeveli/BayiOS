using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BayiOS.IntegrationTests.Platform;

public class PlatformEndpointsTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Health_returns_ok()
    {
        var response = await _client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(TestContext.Current.CancellationToken);
        Assert.Equal("ok", body!["status"]);
    }

    [Fact]
    public async Task Meta_returns_name_and_version()
    {
        var body = await _client.GetFromJsonAsync<Dictionary<string, string>>("/api/v1/meta", TestContext.Current.CancellationToken);

        Assert.Equal("BayiOS API", body!["name"]);
        Assert.False(string.IsNullOrWhiteSpace(body["version"]));
    }

    [Fact]
    public async Task Response_echoes_guid_correlation_id()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("X-Correlation-Id", "0f8fad5bd9cb469fa16570867728950e");

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("0f8fad5bd9cb469fa16570867728950e", response.Headers.GetValues("X-Correlation-Id").Single());
    }

    [Fact]
    public async Task Unknown_route_returns_problem_details()
    {
        var response = await _client.GetAsync("/api/v1/does-not-exist", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
}
