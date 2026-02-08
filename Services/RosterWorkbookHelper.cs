using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using DutyIsland.Models;

namespace DutyIsland.Services;

internal static class RosterWorkbookHelper
{
    private static readonly XNamespace SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace PackageNs = "http://schemas.openxmlformats.org/package/2006/content-types";
    private static readonly XNamespace RelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace OfficeRelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static List<RosterEntry> Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Roster path is empty.", nameof(path));
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"roster.xlsx not found: {path}");
        }

        using var stream = File.OpenRead(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

        var sharedStrings = ReadSharedStrings(archive);
        var sheetEntry = archive.GetEntry("xl/worksheets/sheet1.xml")
                        ?? throw new InvalidDataException("Invalid roster workbook: missing sheet1.xml.");

        using var sheetStream = sheetEntry.Open();
        var sheetDoc = XDocument.Load(sheetStream);
        var rows = ReadRows(sheetDoc, sharedStrings);
        if (rows.Count == 0)
        {
            return [];
        }

        var headers = rows[0];
        if (!TryResolveHeader(headers, "id", out var idCol) || !TryResolveHeader(headers, "name", out var nameCol))
        {
            throw new InvalidDataException("roster.xlsx missing required headers: id,name.");
        }

        TryResolveHeader(headers, "active", out var activeCol);
        var result = new Dictionary<int, RosterEntry>();
        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            var rawId = GetCell(row, idCol).Trim();
            var rawName = GetCell(row, nameCol).Trim();
            if (!int.TryParse(rawId, out var id) || id <= 0 || string.IsNullOrWhiteSpace(rawName))
            {
                continue;
            }

            var activeRaw = activeCol > 0 ? GetCell(row, activeCol) : "1";
            result[id] = new RosterEntry
            {
                Id = id,
                Name = rawName,
                Active = ParseBool(activeRaw, defaultValue: true)
            };
        }

        return result.Values.OrderBy(x => x.Id).ToList();
    }

    public static List<RosterEntry> LoadCsv(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Roster path is empty.", nameof(path));
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Legacy roster file not found: {path}");
        }

        var lines = File.ReadAllLines(path, Encoding.UTF8);
        if (lines.Length == 0)
        {
            return [];
        }

        var headers = ParseCsvLine(lines[0]);
        var idIdx = FindHeaderIndex(headers, "id");
        var nameIdx = FindHeaderIndex(headers, "name");
        var activeIdx = FindHeaderIndex(headers, "active");
        if (idIdx < 0 || nameIdx < 0)
        {
            throw new InvalidDataException("Legacy roster.csv missing required headers: id,name.");
        }

        var result = new Dictionary<int, RosterEntry>();
        for (var i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                continue;
            }

            var cells = ParseCsvLine(lines[i]);
            var rawId = GetCsvCell(cells, idIdx).Trim();
            var rawName = GetCsvCell(cells, nameIdx).Trim();
            if (!int.TryParse(rawId, out var id) || id <= 0 || string.IsNullOrWhiteSpace(rawName))
            {
                continue;
            }

            var activeRaw = activeIdx >= 0 ? GetCsvCell(cells, activeIdx) : "1";
            result[id] = new RosterEntry
            {
                Id = id,
                Name = rawName,
                Active = ParseBool(activeRaw, defaultValue: true)
            };
        }

        return result.Values.OrderBy(x => x.Id).ToList();
    }

    public static void Save(string path, IReadOnlyList<RosterEntry> roster)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Roster path is empty.", nameof(path));
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var normalized = roster
            .Where(x => x.Id > 0 && !string.IsNullOrWhiteSpace(x.Name))
            .OrderBy(x => x.Id)
            .Select(x => new RosterEntry
            {
                Id = x.Id,
                Name = x.Name.Trim(),
                Active = x.Active
            })
            .ToList();

        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);

        WriteXmlEntry(archive, "[Content_Types].xml", BuildContentTypesXml());
        WriteXmlEntry(archive, "_rels/.rels", BuildRootRelsXml());
        WriteXmlEntry(archive, "xl/workbook.xml", BuildWorkbookXml());
        WriteXmlEntry(archive, "xl/_rels/workbook.xml.rels", BuildWorkbookRelsXml());
        WriteXmlEntry(archive, "xl/worksheets/sheet1.xml", BuildSheetXml(normalized));
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
                var reference = (string?)cell.Attribute("r") ?? string.Empty;
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
        var type = (string?)cell.Attribute("t") ?? string.Empty;
        if (string.Equals(type, "inlineStr", StringComparison.OrdinalIgnoreCase))
        {
            var isElement = cell.Element(SpreadsheetNs + "is");
            return isElement == null
                ? string.Empty
                : string.Concat(isElement.Descendants(SpreadsheetNs + "t").Select(t => t.Value));
        }

        var rawValue = cell.Element(SpreadsheetNs + "v")?.Value ?? string.Empty;
        if (string.Equals(type, "s", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(rawValue, out var sharedIndex) &&
            sharedIndex >= 0 &&
            sharedIndex < sharedStrings.Count)
        {
            return sharedStrings[sharedIndex];
        }

        return rawValue;
    }

    private static bool TryResolveHeader(IReadOnlyDictionary<int, string> headers, string name, out int column)
    {
        foreach (var (idx, value) in headers)
        {
            if (string.Equals(value.Trim(), name, StringComparison.OrdinalIgnoreCase))
            {
                column = idx;
                return true;
            }
        }

        column = -1;
        return false;
    }

    private static string GetCell(IReadOnlyDictionary<int, string> row, int column)
    {
        return row.TryGetValue(column, out var value) ? value : string.Empty;
    }

    private static int GetColumnIndexFromCellReference(string cellReference)
    {
        if (string.IsNullOrWhiteSpace(cellReference))
        {
            return -1;
        }

        var idx = 0;
        foreach (var ch in cellReference.ToUpperInvariant())
        {
            if (ch is < 'A' or > 'Z')
            {
                break;
            }
            idx = (idx * 26) + (ch - 'A' + 1);
        }

        return idx;
    }

    private static string BuildCellReference(int column, int row)
    {
        if (column <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(column));
        }
        if (row <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(row));
        }

        var letters = string.Empty;
        var value = column;
        while (value > 0)
        {
            var mod = (value - 1) % 26;
            letters = (char)('A' + mod) + letters;
            value = (value - 1) / 26;
        }

        return $"{letters}{row}";
    }

    private static XDocument BuildContentTypesXml()
    {
        return new XDocument(
            new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(PackageNs + "Types",
                new XElement(PackageNs + "Default",
                    new XAttribute("Extension", "rels"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                new XElement(PackageNs + "Default",
                    new XAttribute("Extension", "xml"),
                    new XAttribute("ContentType", "application/xml")),
                new XElement(PackageNs + "Override",
                    new XAttribute("PartName", "/xl/workbook.xml"),
                    new XAttribute("ContentType",
                        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")),
                new XElement(PackageNs + "Override",
                    new XAttribute("PartName", "/xl/worksheets/sheet1.xml"),
                    new XAttribute("ContentType",
                        "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"))));
    }

    private static XDocument BuildRootRelsXml()
    {
        return new XDocument(
            new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(RelNs + "Relationships",
                new XElement(RelNs + "Relationship",
                    new XAttribute("Id", "rId1"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"),
                    new XAttribute("Target", "xl/workbook.xml"))));
    }

    private static XDocument BuildWorkbookXml()
    {
        return new XDocument(
            new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(SpreadsheetNs + "workbook",
                new XAttribute(XNamespace.Xmlns + "r", OfficeRelNs),
                new XElement(SpreadsheetNs + "sheets",
                    new XElement(SpreadsheetNs + "sheet",
                        new XAttribute("name", "Roster"),
                        new XAttribute("sheetId", "1"),
                        new XAttribute(OfficeRelNs + "id", "rId1")))));
    }

    private static XDocument BuildWorkbookRelsXml()
    {
        return new XDocument(
            new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(RelNs + "Relationships",
                new XElement(RelNs + "Relationship",
                    new XAttribute("Id", "rId1"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"),
                    new XAttribute("Target", "worksheets/sheet1.xml"))));
    }

    private static XDocument BuildSheetXml(IReadOnlyList<RosterEntry> roster)
    {
        var rows = new List<XElement>
        {
            new(SpreadsheetNs + "row",
                new XAttribute("r", 1),
                BuildInlineStringCell(1, 1, "id"),
                BuildInlineStringCell(2, 1, "name"),
                BuildInlineStringCell(3, 1, "active"))
        };

        for (var i = 0; i < roster.Count; i++)
        {
            var rowNumber = i + 2;
            var row = roster[i];
            rows.Add(
                new XElement(SpreadsheetNs + "row",
                    new XAttribute("r", rowNumber),
                    BuildNumberCell(1, rowNumber, row.Id.ToString()),
                    BuildInlineStringCell(2, rowNumber, row.Name),
                    BuildNumberCell(3, rowNumber, row.Active ? "1" : "0")));
        }

        return new XDocument(
            new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(SpreadsheetNs + "worksheet",
                new XElement(SpreadsheetNs + "sheetData", rows)));
    }

    private static XElement BuildInlineStringCell(int column, int row, string value)
    {
        return new XElement(SpreadsheetNs + "c",
            new XAttribute("r", BuildCellReference(column, row)),
            new XAttribute("t", "inlineStr"),
            new XElement(SpreadsheetNs + "is",
                new XElement(SpreadsheetNs + "t", value ?? string.Empty)));
    }

    private static XElement BuildNumberCell(int column, int row, string value)
    {
        return new XElement(SpreadsheetNs + "c",
            new XAttribute("r", BuildCellReference(column, row)),
            new XElement(SpreadsheetNs + "v", value ?? string.Empty));
    }

    private static void WriteXmlEntry(ZipArchive archive, string entryPath, XDocument document)
    {
        var entry = archive.CreateEntry(entryPath, CompressionLevel.NoCompression);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        document.Save(writer, SaveOptions.DisableFormatting);
    }

    private static bool ParseBool(string text, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return defaultValue;
        }

        var value = text.Trim().ToLowerInvariant();
        return value switch
        {
            "1" => true,
            "true" => true,
            "yes" => true,
            "y" => true,
            "on" => true,
            "0" => false,
            "false" => false,
            "no" => false,
            "n" => false,
            "off" => false,
            _ => defaultValue
        };
    }

    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        if (line == null)
        {
            return result;
        }

        var current = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                    continue;
                }

                inQuotes = !inQuotes;
                continue;
            }

            if (ch == ',' && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        result.Add(current.ToString());
        return result;
    }

    private static int FindHeaderIndex(IReadOnlyList<string> headers, string headerName)
    {
        for (var i = 0; i < headers.Count; i++)
        {
            if (string.Equals(headers[i].Trim(), headerName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static string GetCsvCell(IReadOnlyList<string> cells, int index)
    {
        return index >= 0 && index < cells.Count ? cells[index] : string.Empty;
    }
}
