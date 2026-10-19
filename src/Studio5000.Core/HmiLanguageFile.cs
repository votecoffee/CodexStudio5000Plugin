using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Studio5000.Core;

// Native language exports are independent of display XML and ViewApplication.hmi.
internal sealed class HmiLanguageFile
{
    internal const int MaximumBytes = 16 * 1024 * 1024;
    internal const string Boundary = "Offline language-export editing only. Keys/context, other translations and original files are preserved. Native import, layout/fonts and runtime language switching require separate verification. Text uses the export's own escapes; no automatic translation or fallback.";
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly Regex Locale = new("^[A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})+$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex Escaped = new("_x([0-9a-fA-F]{4})_", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly string[] FtHeaders = ["Server", "Component Type", "Component Name", "Description"];
    private sealed record Cell(string Text, bool Writable, XElement? Element = null);
    private sealed record TextCell(string Value, int Start, int End);
    private sealed record TextRow(TextCell[] Cells, int End);
    private sealed class Table(string name, string path, int[] identity, int headerRow)
    {
        internal string Name = name, Path = path;
        internal int[] Identity = identity;
        internal int HeaderRow = headerRow;
        internal Dictionary<string, int> Languages = new(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<int, Dictionary<int, Cell>> Rows = [];
        internal XDocument? Xml;
        internal Dictionary<int, TextRow> TextRows = [];
        internal bool Protected;
        internal int Width;
    }
    private sealed record Entry(string Key, Table Table, int Row, string[] Context);
    private readonly List<Table> tables = [];
    private readonly List<Entry> entries = [];
    private readonly Dictionary<string, byte[]> parts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> timestamps = new(StringComparer.Ordinal);
    private string text = "";
    private Encoding encoding = new UTF8Encoding(false, true);
    private byte[] preamble = [];
    internal string Format { get; private set; } = "";
    internal string[] Languages => tables.SelectMany(t => t.Languages.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    internal HmiLanguageRecord[] Records => entries.Select(e => new HmiLanguageRecord(e.Key, e.Table.Name, e.Context,
        e.Table.Languages.Select(l => { var c = Get(e.Table, e.Row, l.Value); return new HmiLanguageText(l.Key, c.Text, c.Writable && !e.Table.Protected); }).ToArray())).ToArray();
    internal static string Language(string? value)
    {
        if (value is null || value.Length > 64 || !Locale.IsMatch(value)) throw Error("Use an explicit RFC/BCP language code such as en-US or zh-CN.");
        return value;
    }
    internal static void Platform(string platform)
    { if (platform is not ("ftview-me" or "ftview-se" or "view-designer")) throw Error("Choose ftview-me, ftview-se or view-designer."); }
    private static OperationException Error(string message) => new("hmi_language_format", message);
    internal static HmiLanguageFile Parse(byte[] bytes, string platform, string? language)
    {
        Platform(platform);
        if (bytes.Length is < 1 or > MaximumBytes) throw Error("Language exports must be 1 byte..16 MiB.");
        var file = new HmiLanguageFile();
        try
        {
            if (bytes.AsSpan().StartsWith(new byte[] { 0x50, 0x4b, 3, 4 })) file.Workbook(bytes, platform);
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xd0, 0xcf, 0x11, 0xe0 })) throw Error("Legacy binary Excel/BIFF is unsupported. Save a separate OpenXML .xlsx copy or export Unicode text from Studio.");
            else file.TextFile(bytes, platform, language);
            if (file.entries.Count is < 1 or > 50000 || file.Languages.Length is < 1 or > 64) throw Error("Require 1..50000 records and 1..64 languages.");
            if (file.entries.Select(e => e.Key).Distinct(StringComparer.Ordinal).Count() != file.entries.Count) throw Error("Duplicate native string identities are ambiguous; export an unambiguous language file.");
            return file;
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or DecoderFallbackException or FormatException or OverflowException or ArgumentException)
        { throw Error("Invalid or unsupported language export: " + ex.Message); }
    }
    private static XDocument Xml(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64 * 1024 * 1024 });
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }
    private void Workbook(byte[] bytes, string platform)
    {
        Format = "openxml";
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        if (archive.Entries.Count > 512 || archive.Entries.Sum(e => e.Length) > 64 * 1024 * 1024) throw Error("Workbook exceeds the bounded ZIP entry/expanded-size limit.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            if (!names.Add(entry.FullName) || entry.FullName.Contains('\\') || entry.FullName.StartsWith('/') || entry.FullName.Split('/').Contains("..") ||
                entry.FullName.Contains("vbaProject", StringComparison.OrdinalIgnoreCase) || entry.FullName.StartsWith("_xmlsignatures/", StringComparison.OrdinalIgnoreCase)) throw Error("Duplicate, unsafe, macro-enabled or signed workbook packages are unsupported.");
            using var stream = entry.Open(); using var output = new MemoryStream(); stream.CopyTo(output); parts.Add(entry.FullName, output.ToArray()); timestamps.Add(entry.FullName, entry.LastWriteTime);
        }
        byte[] Part(string name) => parts.TryGetValue(name, out var result) ? result : throw Error("Required workbook part is missing: " + name);
        var workbook = Xml(Part("xl/workbook.xml"));
        if (workbook.Root?.Name != S + "workbook") throw Error("Require a standard OpenXML spreadsheet workbook.");
        var relations = Xml(Part("xl/_rels/workbook.xml.rels"));
        var shared = parts.TryGetValue("xl/sharedStrings.xml", out var ss) ? Xml(ss).Root?.Elements(S + "si").ToArray() ?? [] : [];
        if (shared.Length > 500000) throw Error("Too many shared strings.");
        var sheetNames = new HashSet<string>(StringComparer.Ordinal);
        var sheetPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sheet in workbook.Root.Element(S + "sheets")?.Elements(S + "sheet") ?? [])
        {
            var name = (string?)sheet.Attribute("name") ?? throw Error("Sheet name missing.");
            if (!sheetNames.Add(name)) throw Error("Duplicate worksheet names.");
            var rels = relations.Root!.Elements().Where(e => (string?)e.Attribute("Id") == (string?)sheet.Attribute(R + "id")).ToArray();
            if (rels.Length != 1 || (string?)rels[0].Attribute("TargetMode") == "External") throw Error("Invalid worksheet relationship.");
            var target = (string?)rels[0].Attribute("Target") ?? "";
            var path = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
            if (path.Split('/').Contains("..") || !sheetPaths.Add(path)) throw Error("Ambiguous worksheet relationship.");
            var xml = Xml(Part(path));
            if (xml.Root?.Name != S + "worksheet") throw Error("Unsupported worksheet type.");
            var rows = new Dictionary<int, Dictionary<int, Cell>>();
            foreach (var row in xml.Root.Element(S + "sheetData")?.Elements(S + "row") ?? [])
            {
                var rowNumber = (int?)row.Attribute("r") ?? throw Error("Worksheet row numbers must be explicit.");
                if (rowNumber is < 1 or > 1048576 || !rows.TryAdd(rowNumber, [])) throw Error("Invalid or duplicate worksheet row.");
                foreach (var cell in row.Elements(S + "c"))
                {
                    var reference = (string?)cell.Attribute("r") ?? ""; var (col, cellRow) = Address(reference);
                    if (cellRow != rowNumber || !rows[rowNumber].TryAdd(col, ReadCell(cell, shared))) throw Error("Invalid or duplicate cell address.");
                }
            }
            if (rows.Sum(r => r.Value.Count) > 1000000) throw Error("Too many worksheet cells.");
            var candidate = rows.OrderBy(r => r.Key).FirstOrDefault(r => r.Value.Values.Any(c => c.Text.Length != 0));
            if (candidate.Value is null) continue; // Preserve empty sheets without inventing a language table.
            var headers = Enumerable.Range(0, candidate.Value.Keys.DefaultIfEmpty(0).Max() + 1).Select(i => candidate.Value.GetValueOrDefault(i)?.Text ?? "").ToArray();
            var table = Header(name, path, headers, candidate.Key, platform);
            table.Rows = rows; table.Xml = xml; table.Protected = xml.Root.Element(S + "sheetProtection") is not null || xml.Root.Element(S + "mergeCells") is not null;
            tables.Add(table); AddEntries(table);
        }
    }
    private static Cell ReadCell(XElement c, XElement[] shared)
    {
        var type = (string?)c.Attribute("t"); var value = c.Element(S + "v")?.Value ?? ""; var writable = c.Element(S + "f") is null && c.Attribute("cm") is null && c.Attribute("vm") is null;
        if (type == "s")
        {
            if (!int.TryParse(value, out var index) || index < 0 || index >= shared.Length) throw Error("Invalid shared string index.");
            var item = shared[index]; return new(Decode(string.Concat(item.Descendants(S + "t").Select(t => t.Value))), writable && item.Elements().All(e => e.Name == S + "t"), c);
        }
        if (type == "inlineStr")
        {
            var item = c.Element(S + "is"); return new(Decode(string.Concat(item?.Descendants(S + "t").Select(t => t.Value) ?? [])), writable && item is not null && item.Elements().All(e => e.Name == S + "t"), c);
        }
        return new(Decode(value), writable && (type == "str" || type is null && value.Length == 0), c);
    }
    private static string Decode(string value) => Escaped.Replace(value, m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
    private static string Encode(string value) => Escaped.Replace(value, m => "_x005F_" + m.Value[1..]).Replace("\r", "_x000D_");
    private static (int Col, int Row) Address(string reference)
    {
        var col = 0; var i = 0;
        while (i < reference.Length && reference[i] is >= 'A' and <= 'Z') { col = checked(col * 26 + reference[i] - 'A' + 1); i++; }
        if (col is < 1 or > 16384 || !int.TryParse(reference.AsSpan(i), out var row)) throw Error("Invalid cell reference.");
        return (col - 1, row);
    }
    private static string Column(int index)
    {
        var result = ""; for (var n = index + 1; n > 0; n = (n - 1) / 26) result = (char)('A' + (n - 1) % 26) + result; return result;
    }
    private static Table Header(string name, string path, string[] headers, int headerRow, string platform)
    {
        int[] identity;
        if (platform == "view-designer")
        {
            identity = headers.Select((h, i) => (h, i)).Where(x => x.h == "KEY").Select(x => x.i).ToArray();
            if (identity.Length != 1) throw Error("View Designer language files require exactly one KEY column.");
        }
        else
        {
            if (headers.Length < 5 || !headers.Take(4).SequenceEqual(FtHeaders)) throw Error("FactoryTalk workbooks require Server, Component Type, Component Name, Description followed by language columns.");
            identity = [0, 1, 2, 3];
        }
        var table = new Table(name, path, identity, headerRow) { Width = headers.Length };
        for (var i = 0; i < headers.Length; i++)
        {
            if (identity.Contains(i)) continue;
            var language = headers[i].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            Language(language);
            if (!table.Languages.TryAdd(language!, i)) throw Error("Duplicate language columns cannot be edited unambiguously.");
        }
        return table;
    }
    private static Cell Get(Table table, int row, int col) => table.Rows.GetValueOrDefault(row)?.GetValueOrDefault(col) ?? new("", true);
    private void AddEntries(Table table)
    {
        foreach (var row in table.Rows.OrderBy(r => r.Key).Where(r => r.Key > table.HeaderRow))
        {
            if (row.Value.Values.All(c => c.Text.Length == 0)) continue;
            if (row.Value.Any(c => c.Key >= table.Width && c.Value.Text.Length != 0)) throw Error("Data outside the language table is unsupported.");
            var context = table.Identity.Select(i => Get(table, row.Key, i)).ToArray();
            if (context.All(c => c.Text.Length == 0) || context.Any(c => c.Element?.Element(S + "f") is not null)) throw Error("Native string identity is missing or contains a formula.");
            var values = context.Select(c => c.Text).ToArray();
            var key = ProjectService.Hash(JsonSerializer.SerializeToUtf8Bytes(new { sheet = table.Name, context = values }));
            entries.Add(new(key, table, row.Key, values));
        }
    }
    private void TextFile(byte[] bytes, string platform, string? language)
    {
        Format = "unicode-text"; var start = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) { encoding = new UnicodeEncoding(false, false, true); start = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) { encoding = new UnicodeEncoding(true, false, true); start = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) start = 3;
        preamble = bytes[..start]; text = encoding.GetString(bytes, start, bytes.Length - start);
        if (text.Contains('\0') || text.TrimStart().StartsWith('<')) throw Error("Unsupported text/XML export. Use OpenXML workbook or Unicode tab-delimited text.");
        var body = 0;
        if (platform != "view-designer")
        {
            Language(language);
            for (var i = 0; i < 7; i++) { var end = text.IndexOf('\n', body); if (end < 0) throw Error("FactoryTalk Unicode text must preserve its seven native header lines."); body = end + 1; }
        }
        var parsed = ParseRows(text, body, platform != "view-designer"); Table table; var dataStart = 0;
        if (platform == "view-designer")
        {
            if (parsed.Count == 0) throw Error("Empty language file.");
            table = Header("Text", "", parsed[0].Cells.Select(c => c.Value).ToArray(), 0, platform); dataStart = 1;
        }
        else
        {
            table = new("Text", "", [0, 1, 2], -1) { Width = 4 }; table.Languages.Add(language!, 3);
        }
        for (var i = 0; i < parsed.Count; i++)
        {
            if (i >= dataStart && parsed[i].Cells.Length != table.Width) throw Error("Unexpected tab-delimited column count; keys and context must remain unambiguous.");
            table.Rows[i] = parsed[i].Cells.Select((c, col) => (col, c)).ToDictionary(x => x.col, x => new Cell(x.c.Value, true)); table.TextRows[i] = parsed[i];
        }
        tables.Add(table); AddEntries(table);
    }
    private static List<TextRow> ParseRows(string source, int offset, bool comments)
    {
        var result = new List<TextRow>(); var i = offset;
        while (i < source.Length)
        {
            if (comments && source[i] == '#' || source[i] is '\r' or '\n') { var e = source.IndexOf('\n', i); i = e < 0 ? source.Length : e + 1; continue; }
            var cells = new List<TextCell>();
            while (true)
            {
                var start = i; var value = new StringBuilder();
                if (i < source.Length && source[i] == '"')
                {
                    i++; var closed = false;
                    while (i < source.Length)
                    {
                        var c = source[i++];
                        if (c != '"') { value.Append(c); continue; }
                        if (i < source.Length && source[i] == '"') { value.Append('"'); i++; continue; }
                        closed = true; break;
                    }
                    if (!closed || i < source.Length && source[i] is not ('\t' or '\r' or '\n')) throw Error("Ambiguous quoting in text export. Normalize a separate copy with Studio/Excel's Unicode text workflow.");
                }
                else while (i < source.Length && source[i] is not ('\t' or '\r' or '\n')) value.Append(source[i++]);
                cells.Add(new(value.ToString(), start, i));
                if (cells.Count > 128) throw Error("Too many text columns.");
                if (i >= source.Length || source[i] != '\t') break; i++;
            }
            result.Add(new(cells.ToArray(), i)); if (result.Count > 50001) throw Error("Too many text records.");
            if (i < source.Length && source[i] == '\r') i++; if (i < source.Length && source[i] == '\n') i++;
        }
        return result;
    }
    internal (byte[] Bytes, HmiLanguageChange[] Changes, string[] Added) Update(HmiLanguagePreviewRequest request)
    {
        if (request.Edits is null || request.Edits.Length > 200 || request.Edits.Any(e => e is null) || request.Edits.Length == 0 && (request.AddLanguages?.Length ?? 0) == 0) throw Error("Provide up to 200 explicit edits and/or new language columns.");
        var added = request.AddLanguages ?? [];
        if (added.Length > 16 || added.Distinct(StringComparer.OrdinalIgnoreCase).Count() != added.Length || Languages.Length + added.Length > 64) throw Error("Add at most 16 distinct language columns, with 64 total.");
        foreach (var l in added) { Language(l); if (Languages.Contains(l, StringComparer.OrdinalIgnoreCase)) throw Error("A requested new language already exists."); }
        if (added.Length != 0 && tables.Any(t => t.HeaderRow < 0 || t.Protected)) throw Error("New languages require an unprotected matrix export; single-language FactoryTalk text retains its native header.");
        var edits = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var changes = new List<HmiLanguageChange>();
        var replacements = new List<(int Start, int End, string Value)>(); var dirty = new HashSet<Table>();
        var oldWidths = tables.ToDictionary(t => t, t => t.Width);
        foreach (var table in tables)
        foreach (var l in added)
        {
            var col = table.Width++; table.Languages.Add(l, col); if (table.Xml is not null) SetCell(table, table.HeaderRow, col, l); dirty.Add(table);
        }
        foreach (var edit in request.Edits)
        {
            Language(edit.Language);
            if (edit.Text is null || edit.Text.Length > 32767) throw Error("Translation text must be explicit and at most 32767 UTF-16 characters.");
            try { XmlConvert.VerifyXmlChars(edit.Text); } catch (XmlException) { throw Error("Translation contains invalid XML/Unicode characters."); }
            if (!edits.Add(edit.Key + ":" + edit.Language)) throw Error("Edit each string/language pair once per batch.");
            var entry = entries.SingleOrDefault(e => e.Key == edit.Key) ?? throw Error("String key not found in this exact export.");
            if (!entry.Table.Languages.TryGetValue(edit.Language, out var column)) throw Error("Language is absent. Add its column explicitly first.");
            var cell = Get(entry.Table, entry.Row, column);
            if (!cell.Writable || entry.Table.Protected) throw Error("Formula, rich/typed, protected or merged language cells are read-only.");
            changes.Add(new(edit.Key, edit.Language, added.Contains(edit.Language, StringComparer.OrdinalIgnoreCase) ? null : cell.Text, edit.Text));
            if (entry.Table.Xml is not null) SetCell(entry.Table, entry.Row, column, edit.Text);
            else if (column < oldWidths[entry.Table])
            {
                var target = entry.Table.TextRows[entry.Row].Cells[column]; replacements.Add((target.Start, target.End, Quote(edit.Text)));
            }
            entry.Table.Rows[entry.Row][column] = new(edit.Text, true); dirty.Add(entry.Table);
        }
        foreach (var table in tables.Where(t => t.Xml is null && added.Length != 0))
        foreach (var (rowIndex, row) in table.TextRows)
        {
            var values = added.Select(l => rowIndex == table.HeaderRow ? l : Get(table, rowIndex, table.Languages[l]).Text);
            replacements.Add((row.End, row.End, "\t" + string.Join('\t', values.Select(Quote))));
        }
        if (Format == "unicode-text")
        {
            foreach (var replacement in replacements.OrderByDescending(r => r.Start).ThenByDescending(r => r.End)) text = text[..replacement.Start] + replacement.Value + text[replacement.End..];
            return ([..preamble, ..encoding.GetBytes(text)], changes.ToArray(), added);
        }
        foreach (var table in dirty)
        {
            var dimension = table.Xml!.Root!.Element(S + "dimension");
            if (dimension is not null && added.Length != 0) dimension.SetAttributeValue("ref", "A1:" + Column(table.Width - 1) + table.Rows.Keys.Max());
            using var output = new MemoryStream();
            using (var writer = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false, NewLineHandling = NewLineHandling.Entitize })) table.Xml.Save(writer);
            parts[table.Path] = output.ToArray();
        }
        using var result = new MemoryStream();
        using (var archive = new ZipArchive(result, ZipArchiveMode.Create, true))
        foreach (var (name, bytes) in parts)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.Optimal); entry.LastWriteTime = timestamps[name]; using var stream = entry.Open(); stream.Write(bytes);
        }
        if (result.Length > MaximumBytes) throw Error("Updated export exceeds the 16 MiB limit.");
        return (result.ToArray(), changes.ToArray(), added);
    }
    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
    private static void SetCell(Table table, int rowNumber, int column, string value)
    {
        var data = table.Xml!.Root!.Element(S + "sheetData")!;
        var row = data.Elements(S + "row").Single(r => (int?)r.Attribute("r") == rowNumber);
        var cell = row.Elements(S + "c").SingleOrDefault(c => Address((string)c.Attribute("r")!).Col == column);
        if (cell is null)
        {
            cell = new XElement(S + "c", new XAttribute("r", Column(column) + rowNumber));
            var following = row.Elements(S + "c").FirstOrDefault(c => Address((string)c.Attribute("r")!).Col > column);
            if (following is null) row.Add(cell); else following.AddBeforeSelf(cell);
        }
        cell.SetAttributeValue("t", "inlineStr"); cell.Elements(S + "v").Remove(); cell.Elements(S + "is").Remove();
        cell.Add(new XElement(S + "is", new XElement(S + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), Encode(value))));
    }
}
