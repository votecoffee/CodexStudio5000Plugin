using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Studio5000.Core;

// Shared lexical spans for already validated XML. Parsed nodes identify targets;
// round-trip-checked decoding and small patches preserve all unselected bytes.
internal sealed class XmlSourceDocument
{
    internal readonly record struct Span(int Start, int End, int OpenEnd, int CloseStart, bool Empty, string QualifiedName);
    internal sealed record Patch(int Start, int End, string Text);
    private readonly Encoding encoding;
    private readonly byte[] preamble;
    private readonly int[] lines;
    private readonly string errorPrefix;
    public string Text { get; }
    public XDocument Document { get; }
    public XmlSourceDocument(byte[] bytes, XDocument document, string errorPrefix = "xml_preservation")
    {
        this.errorPrefix = errorPrefix; Document = document;
        if (bytes.Length > 64 * 1024 * 1024) throw Error("limit", "XML byte preservation supports at most 64 MiB.");
        var count = 0;
        foreach (var node in document.Descendants()) if (++count > 1000000 || node.Ancestors().Take(65).Count() > 64) throw Error("limit", "XML byte preservation supports 1000000 elements and 64 nesting levels.");
        var skip = 0;
        try
        {
            if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 })) { encoding = new UTF32Encoding(false, false, true); skip = 4; }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff })) { encoding = new UTF32Encoding(true, false, true); skip = 4; }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) { encoding = new UTF8Encoding(false, true); skip = 3; }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) { encoding = new UnicodeEncoding(false, false, true); skip = 2; }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) { encoding = new UnicodeEncoding(true, false, true); skip = 2; }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0, 0x3c })) encoding = new UTF32Encoding(true, false, true);
            else if (bytes.AsSpan().StartsWith(new byte[] { 0x3c, 0, 0, 0 })) encoding = new UTF32Encoding(false, false, true);
            else if (bytes.AsSpan().StartsWith(new byte[] { 0, 0x3c, 0, 0x3f })) encoding = new UnicodeEncoding(true, false, true);
            else if (bytes.AsSpan().StartsWith(new byte[] { 0x3c, 0, 0x3f, 0 })) encoding = new UnicodeEncoding(false, false, true);
            else
            {
                var name = document.Declaration?.Encoding ?? "utf-8";
                encoding = Encoding.GetEncoding(name.Equals("utf8", StringComparison.OrdinalIgnoreCase) ? "utf-8" : name, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            }
            preamble = bytes[..skip]; Text = encoding.GetString(bytes.AsSpan(skip));
            if (!preamble.Concat(encoding.GetBytes(Text)).SequenceEqual(bytes)) throw Error("encoding", "Source encoding does not round-trip exactly; no byte-preserving edit was produced.");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        { throw Error("encoding", "The XML source encoding cannot be preserved exactly."); }
        var offsets = new List<int> { 0 };
        for (var i = 0; i < Text.Length; i++)
            if (Text[i] == '\r') { if (i + 1 < Text.Length && Text[i + 1] == '\n') i++; offsets.Add(i + 1); }
            else if (Text[i] == '\n') offsets.Add(i + 1);
        lines = offsets.ToArray();
    }
    private OperationException Error(string suffix, string message) => new(errorPrefix + "_" + suffix, message);
    private int Position(XObject node)
    {
        var info = (IXmlLineInfo)node;
        if (!info.HasLineInfo() || info.LineNumber < 1 || info.LineNumber > lines.Length) throw Error("span", "Tracked XML source positions are required.");
        return lines[info.LineNumber - 1] + info.LinePosition - 1;
    }
    private int OpenEnd(int start)
    {
        char quote = '\0';
        for (var i = start + 1; i < Text.Length; i++)
            if (quote != '\0') { if (Text[i] == quote) quote = '\0'; }
            else if (Text[i] is '\'' or '"') quote = Text[i];
            else if (Text[i] == '>') return i + 1;
        throw Error("span", "Opening XML delimiter is incomplete.");
    }
    public Span Locate(XElement element)
    {
        var start = Text.LastIndexOf('<', Math.Min(Position(element), Text.Length - 1));
        if (start < 0) throw Error("span", "Element source position is unavailable.");
        var nameEnd = start + 1; while (nameEnd < Text.Length && !char.IsWhiteSpace(Text[nameEnd]) && Text[nameEnd] is not ('/' or '>')) nameEnd++;
        var name = Text[(start + 1)..nameEnd]; var colon = name.IndexOf(':');
        if ((colon < 0 ? name : name[(colon + 1)..]) != element.Name.LocalName || (colon >= 0 && element.GetNamespaceOfPrefix(name[..colon]) != element.Name.Namespace)) throw Error("span", "Parsed identity differs from its source name.");
        var openEnd = OpenEnd(start); if (Text[openEnd - 2] == '/') return new(start, openEnd, openEnd, start, true, name);
        var depth = 1; var cursor = openEnd;
        while (depth != 0)
        {
            var token = Text.IndexOf('<', cursor); if (token < 0) throw Error("span", "Closing XML delimiter is missing.");
            var tail = Text.AsSpan(token);
            string? terminator = tail.StartsWith("<![CDATA[", StringComparison.Ordinal) ? "]]>" : tail.StartsWith("<!--", StringComparison.Ordinal) ? "-->" : tail.StartsWith("<?", StringComparison.Ordinal) ? "?>" : null;
            if (terminator is not null)
            { var finish = Text.IndexOf(terminator, token + 2, StringComparison.Ordinal); if (finish < 0) throw Error("span", "XML token is incomplete."); cursor = finish + terminator.Length; continue; }
            var end = OpenEnd(token);
            if (Text[token + 1] == '/') depth--; else if (Text[end - 2] != '/') depth++;
            if (depth == 0) return new(start, end, openEnd, token, false, name);
            cursor = end;
        }
        throw Error("span", "Invalid XML source span.");
    }
    public (int Start, int End, char Quote) AttributeValue(XAttribute attribute)
    {
        var cursor = Position(attribute);
        while (cursor < Text.Length && !char.IsWhiteSpace(Text[cursor]) && Text[cursor] != '=') cursor++;
        while (cursor < Text.Length && char.IsWhiteSpace(Text[cursor])) cursor++;
        if (cursor >= Text.Length || Text[cursor++] != '=') throw Error("span", "Attribute assignment is unavailable.");
        while (cursor < Text.Length && char.IsWhiteSpace(Text[cursor])) cursor++;
        if (cursor >= Text.Length || Text[cursor] is not ('\'' or '"')) throw Error("span", "Quoted attribute source is required.");
        var quote = Text[cursor++]; var end = Text.IndexOf(quote, cursor); if (end < 0) throw Error("span", "Attribute delimiter is incomplete.");
        return (cursor, end, quote);
    }
    public byte[] Apply(IEnumerable<Patch> edits)
    {
        var result = new StringBuilder(Text.Length); var previous = 0;
        foreach (var edit in edits.OrderBy(e => e.Start))
        {
            if (edit.Start < previous || edit.End < edit.Start || edit.End > Text.Length) throw Error("span", "Overlapping or invalid XML edit spans.");
            result.Append(Text, previous, edit.Start - previous); result.Append(edit.Text); previous = edit.End;
        }
        result.Append(Text, previous, Text.Length - previous); byte[] payload;
        try { payload = encoding.GetBytes(result.ToString()); }
        catch (EncoderFallbackException) { throw Error("encoding", "The requested value cannot be represented in the original XML encoding."); }
        if ((long)payload.Length + preamble.Length > 64 * 1024 * 1024) throw Error("limit", "Edited XML exceeds 64 MiB.");
        return preamble.Concat(payload).ToArray();
    }
}
