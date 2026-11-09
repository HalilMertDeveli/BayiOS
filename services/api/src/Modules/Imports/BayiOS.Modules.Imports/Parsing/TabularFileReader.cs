using System.Globalization;
using System.IO.Compression;
using System.Text;
using ExcelDataReader;

namespace BayiOS.Modules.Imports.Parsing;

/// <summary>
/// Safe reader for uploaded spreadsheets (threat-model §6.1–§6.2). Order of checks: extension and declared content
/// type → magic bytes → (xlsx) archive inspection with real decompressed sizes, macro and DTD rejection → parsing
/// with row/column/cell limits. Formulas are never evaluated: xlsx cells yield their cached value, CSV is plain text.
/// Accepted: <c>.xlsx</c> and <c>.csv</c> (UTF-8 with or without BOM, UTF-16 with BOM, otherwise Windows-1254;
/// delimiter <c>;</c>, <c>,</c> or tab detected from the header line).
/// </summary>
internal static class TabularFileReader
{
    private static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] OleMagic = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    private static readonly HashSet<string> MacroExtensions = new(StringComparer.OrdinalIgnoreCase) { ".xlsm", ".xltm", ".xlam" };

    private static readonly HashSet<string> XlsxContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/octet-stream",
        "application/zip",
        "application/x-zip-compressed",
    };

    private static readonly HashSet<string> CsvContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text/csv",
        "text/plain",
        "text/comma-separated-values",
        "application/csv",
        "application/vnd.ms-excel", // what Windows browsers send for .csv
        "application/octet-stream",
    };

    static TabularFileReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>Validates and parses an upload. Throws <see cref="ImportFileRejectedException"/> for any refused file.</summary>
    public static ParsedTable Read(byte[] content, string fileName, string? contentType, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ImportLimits.ParseTimeout);
        try
        {
            return ReadCore(content, fileName, contentType, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw ImportFileRejectedException.Invalid("imports.file_parse_timeout", "The file took too long to read.");
        }
    }

    /// <summary>The format the upload must be, from its name and declared type; magic bytes are checked separately.</summary>
    public static string DetectFormat(string fileName, string? contentType)
    {
        var extension = Path.GetExtension(fileName);
        var mediaType = MediaType(contentType);
        if (MacroExtensions.Contains(extension) || (mediaType?.Contains("macroenabled", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            throw ImportFileRejectedException.MacroNotAllowed();
        }

        var format = extension.ToLowerInvariant() switch
        {
            ".xlsx" => FileFormats.Xlsx,
            ".csv" or ".txt" => FileFormats.Csv,
            ".xls" => throw ImportFileRejectedException.TypeNotAllowed("Legacy .xls files are not accepted; save the file as .xlsx or .csv."),
            _ => throw ImportFileRejectedException.TypeNotAllowed("Only .xlsx and .csv files are accepted."),
        };

        var allowed = format == FileFormats.Xlsx ? XlsxContentTypes : CsvContentTypes;
        if (mediaType is not null && !allowed.Contains(mediaType))
        {
            throw ImportFileRejectedException.TypeNotAllowed($"Content type '{mediaType}' does not match a .{format} file.");
        }

        return format;
    }

    private static ParsedTable ReadCore(byte[] content, string fileName, string? contentType, CancellationToken cancellationToken)
    {
        var format = DetectFormat(fileName, contentType);
        if (content.Length == 0)
        {
            throw ImportFileRejectedException.Invalid("imports.file_empty", "The file is empty.");
        }

        if (content.LongLength > ImportLimits.MaxFileBytes)
        {
            throw ImportFileRejectedException.TooLarge();
        }

        if (format == FileFormats.Xlsx)
        {
            if (!content.AsSpan().StartsWith(ZipMagic))
            {
                throw ImportFileRejectedException.ContentMismatch("The file is not a valid .xlsx workbook.");
            }

            InspectXlsxArchive(content, cancellationToken);
            return ReadXlsx(content, cancellationToken);
        }

        if (content.AsSpan().StartsWith(ZipMagic) || content.AsSpan().StartsWith(OleMagic) || LooksBinary(content))
        {
            throw ImportFileRejectedException.ContentMismatch("The file is not a text CSV file.");
        }

        return ReadCsv(content, cancellationToken);
    }

    // ---------------------------------------------------------------- xlsx

    /// <summary>
    /// Zip-bomb and active-content checks on the real archive: entry count, actual decompressed size per entry and in
    /// total (declared sizes can lie), compression ratio, duplicate names, VBA/ActiveX parts, macro content types and
    /// XML DTDs (XXE). Every entry is decompressed once here with hard limits before the parser opens the file.
    /// </summary>
    internal static void InspectXlsxArchive(byte[] content, CancellationToken cancellationToken)
    {
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(new MemoryStream(content, writable: false), ZipArchiveMode.Read);
        }
        catch (InvalidDataException)
        {
            throw ImportFileRejectedException.ContentMismatch("The file is not a valid .xlsx workbook.");
        }

        using (archive)
        {
            if (archive.Entries.Count > ImportLimits.MaxZipEntries)
            {
                throw ImportFileRejectedException.ArchiveLimits($"The workbook has more than {ImportLimits.MaxZipEntries} parts.");
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hasWorkbook = false;
            long total = 0;
            var buffer = new byte[81920];
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = entry.FullName.Replace('\\', '/');
                if (!names.Add(name))
                {
                    throw ImportFileRejectedException.ArchiveLimits("The workbook contains duplicate parts.");
                }

                if (name.Contains("vbaproject", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("xl/activeX/", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                {
                    throw ImportFileRejectedException.MacroNotAllowed();
                }

                hasWorkbook |= name.Equals("xl/workbook.xml", StringComparison.OrdinalIgnoreCase);
                if (entry.Length > ImportLimits.MaxZipEntryBytes)
                {
                    throw ImportFileRejectedException.ArchiveLimits("A workbook part is too large when decompressed.");
                }

                var isXml = name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase);
                var isContentTypes = name.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase);
                using var head = new MemoryStream();
                long size = 0;
                try
                {
                    using var stream = entry.Open();
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        size += read;
                        if (size > ImportLimits.MaxZipEntryBytes || total + size > ImportLimits.MaxZipTotalBytes)
                        {
                            throw ImportFileRejectedException.ArchiveLimits("The workbook is too large when decompressed.");
                        }

                        // Keep the start of XML parts (DTD check) and all of [Content_Types].xml (macro check, small).
                        if (isXml && (isContentTypes ? head.Length < 1024 * 1024 : head.Length < 4096))
                        {
                            head.Write(buffer, 0, read);
                        }
                    }
                }
                catch (InvalidDataException)
                {
                    throw ImportFileRejectedException.ContentMismatch("The workbook archive is corrupt.");
                }

                if (size > ImportLimits.RatioCheckThresholdBytes && size > Math.Max(entry.CompressedLength, 1) * ImportLimits.MaxCompressionRatio)
                {
                    throw ImportFileRejectedException.ArchiveLimits("A workbook part is compressed suspiciously well (zip bomb).");
                }

                total += size;
                if (isXml)
                {
                    var text = Encoding.UTF8.GetString(head.GetBuffer(), 0, (int)head.Length);
                    if (text.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) || text.Contains("<!ENTITY", StringComparison.OrdinalIgnoreCase))
                    {
                        throw ImportFileRejectedException.Invalid("imports.file_xml_dtd_not_allowed", "The workbook contains XML document type declarations.");
                    }

                    if (isContentTypes && text.Contains("macroEnabled", StringComparison.OrdinalIgnoreCase))
                    {
                        throw ImportFileRejectedException.MacroNotAllowed();
                    }
                }
            }

            if (total > ImportLimits.RatioCheckThresholdBytes && total > (long)content.Length * ImportLimits.MaxCompressionRatio)
            {
                throw ImportFileRejectedException.ArchiveLimits("The workbook is compressed suspiciously well (zip bomb).");
            }

            if (!hasWorkbook)
            {
                throw ImportFileRejectedException.ContentMismatch("The file is not an Excel workbook.");
            }
        }
    }

    private static ParsedTable ReadXlsx(byte[] content, CancellationToken cancellationToken)
    {
        var rows = new List<(int RowNumber, string?[] Cells)>();
        string? sheetName;
        try
        {
            using var stream = new MemoryStream(content, writable: false);
            using var reader = ExcelReaderFactory.CreateOpenXmlReader(stream);
            sheetName = reader.Name;
            var rowNumber = 0;
            var nonEmpty = 0;
            while (reader.Read())
            {
                rowNumber++;
                if ((rowNumber & 0xFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var width = reader.FieldCount;
                var cells = new string?[Math.Min(width, ImportLimits.MaxColumns)];
                for (var i = 0; i < width; i++)
                {
                    var text = CellToText(reader.GetValue(i));
                    if (i >= ImportLimits.MaxColumns)
                    {
                        if (text is not null)
                        {
                            throw TooManyColumns();
                        }

                        continue;
                    }

                    cells[i] = text;
                }

                var hasValue = Array.Exists(cells, c => c is not null);
                if (rowNumber == 1 || hasValue)
                {
                    rows.Add((rowNumber, cells));
                }

                if (hasValue && ++nonEmpty > ImportLimits.MaxDataRows + 1)
                {
                    throw TooManyRows();
                }
            }
        }
        catch (Exception ex) when (ex is not (ImportFileRejectedException or OperationCanceledException))
        {
            // Malformed parts, unsupported features: the parser's message may echo content, so it is not surfaced.
            throw ImportFileRejectedException.Invalid("imports.file_unreadable", "The workbook could not be read.");
        }

        return Build(FileFormats.Xlsx, null, null, sheetName, rows);
    }

    /// <summary>Cell value → trimmed text. Numbers are formatted invariantly without exponent; dates as ISO.</summary>
    internal static string? CellToText(object? value)
    {
        var text = value switch
        {
            null or DBNull => null,
            string s => s,
            double d when double.IsFinite(d) && Math.Abs(d) < 7.9e27 => ((decimal)d).ToString("0.############################", CultureInfo.InvariantCulture),
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            DateTime dt => dt.TimeOfDay == TimeSpan.Zero
                ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
        text = text?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    // ---------------------------------------------------------------- csv

    private static ParsedTable ReadCsv(byte[] content, CancellationToken cancellationToken)
    {
        var (text, encodingName) = Decode(content);
        if (text.Contains('\0', StringComparison.Ordinal))
        {
            throw ImportFileRejectedException.ContentMismatch("The file is not a text CSV file.");
        }

        var delimiter = DetectDelimiter(text);
        var rows = new List<(int RowNumber, string?[] Cells)>();
        var fields = new List<string?>();
        var field = new StringBuilder();
        var inQuotes = false;
        var fieldWasQuoted = false;
        var record = 0;
        var nonEmpty = 0;

        void EndField()
        {
            var value = fieldWasQuoted ? field.ToString() : field.ToString().Trim();
            fields.Add(value.Length == 0 ? null : value.Trim());
            field.Clear();
            fieldWasQuoted = false;
        }

        void EndRecord()
        {
            EndField();
            record++;
            if (fields.Count > ImportLimits.MaxColumns && fields.Skip(ImportLimits.MaxColumns).Any(f => f is not null))
            {
                throw TooManyColumns();
            }

            var cells = fields.Take(ImportLimits.MaxColumns).ToArray();
            var hasValue = Array.Exists(cells, c => c is not null);
            if (record == 1 || hasValue)
            {
                rows.Add((record, cells));
            }

            fields.Clear();
            if ((record & 0xFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (hasValue && ++nonEmpty > ImportLimits.MaxDataRows + 1)
            {
                throw TooManyRows();
            }
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                if (field.Length > ImportLimits.MaxCellLength)
                {
                    throw CellTooLong(record + 1);
                }

                continue;
            }

            if (c == '"' && field.ToString().Trim().Length == 0)
            {
                field.Clear();
                inQuotes = true;
                fieldWasQuoted = true;
            }
            else if (c == delimiter)
            {
                EndField();
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                EndRecord();
            }
            else
            {
                field.Append(c);
                if (field.Length > ImportLimits.MaxCellLength)
                {
                    throw CellTooLong(record + 1);
                }
            }
        }

        if (inQuotes)
        {
            throw ImportFileRejectedException.Invalid("imports.file_unreadable", "The CSV file has an unterminated quoted value.");
        }

        if (field.Length > 0 || fields.Count > 0 || fieldWasQuoted)
        {
            EndRecord();
        }

        return Build(FileFormats.Csv, encodingName, delimiter.ToString(), null, rows);
    }

    /// <summary>BOM → UTF-8/UTF-16; otherwise strict UTF-8, falling back to Windows-1254 (Turkish ANSI).</summary>
    internal static (string Text, string EncodingName) Decode(byte[] content)
    {
        var span = content.AsSpan();
        if (span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return (StrictUtf8(content, 3), "utf-8");
        }

        if (span.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return (Encoding.Unicode.GetString(content, 2, content.Length - 2), "utf-16");
        }

        if (span.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return (Encoding.BigEndianUnicode.GetString(content, 2, content.Length - 2), "utf-16");
        }

        try
        {
            return (StrictUtf8(content, 0), "utf-8");
        }
        catch (DecoderFallbackException)
        {
            return (Encoding.GetEncoding(1254).GetString(content), "windows-1254");
        }
    }

    private static string StrictUtf8(byte[] content, int offset)
    {
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(content, offset, content.Length - offset);
        }
        catch (DecoderFallbackException) when (offset > 0)
        {
            throw ImportFileRejectedException.Invalid("imports.file_unreadable", "The file declares UTF-8 but contains invalid characters.");
        }
    }

    /// <summary>The most frequent of <c>;</c> <c>,</c> tab on the header line (outside quotes); ties prefer <c>;</c>.</summary>
    internal static char DetectDelimiter(string text)
    {
        int semicolons = 0, commas = 0, tabs = 0;
        var inQuotes = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (!inQuotes && c is '\r' or '\n')
            {
                break;
            }
            else if (!inQuotes)
            {
                semicolons += c == ';' ? 1 : 0;
                commas += c == ',' ? 1 : 0;
                tabs += c == '\t' ? 1 : 0;
            }
        }

        if (semicolons >= commas && semicolons >= tabs && semicolons > 0)
        {
            return ';';
        }

        return tabs > commas ? '\t' : ',';
    }

    private static bool LooksBinary(byte[] content)
    {
        // UTF-16 text legitimately contains NUL bytes; it must carry a BOM to be accepted.
        if (content.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) || content.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return false;
        }

        var head = content.AsSpan(0, Math.Min(content.Length, 8192));
        return head.Contains((byte)0)
            || head.StartsWith("%PDF"u8)
            || head.StartsWith("MZ"u8)
            || head.StartsWith((ReadOnlySpan<byte>)[0x1F, 0x8B]);
    }

    // ---------------------------------------------------------------- shared

    /// <summary>First row = headers (unique, never empty); empty data rows are dropped; limits enforced.</summary>
    private static ParsedTable Build(string format, string? encoding, string? delimiter, string? sheetName, List<(int RowNumber, string?[] Cells)> rows)
    {
        if (rows.Count == 0 || rows[0].Cells.All(c => c is null))
        {
            throw ImportFileRejectedException.Invalid("imports.file_empty", "The first row must contain the column headers.");
        }

        var rawHeaders = rows[0].Cells;
        var width = Array.FindLastIndex(rawHeaders, h => h is not null) + 1;
        var headers = new List<string>(width);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < width; i++)
        {
            var header = string.Join(' ', (rawHeaders[i] ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (header.Length > ImportLimits.MaxHeaderLength)
            {
                header = header[..ImportLimits.MaxHeaderLength];
            }

            if (header.Length == 0)
            {
                header = $"Sütun {i + 1}";
            }

            var unique = header;
            for (var n = 2; !seen.Add(unique); n++)
            {
                unique = $"{header} ({n})";
            }

            headers.Add(unique);
        }

        var data = new List<ParsedRow>();
        foreach (var (rowNumber, cells) in rows.Skip(1))
        {
            var normalized = new string?[width];
            var any = false;
            for (var i = 0; i < width && i < cells.Length; i++)
            {
                var value = cells[i];
                if (value is { Length: > ImportLimits.MaxCellLength })
                {
                    throw CellTooLong(rowNumber);
                }

                normalized[i] = value;
                any |= value is not null;
            }

            if (any)
            {
                data.Add(new ParsedRow(rowNumber, normalized));
            }
        }

        if (data.Count > ImportLimits.MaxDataRows)
        {
            throw TooManyRows();
        }

        if (data.Count == 0)
        {
            throw ImportFileRejectedException.Invalid("imports.file_empty", "The file has no data rows.");
        }

        return new ParsedTable(format, encoding, delimiter, sheetName, headers, data);
    }

    private static string? MediaType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        var semicolon = contentType.IndexOf(';', StringComparison.Ordinal);
        return (semicolon >= 0 ? contentType[..semicolon] : contentType).Trim();
    }

    private static ImportFileRejectedException TooManyRows() =>
        ImportFileRejectedException.Invalid("imports.file_too_many_rows", $"The file has more than {ImportLimits.MaxDataRows} data rows.");

    private static ImportFileRejectedException TooManyColumns() =>
        ImportFileRejectedException.Invalid("imports.file_too_many_columns", $"The file has more than {ImportLimits.MaxColumns} columns.");

    private static ImportFileRejectedException CellTooLong(int rowNumber) =>
        ImportFileRejectedException.Invalid(
            "imports.file_cell_too_long", $"Row {rowNumber.ToString(CultureInfo.InvariantCulture)} has a cell longer than {ImportLimits.MaxCellLength} characters.");
}
