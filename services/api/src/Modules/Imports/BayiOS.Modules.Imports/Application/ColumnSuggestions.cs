using BayiOS.Modules.Imports.Contracts;
using BayiOS.Modules.Imports.Parsing;

namespace BayiOS.Modules.Imports.Application;

/// <summary>
/// Deterministic column suggestion (no AI/LLM): Turkish and English header synonyms, then a sample-value heuristic
/// for barcodes. Each field and each header is suggested at most once, best score first.
/// </summary>
internal sealed class HeaderSynonymSuggestionProvider : IColumnSuggestionProvider
{
    public const decimal ExactScore = 0.95m;
    public const decimal PartialScore = 0.7m;
    public const decimal SampleScore = 0.5m;

    public Task<IReadOnlyList<ColumnSuggestion>> SuggestAsync(ColumnSuggestionContext context, CancellationToken cancellationToken)
    {
        var fields = ImportFields.For(context.ImportType).Where(f => context.Fields.Any(i => i.Key == f.Key)).ToList();
        var folded = context.Headers.Select(ImportValues.Fold).ToList();
        var candidates = new List<(int FieldOrder, int HeaderIndex, decimal Score, string Source)>();
        for (var f = 0; f < fields.Count; f++)
        {
            for (var h = 0; h < folded.Count; h++)
            {
                var header = folded[h];
                if (header.Length == 0)
                {
                    continue;
                }

                if (fields[f].FoldedSynonyms.Contains(header))
                {
                    candidates.Add((f, h, ExactScore, "synonym"));
                }
                else if (fields[f].FoldedSynonyms.Any(s => (s.Length >= 4 && header.Contains(s, StringComparison.Ordinal))
                    || (header.Length >= 4 && s.Contains(header, StringComparison.Ordinal))))
                {
                    candidates.Add((f, h, PartialScore, "partial"));
                }
            }

            if (fields[f].Key == ImportFields.Barcode)
            {
                for (var h = 0; h < folded.Count; h++)
                {
                    var samples = context.SampleRows.Select(r => h < r.Count ? r[h] : null).Where(v => v is not null).ToList();
                    if (samples.Count > 0 && samples.All(v => v is { Length: 8 or 12 or 13 or 14 } && v.All(char.IsAsciiDigit)))
                    {
                        candidates.Add((f, h, SampleScore, "sample_values"));
                    }
                }
            }
        }

        var usedFields = new HashSet<int>();
        var usedHeaders = new HashSet<int>();
        var result = new List<ColumnSuggestion>();
        foreach (var (fieldOrder, headerIndex, score, source) in candidates.OrderByDescending(c => c.Score).ThenBy(c => c.FieldOrder).ThenBy(c => c.HeaderIndex))
        {
            if (usedFields.Contains(fieldOrder) || usedHeaders.Contains(headerIndex))
            {
                continue;
            }

            usedFields.Add(fieldOrder);
            usedHeaders.Add(headerIndex);
            result.Add(new ColumnSuggestion(fields[fieldOrder].Key, context.Headers[headerIndex], score, source));
        }

        return Task.FromResult<IReadOnlyList<ColumnSuggestion>>([.. result.OrderBy(s => fields.FindIndex(f => f.Key == s.Field))]);
    }
}
