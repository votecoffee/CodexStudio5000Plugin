using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Studio5000.Core;

// Parsed identities select edits; lexical spans preserve every other UTF-8 byte,
// including CDATA, line endings, indentation and opaque native data formats.
internal sealed class LogixLexicalDocument
{
    internal readonly record struct Span(int Start, int End, int OpenEnd, int CloseStart, bool Empty);
    internal sealed record Patch(int Start, int End, string Text);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly bool bom;
    private readonly int[] lines;
    public string Text { get; }
    public XDocument Document { get; }
    public LogixLexicalDocument(byte[] bytes)
    {
        if (bytes.Length > 64 * 1024 * 1024) throw new OperationException("merge_limit", "A merge input supports at most 64 MiB.");
        bom = bytes.AsSpan().StartsWith(new byte[] { 239, 187, 191 });
        try { Text = Utf8.GetString(bytes.AsSpan(bom ? 3 : 0)); }
        catch (DecoderFallbackException) { throw new OperationException("merge_encoding", "Preserve-fresh merging requires UTF-8 L5X; other encodings are unsupported."); }
        using var reader = XmlReader.Create(new StringReader(Text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64 * 1024 * 1024 });
        Document = XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        XmlProjects.Validate(Document); ValidateBudget(Document);
        if (Document.Declaration?.Encoding is { } encoding && !encoding.Equals("utf-8", StringComparison.OrdinalIgnoreCase) && !encoding.Equals("utf8", StringComparison.OrdinalIgnoreCase))
            throw new OperationException("merge_encoding", "Preserve-fresh merging requires a UTF-8 declaration.");
        var offsets = new List<int> { 0 };
        for (var i = 0; i < Text.Length; i++)
            if (Text[i] == '\r') { if (i + 1 < Text.Length && Text[i + 1] == '\n') i++; offsets.Add(i + 1); }
            else if (Text[i] == '\n') offsets.Add(i + 1);
        lines = offsets.ToArray();
    }
    internal static void ValidateBudget(XDocument document)
    {
        if (document.Root?.Elements("Controller").Count() != 1) throw new OperationException("merge_unsupported", "Preservation requires exactly one Controller element.");
        foreach (var container in document.Root.Element("Controller")!.Elements("Programs").Elements("Program").Elements("Routines"))
            if (container.Elements().Any(e => e.Name != "Routine")) throw new OperationException("merge_unsupported", "Unknown program routine-container children are unsupported.");
        var count = 0;
        foreach (var node in document.Descendants())
            if (++count > 1000000 || node.Ancestors().Take(65).Count() > 64)
                throw new OperationException("merge_limit", "Project preservation supports 1000000 XML elements and 64 levels per input.");
    }
    private int OpenEnd(int start)
    {
        char quote = '\0';
        for (var i = start + 1; i < Text.Length; i++)
        {
            var c = Text[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; }
            else if (c is '\'' or '"') quote = c;
            else if (c == '>') return i + 1;
        }
        throw new OperationException("merge_span", "An XML opening delimiter was not found.");
    }
    public Span Locate(XElement element)
    {
        var info = (IXmlLineInfo)element;
        if (!info.HasLineInfo()) throw new OperationException("merge_span", "Source line information is required.");
        var position = lines[info.LineNumber - 1] + info.LinePosition - 1;
        var start = Text.LastIndexOf('<', Math.Min(position, Text.Length - 1));
        if (start < 0 || !Text.AsSpan(start + 1).StartsWith(element.Name.LocalName, StringComparison.Ordinal)) throw new OperationException("merge_span", "Parsed identity differs from its source span.");
        var firstEnd = OpenEnd(start); var empty = Text[firstEnd - 2] == '/';
        if (empty) return new(start, firstEnd, firstEnd, start, true);
        var depth = 1; var cursor = firstEnd;
        while (depth != 0)
        {
            var token = Text.IndexOf('<', cursor); if (token < 0) throw new OperationException("merge_span", "Missing closing XML delimiter.");
            var tail = Text.AsSpan(token);
            string? terminator = tail.StartsWith("<![CDATA[", StringComparison.Ordinal) ? "]]>" : tail.StartsWith("<!--", StringComparison.Ordinal) ? "-->" : tail.StartsWith("<?", StringComparison.Ordinal) ? "?>" : null;
            if (terminator is not null)
            {
                var end = Text.IndexOf(terminator, token + 2, StringComparison.Ordinal); if (end < 0) throw new OperationException("merge_span", "Incomplete XML token."); cursor = end + terminator.Length; continue;
            }
            var finish = OpenEnd(token);
            if (Text[token + 1] == '/') depth--;
            else if (Text[finish - 2] != '/') depth++;
            if (depth == 0) return new(start, finish, firstEnd, token, false);
            cursor = finish;
        }
        throw new OperationException("merge_span", "Invalid source span.");
    }
    public string Raw(XElement element) { var span = Locate(element); return Text[span.Start..span.End]; }
    public byte[] Apply(IEnumerable<Patch> source)
    {
        var patches = source.OrderBy(p => p.Start).ToArray(); var previous = 0;
        var result = new StringBuilder(Text.Length);
        foreach (var patch in patches)
        {
            if (patch.Start < previous || patch.End < patch.Start || patch.End > Text.Length) throw new OperationException("merge_span", "Overlapping or invalid merge patches.");
            result.Append(Text, previous, patch.Start - previous); result.Append(patch.Text); previous = patch.End;
        }
        result.Append(Text, previous, Text.Length - previous);
        var payload = Utf8.GetBytes(result.ToString());
        if (payload.Length > 64 * 1024 * 1024 - (bom ? 3 : 0)) throw new OperationException("merge_limit", "Merge result exceeds 64 MiB.");
        var bytes = bom ? new byte[] { 239, 187, 191 }.Concat(payload).ToArray() : payload;
        XmlProjects.Parse(bytes); return bytes;
    }
}
