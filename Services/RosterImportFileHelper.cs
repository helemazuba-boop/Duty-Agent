using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace DutyAgent.Services;

internal static class RosterImportFileHelper
{
    private static readonly XNamespace SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace OfficeRelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace RelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly HashSet<string> SupportedNameHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "\u59d3\u540d",
        "\u5b66\u751f\u59d3\u540d",
        "name",
        "studentname",
        "student_name"
    };

    public static List<string> LoadStudentNames(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Student list file path is empty.", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Student list file not found.", filePath);
        }

        var extension = (Path.GetExtension(filePath) ?? string.Empty).Trim().ToLowerInvariant();
        return extension switch
        {
            ".txt" => LoadFromTxt(filePath),
            ".xlsx" => LoadFromXlsx(filePath),
            _ => throw new InvalidDataException("Unsupported file format. Only .txt and .xlsx are supported.")
        };
    }

    private static List<string> LoadFromTxt(string filePath)
    {
        return File.ReadAllLines(filePath, Encoding.UTF8)
            .Select(x => (x ?? string.Empty).Trim())
            .Where(x => x.Length > 0)
            .ToList();
    }

    private static List<string> LoadFromXlsx(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var sharedStrings = ReadSharedStrings(archive);
        var worksheet = ResolveWorksheetEntry(archive);
        if (worksheet == null)
        {
            throw new InvalidDataException("Invalid workbook: worksheet not found.");
        }

        using var sheetStream = worksheet.Open();
        var sheetDoc = XDocument.Load(sheetStream);
        var rows = ReadRows(sheetDoc, sharedStrings);
        if (rows.Count == 0)
        {
            return [];
        }

        if (!TryResolveNameColumn(rows, out var headerRowIndex, out var nameColumn))
        {
            throw new InvalidDataException("Workbook missing a `\u59d3\u540d` column.");
        }

        var names = new List<string>();
        for (var i = headerRowIndex + 1; i < rows.Count; i++)
        {
            var name = GetCell(rows[i], nameColumn).Trim();
            if (name.Length > 0)
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static ZipArchiveEntry? ResolveWorksheetEntry(ZipArchive archive)
    {
        var workbookEntry = archive.GetEntry("xl/workbook.xml");
        if (workbookEntry != null)
        {
            using var workbookStream = workbookEntry.Open();
            var workbookDoc = XDocument.Load(workbookStream);
            var firstSheet = workbookDoc.Descendants(SpreadsheetNs + "sheet").FirstOrDefault();
            var relationId = firstSheet?.Attribute(OfficeRelNs + "id")?.Value;
            if (!string.IsNullOrWhiteSpace(relationId))
            {
                var workbookRelsEntry = archive.GetEntry("xl/_rels/workbook.xml.rels");
                if (workbookRelsEntry != null)
                {
                    using var relStream = workbookRelsEntry.Open();
                    var relDoc = XDocument.Load(relStream);
                    var relation = relDoc.Descendants(RelNs + "Relationship")
                        .FirstOrDefault(x => string.Equals(
                            x.Attribute("Id")?.Value,
                            relationId,
                            StringComparison.Ordinal));
                    var target = relation?.Attribute("Target")?.Value;
                    if (!string.IsNullOrWhiteSpace(target))
                    {
                        var normalized = target.Replace('\\', '/').TrimStart('/');
                        if (!normalized.StartsWith("xl/", StringComparison.OrdinalIgnoreCase))
                        {
                            normalized = $"xl/{normalized}";
                        }

                        var entry = archive.GetEntry(normalized);
                        if (entry != null)
                        {
                            return entry;
                        }
                    }
                }
            }
        }

        return archive.Entries
            .Where(x => x.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase) &&
                        x.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.FullName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static List<string> ReadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry == null)
        {
            return [];
        }

        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        return doc
            .Descendants(SpreadsheetNs + "si")
            .Select(si => string.Concat(si.Descendants(SpreadsheetNs + "t").Select(t => t.Value)))
            .ToList();
    }

    private static List<Dictionary<int, string>> ReadRows(XDocument sheetDoc, IReadOnlyList<string> sharedStrings)
    {
        var rows = new List<Dictionary<int, string>>();
        var rowElements = sheetDoc
            .Descendants(SpreadsheetNs + "sheetData")
            .Elements(SpreadsheetNs + "row");

        foreach (var rowElement in rowElements)
        {
            var row = new Dictionary<int, string>();
            foreach (var cell in rowElement.Elements(SpreadsheetNs + "c"))
            {
                var reference = cell.Attribute("r")?.Value ?? string.Empty;
                var column = GetColumnIndexFromCellReference(reference);
                if (column <= 0)
                {
                    continue;
                }

                row[column] = ReadCellValue(cell, sharedStrings);
            }

            rows.Add(row);
        }

        return rows;
    }

    private static string ReadCellValue(XElement cell, IReadOnlyList<string> sharedStrings)
    {
        var type = cell.Attribute("t")?.Value ?? string.Empty;
        if (string.Equals(type, "inlineStr", StringComparison.OrdinalIgnoreCase))
        {
            var inline = cell.Element(SpreadsheetNs + "is");
            return inline == null
                ? string.Empty
                : string.Concat(inline.Descendants(SpreadsheetNs + "t").Select(t => t.Value));
        }

        var raw = cell.Element(SpreadsheetNs + "v")?.Value ?? string.Empty;
        if (string.Equals(type, "s", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(raw, out var idx) &&
            idx >= 0 &&
            idx < sharedStrings.Count)
        {
            return sharedStrings[idx];
        }

        return raw;
    }

    private static int GetColumnIndexFromCellReference(string cellReference)
    {
        if (string.IsNullOrWhiteSpace(cellReference))
        {
            return -1;
        }

        var column = 0;
        foreach (var ch in cellReference.ToUpperInvariant())
        {
            if (ch is < 'A' or > 'Z')
            {
                break;
            }

            column = (column * 26) + (ch - 'A' + 1);
        }

        return column;
    }

    private static bool TryResolveNameColumn(
        IReadOnlyList<Dictionary<int, string>> rows,
        out int headerRowIndex,
        out int nameColumn)
    {
        var scanRows = Math.Min(rows.Count, 20);
        for (var rowIndex = 0; rowIndex < scanRows; rowIndex++)
        {
            foreach (var (column, value) in rows[rowIndex])
            {
                if (IsNameHeader(value))
                {
                    headerRowIndex = rowIndex;
                    nameColumn = column;
                    return true;
                }
            }
        }

        headerRowIndex = -1;
        nameColumn = -1;
        return false;
    }

    private static bool IsNameHeader(string value)
    {
        var normalized = (value ?? string.Empty)
            .Trim()
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        return SupportedNameHeaders.Contains(normalized);
    }

    private static string GetCell(IReadOnlyDictionary<int, string> row, int column)
    {
        return row.TryGetValue(column, out var value) ? value : string.Empty;
    }
}
