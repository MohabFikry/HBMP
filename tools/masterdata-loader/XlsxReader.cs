using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Mersal.MasterData.Loader;

/// <summary>
/// A streaming reader for the Egyptian drug-list workbook.
/// </summary>
/// <remarks>
/// The sheet is ~750,000 cells (22,653 rows × 33 columns; 28 MB of sheet XML). Materialising the workbook
/// costs on the order of a gigabyte, which is a poor thing to require of a CI runner, so this walks the
/// sheet with a SAX reader and yields one row at a time. Cells are bound by <b>header name</b> rather than
/// position: a reordered or renamed column then fails loudly at load instead of silently populating nulls
/// that would later read as "this drug has no indications".
/// </remarks>
public static class XlsxReader
{
    /// <summary>Streams the <c>Drug List</c> sheet. Throws if the sheet or any required column is absent.</summary>
    public static IEnumerable<DrugListXlsxRow> ReadDrugList(string path, string sheetName = "Drug List")
    {
        using var doc = SpreadsheetDocument.Open(path, isEditable: false);
        var workbookPart = doc.WorkbookPart
            ?? throw new InvalidDataException($"{path}: no workbook part — not a valid xlsx.");

        var sheet = workbookPart.Workbook.Descendants<Sheet>()
            .FirstOrDefault(s => string.Equals(s.Name?.Value, sheetName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                $"{path}: no sheet named '{sheetName}'. Found: " +
                string.Join(", ", workbookPart.Workbook.Descendants<Sheet>().Select(s => $"'{s.Name?.Value}'")));

        var strings = SharedStrings(workbookPart);
        var part = (WorksheetPart)workbookPart.GetPartById(sheet.Id!.Value!);

        Dictionary<string, int>? header = null;
        using var reader = OpenXmlReader.Create(part);

        while (reader.Read())
        {
            if (reader.ElementType != typeof(Row) || !reader.IsStartElement) continue;

            var cells = ReadRow((Row)reader.LoadCurrentElement()!, strings);

            if (header is null)
            {
                header = BuildHeader(cells, path, sheetName);
                continue;
            }

            string? At(string column) =>
                header.TryGetValue(column, out var i) && cells.TryGetValue(i, out var v) ? v : null;

            // A row with no id and no trade name is trailing spreadsheet whitespace, not a drug.
            if (At(DrugListColumns.SourceRowId) is null && At(DrugListColumns.TradeNameEn) is null) continue;

            yield return new DrugListXlsxRow
            {
                SourceRowId = At(DrugListColumns.SourceRowId),
                TradeNameEn = At(DrugListColumns.TradeNameEn),
                PriceEgp = At(DrugListColumns.PriceEgp),
                ActiveIngredient = At(DrugListColumns.ActiveIngredient),
                Manufacturer = At(DrugListColumns.Manufacturer),
                AtcCode = At(DrugListColumns.AtcCode),
                AtcL1 = At(DrugListColumns.AtcL1),
                AtcL2 = At(DrugListColumns.AtcL2),
                AtcL3 = At(DrugListColumns.AtcL3),
                AtcL4 = At(DrugListColumns.AtcL4),
                AtcL5 = At(DrugListColumns.AtcL5),
                RelatedIcds = At(DrugListColumns.RelatedIcds),
                IcdCount = At(DrugListColumns.IcdCount),
                IcdBasis = At(DrugListColumns.IcdBasis),
                MajorUnits = At(DrugListColumns.MajorUnits),
                MinorUnits = At(DrugListColumns.MinorUnits),
                VolumeWeight = At(DrugListColumns.VolumeWeight),
                Strength = At(DrugListColumns.Strength),
                DosageForm = At(DrugListColumns.DosageForm),
            };
        }

        if (header is null) throw new InvalidDataException($"{path}: sheet '{sheetName}' is empty — no header row.");
    }

    private static Dictionary<string, int> BuildHeader(Dictionary<int, string> cells, string path, string sheetName)
    {
        var header = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (index, name) in cells)
        {
            if (!string.IsNullOrWhiteSpace(name)) header[name.Trim()] = index;
        }

        var missing = DrugListColumns.Required.Where(c => !header.ContainsKey(c)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidDataException(
                $"{path}: sheet '{sheetName}' is missing required column(s): {string.Join(", ", missing)}. " +
                $"Found: {string.Join(", ", header.Keys)}. " +
                "The column mapping is documented in tools/masterdata-loader/README.md — update both together.");
        }

        return header;
    }

    /// <summary>Cell values by zero-based column index. Blank cells are omitted rather than stored as "".</summary>
    private static Dictionary<int, string> ReadRow(Row row, string[] strings)
    {
        var values = new Dictionary<int, string>();
        var position = 0;
        foreach (var cell in row.Elements<Cell>())
        {
            // Writers may omit the reference on a dense row. Falling back to the running position keeps the
            // columns aligned; defaulting to 0 would pile every cell onto the first column instead.
            var index = cell.CellReference?.Value is { Length: > 0 } reference ? ColumnIndex(reference) : position;
            position = index + 1;

            var text = CellText(cell, strings);
            if (!string.IsNullOrWhiteSpace(text)) values[index] = text.Trim();
        }
        return values;
    }

    private static string? CellText(Cell cell, string[] strings)
    {
        if (cell.DataType?.Value == CellValues.SharedString)
        {
            return int.TryParse(cell.CellValue?.InnerText, out var i) && i >= 0 && i < strings.Length
                ? strings[i]
                : null;
        }
        if (cell.DataType?.Value == CellValues.InlineString) return cell.InlineString?.Text?.Text;
        return cell.CellValue?.InnerText;
    }

    /// <summary>"AG12" → 32. Zero-based, so it lines up with the header map.</summary>
    private static int ColumnIndex(string? cellReference)
    {
        if (string.IsNullOrEmpty(cellReference)) return 0;
        var n = 0;
        foreach (var ch in cellReference)
        {
            if (ch is < 'A' or > 'Z') break;
            n = n * 26 + (ch - 'A' + 1);
        }
        return n - 1;
    }

    /// <summary>
    /// The shared-string table, read once. It is 2.7 MB here — large enough to be worth reading once and
    /// small enough to hold, unlike the sheet itself.
    /// </summary>
    private static string[] SharedStrings(WorkbookPart workbookPart)
    {
        var table = workbookPart.SharedStringTablePart?.SharedStringTable;
        if (table is null) return [];

        var items = new List<string>();
        foreach (var item in table.Elements<SharedStringItem>())
        {
            // Concatenate runs; skip phonetic (rPh) so Arabic/Latin text is not doubled.
            items.Add(item.Text?.Text ?? string.Concat(item.Elements<Run>().Select(r => r.Text?.Text ?? "")));
        }
        return [.. items];
    }
}
