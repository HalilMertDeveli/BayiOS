using System.Globalization;
using System.Text.Json;
using Anthropic;
using BayiOS.Modules.Integrations.Application;
using BayiOS.Modules.Integrations.Matching;
using BayiOS.Modules.Integrations.Parsing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BayiOS.UnitTests.Integrations;

/// <summary>
/// Opt-in evaluation of the real Claude parser on the synthetic set (#49/#50). Never runs in CI: it is skipped unless
/// <c>BAYIOS_AI_EVAL=1</c> and <c>Anthropic__ApiKey</c> are set in the environment (owner-approved key, synthetic data
/// only). Prints the same metrics as the deterministic run plus measured tokens and cost.
/// <code>
/// BAYIOS_AI_EVAL=1 Anthropic__ApiKey=… dotnet test services/api/tests/BayiOS.UnitTests --filter-class "*LiveClaudeEvaluationTests"
/// </code>
/// Optional: <c>Ai__Model</c> (default claude-opus-5-5), <c>Ai__Effort</c> (default low), <c>BAYIOS_AI_EVAL_LIMIT</c>
/// (first N messages). Prices are list prices per million tokens (docs/operations/ai-cost-quality.md §2).
/// </summary>
public sealed class LiveClaudeEvaluationTests
{
    private static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [Fact]
    public async Task Claude_parser_on_the_evaluation_set()
    {
        var key = Environment.GetEnvironmentVariable("Anthropic__ApiKey");
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("BAYIOS_AI_EVAL") == "1" && !string.IsNullOrWhiteSpace(key),
            "Live AI evaluation is opt-in: set BAYIOS_AI_EVAL=1 and Anthropic__ApiKey.");

        var settings = new AiOptions
        {
            Enabled = true,
            Model = Environment.GetEnvironmentVariable("Ai__Model") ?? AiOptions.DefaultModel,
            Effort = Environment.GetEnvironmentVariable("Ai__Effort") ?? "low",
            TimeoutSeconds = 60,
            MaxRetries = 2,
        };
        var client = new AnthropicClient { ApiKey = key, Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds), MaxRetries = settings.MaxRetries };
        var parser = new AnthropicOrderParser(client, Options.Create(settings), NullLogger<AnthropicOrderParser>.Instance);
        var matcher = new CatalogMatcher(EvalDataset.Catalog);
        var limit = int.TryParse(Environment.GetEnvironmentVariable("BAYIOS_AI_EVAL_LIMIT"), CultureInfo.InvariantCulture, out var n) ? n : int.MaxValue;

        var runs = new List<(EvalMessage, OrderReview)>();
        var usage = AiTokenUsage.None;
        var failures = new Dictionary<string, int>();
        foreach (var message in EvalDataset.Messages.Take(limit))
        {
            var masked = TurkishText.MaskPersonalData(message.Text, out _);
            var result = await parser.ParseAsync(masked, TestContext.Current.CancellationToken);
            usage = new AiTokenUsage(
                usage.InputTokens + result.Usage.InputTokens,
                usage.OutputTokens + result.Usage.OutputTokens,
                usage.CacheReadInputTokens + result.Usage.CacheReadInputTokens,
                usage.CacheCreationInputTokens + result.Usage.CacheCreationInputTokens);
            OrderReview review;
            if (result.Parse is null)
            {
                failures[result.FailureReason!] = failures.GetValueOrDefault(result.FailureReason!) + 1;
                review = OrderParseReview.Build(RuleBasedOrderParser.Parse(masked), masked, matcher, fromModel: false);
            }
            else
            {
                review = OrderParseReview.Build(result.Parse, AnthropicOrderParser.Neutralise(masked), matcher, fromModel: true);
            }

            runs.Add((message, review));
        }

        var report = OrderParseEvaluator.Evaluate($"{settings.Model}/{settings.Effort}", runs);
        var cost = Cost(usage);
        var output = TestContext.Current.TestOutputHelper;
        output?.WriteLine(report.Summary);
        output?.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  tokens: input={usage.InputTokens} output={usage.OutputTokens} cacheRead={usage.CacheReadInputTokens} cacheWrite={usage.CacheCreationInputTokens}"));
        output?.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  cost: ${cost:F4} total, ${cost / Math.Max(1, runs.Count):F5} per message"));
        output?.WriteLine("  provider failures (fell back to rules): " + (failures.Count == 0 ? "none" : string.Join(", ", failures.Select(f => $"{f.Key}={f.Value}"))));
        foreach (var detail in report.WrongMatchDetails.Concat(report.Misses))
        {
            output?.WriteLine("  " + detail);
        }

        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "eval-report-ai.json"), JsonSerializer.Serialize(new { report, usage, cost }, Indented));

        // The safety property holds for the model path as well: grounding + deterministic matching.
        Assert.True(report.WrongMatches == 0, string.Join('\n', report.WrongMatchDetails));
    }

    /// <summary>List prices (USD per MTok) of the configured model; unknown models are priced as Claude Opus 5.5.</summary>
    private static decimal Cost(AiTokenUsage usage)
    {
        var model = Environment.GetEnvironmentVariable("Ai__Model") ?? AiOptions.DefaultModel;
        var (input, output, cacheRead, cacheWrite) = model.StartsWith("claude-haiku-5-5", StringComparison.Ordinal)
            ? (0.10m, 0.50m, 0.01m, 0.125m)
            : (4m, 20m, 0.20m, 5m);
        return ((usage.InputTokens * input) + (usage.OutputTokens * output) + (usage.CacheReadInputTokens * cacheRead)
            + (usage.CacheCreationInputTokens * cacheWrite)) / 1_000_000m;
    }
}
