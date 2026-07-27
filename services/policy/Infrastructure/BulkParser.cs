using System.Globalization;
using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;
using Mersal.Policy.Domain;

namespace Mersal.Policy.Infrastructure;

/// <summary>
/// Phase 19.5b — parse an uploaded CSV or XLSX against a job type's explicit column contract.
///
/// <para>Both formats go through the SAME header check and the SAME cell rules (<see cref="BulkCells"/>). A
/// date that reads as 3 January from a spreadsheet and 1 March from a CSV is a defect that only appears in
/// production, on somebody's cover.</para>
/// </summary>
public interface IBulkFileParser
{
    ParseResult Parse(BulkTemplate template, string fileName, byte[] content);
}

public sealed class BulkFileParser : IBulkFileParser
{
    /// <summary>A ceiling on rows, not on bytes. It is a safety limit on how much one operator action can
    /// change at once — well above the 50 000-row job the build prompt sizes for, and far below "the whole
    /// membership by accident".</summary>
    public const int MaxRows = 200_000;

    public ParseResult Parse(BulkTemplate template, string fileName, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(content);

        if (content.Length == 0)
            return ParseResult.Failed(new ParseFailure("EMPTY_FILE", "The file is empty.", "الملف فارغ."));

        var extension = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        try
        {
            return extension switch
            {
                ".csv" or ".txt" => ParseCsv(template, content),
                ".xlsx" => ParseXlsx(template, content),
                _ => ParseResult.Failed(new ParseFailure("UNSUPPORTED_FORMAT",
                    $"'{extension}' is not a supported upload format; use .csv or .xlsx.",
                    $"الصيغة '{extension}' غير مدعومة؛ استخدم .csv أو .xlsx.")),
            };
        }
        catch (Exception ex) when (ex is CsvHelperException or InvalidDataException or IOException or FormatException)
        {
            // A file we cannot read is a WHOLE-FILE failure, never a set of row errors — reporting "row 1
            // invalid" for a corrupt archive sends an operator to fix a line that was never read.
            return ParseResult.Failed(new ParseFailure("UNREADABLE_FILE",
                $"The file could not be read: {ex.Message}",
                "تعذّرت قراءة الملف؛ تأكد من أنه ملف CSV أو XLSX سليم."));
        }
    }

    private static ParseResult ParseCsv(BulkTemplate template, byte[] content)
    {
        using var stream = new MemoryStream(content, writable: false);
        using var reader = new StreamReader(stream);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            // Comment lines are how the downloadable template carries its legend; a returned template must
            // parse without the operator having to strip anything.
            AllowComments = true,
            Comment = '#',
            IgnoreBlankLines = true,
            DetectColumnCountChanges = false,
            MissingFieldFound = null,
            BadDataFound = null,
            TrimOptions = TrimOptions.Trim,
        });

        if (!csv.Read() || !csv.ReadHeader() || csv.HeaderRecord is not { Length: > 0 } header)
            return ParseResult.Failed(new ParseFailure("NO_HEADER",
                "The file has no header row.", "لا يحتوي الملف على صف عناوين."));

        if (BulkHeaderContract.Check(template, header) is { } failure) return ParseResult.Failed(failure);

        var map = BuildMap(template, header);
        var rows = new List<ParsedRow>();
        var rowNumber = 0;
        while (csv.Read())
        {
            rowNumber++;
            if (rowNumber > MaxRows) return TooManyRows();

            var cells = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var (canonical, index) in map)
                cells[canonical] = index < csv.Parser.Count ? csv.Parser[index] : null;

            if (cells.Values.All(string.IsNullOrWhiteSpace)) { rowNumber--; continue; }   // trailing blank line
            rows.Add(new ParsedRow(rowNumber, cells));
        }

        return new ParseResult(rows, null);
    }

    private static ParseResult ParseXlsx(BulkTemplate template, byte[] content)
    {
        using var stream = new MemoryStream(content, writable: false);
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheets.FirstOrDefault();
        if (sheet is null)
            return ParseResult.Failed(new ParseFailure("NO_SHEET",
                "The workbook has no sheets.", "لا يحتوي المصنّف على أوراق عمل."));

        var used = sheet.RangeUsed();
        if (used is null)
            return ParseResult.Failed(new ParseFailure("NO_HEADER",
                "The sheet is empty.", "ورقة العمل فارغة."));

        var headerRow = used.FirstRow();
        var header = headerRow.Cells().Select(c => c.GetString()).ToList();
        if (BulkHeaderContract.Check(template, header) is { } failure) return ParseResult.Failed(failure);

        var map = BuildMap(template, header);
        var rows = new List<ParsedRow>();
        var rowNumber = 0;
        foreach (var row in used.RowsUsed().Skip(1))
        {
            rowNumber++;
            if (rowNumber > MaxRows) return TooManyRows();

            var cells = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var (canonical, index) in map)
            {
                var cell = row.Cell(index + 1);
                cells[canonical] = CellText(cell);
            }

            if (cells.Values.All(string.IsNullOrWhiteSpace)) { rowNumber--; continue; }
            rows.Add(new ParsedRow(rowNumber, cells));
        }

        return new ParseResult(rows, null);
    }

    /// <summary>
    /// Read a spreadsheet cell as the text the operator sees, with ONE exception: a real date cell is
    /// rendered as ISO.
    ///
    /// <para>This is the single most dangerous conversion in the file. A cell that a user typed as 03/04/2026
    /// and Excel stored as a date has no unambiguous string form — <c>GetString()</c> returns it in whatever
    /// culture wrote the workbook, and "3 April" and "4 March" are both plausible enrolment dates. Taking the
    /// underlying DateTime and formatting it ourselves removes the ambiguity entirely.</para>
    /// </summary>
    private static string? CellText(IXLCell cell)
    {
        if (cell is null || cell.IsEmpty()) return null;
        if (cell.DataType == XLDataType.DateTime && cell.TryGetValue<DateTime>(out var date))
            return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (cell.DataType == XLDataType.Number && cell.TryGetValue<double>(out var number))
            return number.ToString("0.############", CultureInfo.InvariantCulture);
        if (cell.DataType == XLDataType.Boolean && cell.TryGetValue<bool>(out var flag))
            return flag ? "true" : "false";
        return cell.GetString();
    }

    /// <summary>Canonical column name → its ZERO-BASED position in this particular file. Columns may appear in
    /// any order: insisting on the template's order would reject files that are otherwise perfectly correct,
    /// and an operator who reorders columns has not made a mistake.</summary>
    private static List<(string Canonical, int Index)> BuildMap(BulkTemplate template, IReadOnlyList<string> header)
    {
        var map = new List<(string, int)>();
        for (var i = 0; i < header.Count; i++)
        {
            var canonical = BulkColumn.Canonical(header[i]);
            if (template.Columns.Any(c => c.CanonicalName == canonical)) map.Add((canonical, i));
        }
        return map;
    }

    private static ParseResult TooManyRows() => ParseResult.Failed(new ParseFailure("TOO_MANY_ROWS",
        $"The file exceeds the {MaxRows:N0}-row limit for a single job; split it.",
        $"يتجاوز الملف الحد الأقصى البالغ {MaxRows:N0} صف للوظيفة الواحدة؛ يُرجى تقسيمه."));
}
