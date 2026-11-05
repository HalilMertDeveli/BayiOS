using System.Text.Json;
using System.Text.RegularExpressions;
using BayiOS.IntegrationTests.Infrastructure;

namespace BayiOS.IntegrationTests.Security;

/// <summary>One operation of the running API, read from its OpenAPI document (<c>/openapi/v1.json</c>).</summary>
internal sealed record ApiRoute(
    string Method,
    string Template,
    string? Permission,
    IReadOnlyList<string> PathParameters,
    IReadOnlyList<string> QueryParameters,
    IReadOnlyList<string> RequestContentTypes)
{
    public string Key => $"{Method} {Template}";

    /// <summary>Tenant-scoped (needs <c>X-Tenant-Id</c> + a membership): every such endpoint declares a permission.</summary>
    public bool IsTenantScoped => Permission is not null;

    public HttpMethod HttpMethod => new(Method);

    public override string ToString() => Key;
}

/// <summary>
/// Reads the OpenAPI document the API serves, so the security sweeps cover every endpoint that exists — a new endpoint
/// is swept automatically or fails the sweep until it is classified (see the route tables in the sweeps).
/// </summary>
internal static partial class ApiRoutes
{
    private static readonly string[] Methods = ["get", "put", "post", "patch", "delete"];

    public static async Task<IReadOnlyList<ApiRoute>> LoadAsync(ApiFixture fixture)
    {
        using var client = fixture.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken));
        var routes = new List<ApiRoute>();
        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject().Where(o => Methods.Contains(o.Name)))
            {
                var parameters = operation.Value.TryGetProperty("parameters", out var p) ? p.EnumerateArray().ToList() : [];
                string[] In(string location) =>
                    [.. parameters.Where(x => x.GetProperty("in").GetString() == location).Select(x => x.GetProperty("name").GetString()!)];
                var contentTypes = operation.Value.TryGetProperty("requestBody", out var body) && body.TryGetProperty("content", out var content)
                    ? content.EnumerateObject().Select(c => c.Name).ToArray()
                    : [];
                var permission = operation.Value.TryGetProperty("x-permission", out var x) ? x.GetString() : null;

                // Path parameters come from the template too (some generators omit them from "parameters").
                var fromTemplate = PathParameter().Matches(path.Name).Select(m => m.Groups[1].Value).ToArray();
                routes.Add(new ApiRoute(operation.Name.ToUpperInvariant(), path.Name, permission, fromTemplate, In("query"), contentTypes));
            }
        }

        Assert.True(routes.Count > 50, $"OpenAPI document lists only {routes.Count} operations — is the document complete?");
        return routes;
    }

    /// <summary>
    /// Fills <c>{name}</c> placeholders. The callback gets the collection the parameter addresses: the literal segment
    /// before it, prefixed with the parent collection when nested (<c>picking-tasks/lines</c> for
    /// <c>/picking-tasks/{id}/lines/{lineId}</c>), and the parameter name.
    /// </summary>
    public static string Fill(string template, Func<string, string, string> value)
    {
        var original = template.Split('/');
        var segments = template.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var match = PathParameter().Match(segments[i]);
            if (match.Success)
            {
                var collection = i > 0 ? segments[i - 1] : string.Empty;
                if (i > 2 && PathParameter().IsMatch(original[i - 2]))
                {
                    collection = segments[i - 3] + '/' + collection;
                }

                segments[i] = value(collection, match.Groups[1].Value);
            }
        }

        return string.Join('/', segments);
    }

    [GeneratedRegex(@"\{([A-Za-z0-9_]+)(:[^}]*)?\}")]
    private static partial Regex PathParameter();
}
