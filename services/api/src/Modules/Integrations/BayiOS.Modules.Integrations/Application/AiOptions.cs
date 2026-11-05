using System.ComponentModel.DataAnnotations;

namespace BayiOS.Modules.Integrations.Application;

/// <summary>
/// <c>Ai:*</c> configuration of the WhatsApp order assistant (docs/operations/ai-cost-quality.md). The API key is
/// <c>Anthropic:ApiKey</c> (env <c>Anthropic__ApiKey</c>), read only on the server and never logged.
/// </summary>
public sealed class AiOptions
{
    public const string Section = "Ai";
    public const string ApiKeyKey = "Anthropic:ApiKey";

    /// <summary>Default model (Claude API model id, no date suffix). Changing it is an owner decision (cost/quality).</summary>
    public const string DefaultModel = "claude-opus-5-5";

    /// <summary>Feature flag: false (default) = the deterministic rules parser only; no provider call is ever made.</summary>
    public bool Enabled { get; set; }

    [Required]
    public string Model { get; set; } = DefaultModel;

    /// <summary>
    /// <c>output_config.effort</c>: low | medium | high | xhigh | max. Thinking cannot be disabled on Claude Opus 5.5;
    /// effort is the cost/latency control. Extraction is a narrow task, so the default is <c>low</c>.
    /// </summary>
    [Required]
    public string Effort { get; set; } = "low";

    /// <summary><c>max_tokens</c>: thinking counts toward it, so it leaves room beyond the JSON itself.</summary>
    [Range(1024, 64000)]
    public int MaxOutputTokens { get; set; } = 8000;

    /// <summary>Per attempt (SDK <c>Timeout</c>); a timed-out attempt is retried like a connection error.</summary>
    [Range(1, 120)]
    public int TimeoutSeconds { get; set; } = 20;

    /// <summary>SDK retries for connection errors, timeouts, 408/409/429/5xx (default 1: ≤ 2 attempts).</summary>
    [Range(0, 3)]
    public int MaxRetries { get; set; } = 1;

    /// <summary>Override of the Claude API base URL (tests / proxies only); null = SDK default.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Longest accepted message (characters, after trimming).</summary>
    [Range(100, 20000)]
    public int MaxInputChars { get; set; } = 4000;

    /// <summary>Most lines (newlines) a message may have.</summary>
    [Range(1, 500)]
    public int MaxInputLines { get; set; } = 150;

    /// <summary>Parse requests per tenant per clock hour (all parsers; threat-model §6.5: 30/hour). Over it: 429.</summary>
    [Range(1, 10000)]
    public int HourlyParseLimit { get; set; } = 30;

    /// <summary>AI calls per tenant per day (Europe/Istanbul). Over it the rules parser is used (no 429).</summary>
    [Range(0, 100000)]
    public int DailyCallLimit { get; set; } = 100;

    /// <summary>
    /// Billable-equivalent tokens per tenant per day (uncached input + cache writes + output; cache reads are not
    /// counted). Over it the rules parser is used.
    /// </summary>
    [Range(0, 1_000_000_000)]
    public long DailyTokenLimit { get; set; } = 300_000;

    /// <summary>How long the raw message text (and anything derived from it) is kept (data-policy §4: 30 days).</summary>
    [Range(1, 365)]
    public int TextRetentionDays { get; set; } = 30;

    /// <summary>Most active variants the matcher loads for fuzzy matching (exact codes are always looked up in full).</summary>
    [Range(100, 200000)]
    public int MatchCatalogLimit { get; set; } = 25000;
}
