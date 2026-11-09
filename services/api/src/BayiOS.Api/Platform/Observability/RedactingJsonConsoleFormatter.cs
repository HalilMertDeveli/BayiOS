using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace BayiOS.Api.Platform.Observability;

/// <summary>
/// One JSON object per line (stdout) for non-Development hosts (#55, observability.md §1). Every message, exception,
/// state field and scope field passes through <see cref="LogRedactor"/>; the request body is never logged by the API,
/// and fields named like secrets or personal data are written as <c>[redacted]</c>.
/// <code>{"timestamp":"…","level":"Information","category":"…","eventId":1,"message":"…","exception":"…","fields":{"CorrelationId":"…","TenantId":"…"}}</code>
/// </summary>
internal sealed class RedactingJsonConsoleFormatter() : ConsoleFormatter(FormatterName)
{
    public const string FormatterName = "bayios-json";

    private const string OriginalFormat = "{OriginalFormat}";

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);
        if (message is null && logEntry.Exception is null)
        {
            return;
        }

        textWriter.WriteLine(Format(logEntry.LogLevel, logEntry.Category, logEntry.EventId, message, logEntry.Exception, logEntry.State, scopeProvider, DateTimeOffset.UtcNow));
    }

    /// <summary>The formatted line (no trailing newline). Separate from <see cref="Write"/> for the redaction tests.</summary>
    public static string Format(
        LogLevel level,
        string category,
        EventId eventId,
        string? message,
        Exception? exception,
        object? state,
        IExternalScopeProvider? scopeProvider,
        DateTimeOffset timestamp)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (state is IEnumerable<KeyValuePair<string, object?>> properties)
        {
            foreach (var (key, value) in properties)
            {
                if (key != OriginalFormat)
                {
                    fields[key] = value;
                }
            }
        }

        scopeProvider?.ForEachScope(
            static (scope, target) =>
            {
                if (scope is IEnumerable<KeyValuePair<string, object?>> values)
                {
                    foreach (var (key, value) in values)
                    {
                        if (key != OriginalFormat)
                        {
                            target.TryAdd(key, value);
                        }
                    }
                }

                // Plain string scopes (e.g. hosting's "HTTP GET /path") are not written: they may carry raw paths.
            },
            fields);

        using var buffer = new MemoryStream();
        // Relaxed escaping keeps '+', quotes and non-ASCII (Turkish) readable; the output is a log stream, never HTML.
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("timestamp", timestamp.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("level", level.ToString());
            writer.WriteString("category", category);
            if (eventId.Id != 0)
            {
                writer.WriteNumber("eventId", eventId.Id);
            }

            if (message is not null)
            {
                writer.WriteString("message", LogRedactor.Redact(message));
            }

            if (exception is not null)
            {
                writer.WriteString("exceptionType", exception.GetType().FullName);
                writer.WriteString("exception", LogRedactor.Redact(exception.ToString()));
            }

            writer.WriteStartObject("fields");
            foreach (var (key, value) in fields)
            {
                if (LogRedactor.IsDropped(key))
                {
                    continue;
                }

                if (LogRedactor.IsSensitiveKey(key))
                {
                    writer.WriteString(key, LogRedactor.Redacted);
                    continue;
                }

                WriteValue(writer, key, value);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteValue(Utf8JsonWriter writer, string key, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNull(key);
                break;
            case bool b:
                writer.WriteBoolean(key, b);
                break;
            case int or long or short or byte or uint or ulong or ushort or sbyte:
                writer.WriteNumber(key, Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
            case double d when double.IsFinite(d):
                writer.WriteNumber(key, d);
                break;
            case float f when float.IsFinite(f):
                writer.WriteNumber(key, f);
                break;
            case decimal m:
                writer.WriteNumber(key, m);
                break;
            case Guid g:
                writer.WriteString(key, g);
                break;
            case DateTimeOffset dto:
                writer.WriteString(key, dto);
                break;
            case DateTime dt:
                writer.WriteString(key, dt);
                break;
            case TimeSpan ts:
                writer.WriteString(key, ts.ToString("c", CultureInfo.InvariantCulture));
                break;
            default:
                writer.WriteString(key, LogRedactor.Redact(Convert.ToString(value, CultureInfo.InvariantCulture)));
                break;
        }
    }
}

