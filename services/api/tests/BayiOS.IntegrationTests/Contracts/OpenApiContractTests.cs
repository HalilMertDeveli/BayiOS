using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BayiOS.IntegrationTests.Contracts;

/// <summary>
/// Keeps packages/contracts/openapi.json in sync with the running API.
/// Regenerate after an intentional API change with: UPDATE_CONTRACTS=1 dotnet test
/// </summary>
public class OpenApiContractTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    [Fact]
    public async Task Committed_contract_matches_generated_document()
    {
        var client = factory.CreateClient();
        var generated = JsonNode.Parse(await client.GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken))!;
        var contractPath = Path.Combine(FindRepoRoot(), "packages", "contracts", "openapi.json");

        if (Environment.GetEnvironmentVariable("UPDATE_CONTRACTS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(contractPath)!);
            await File.WriteAllTextAsync(contractPath, generated.ToJsonString(Indented) + "\n", TestContext.Current.CancellationToken);
        }

        Assert.True(File.Exists(contractPath), $"Missing {contractPath}. Run: UPDATE_CONTRACTS=1 dotnet test");
        var committed = JsonNode.Parse(await File.ReadAllTextAsync(contractPath, TestContext.Current.CancellationToken));
        Assert.True(
            JsonNode.DeepEquals(generated, committed),
            "packages/contracts/openapi.json is out of date. Run: UPDATE_CONTRACTS=1 dotnet test");
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "services", "api")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
