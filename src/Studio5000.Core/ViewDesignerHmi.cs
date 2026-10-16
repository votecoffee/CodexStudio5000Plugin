using System.Text;
using System.Text.RegularExpressions;

namespace Studio5000.Core;

// Lossless structural reader for the documented .hmi export format (9324-RM001).
// This deliberately does not claim to validate HMICatalog property types or native build semantics.
public static class ViewDesignerHmi
{
    private sealed record Token(string Text, int Start, int End, bool Quoted = false);
    private sealed record Span(string Selector, int Start, int End);
    private sealed record Parsed(HmiDocument Document, Span[] Spans);

    public static HmiDocument Parse(byte[] bytes) => Read(Decode(bytes).Text).Document;

    public static byte[] Apply(byte[] bytes, ViewDesignerEdit[] edits)
    {
        var (text, encoding, preamble) = Decode(bytes);
        var parsed = Read(text);
        var replacements = new List<(Span Span, string Value)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var edit in edits)
        {
            if (!seen.Add(edit.Selector)) throw Error("An edit batch cannot target a property twice.");
            var span = parsed.Spans.SingleOrDefault(p => p.Selector.Equals(edit.Selector, StringComparison.OrdinalIgnoreCase))
                ?? throw new OperationException("not_found", "Property selector does not exist: " + edit.Selector);
            ValidateValue(edit.Value);
            replacements.Add((span, edit.Value));
        }
        foreach (var (span, value) in replacements.OrderByDescending(p => p.Span.Start))
            text = text[..span.Start] + value + text[span.End..];
        Read(text);
        byte[] output = [..preamble, ..encoding.GetBytes(text)];
        if (output.Length > 4 * 1024 * 1024) throw Error("Edited .hmi file exceeds 4 MiB.");
        return output;
    }

    private static (string Text, Encoding Encoding, byte[] Preamble) Decode(byte[] bytes)
    {
        if (bytes.Length > 4 * 1024 * 1024) throw Error("An .hmi file exceeds 4 MiB.");
        Encoding encoding; var skip = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) { encoding = new UnicodeEncoding(false, false, true); skip = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) { encoding = new UnicodeEncoding(true, false, true); skip = 2; }
        else { encoding = new UTF8Encoding(false, true); if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) skip = 3; }
        try { return (encoding.GetString(bytes, skip, bytes.Length - skip), encoding, bytes[..skip]); }
        catch (DecoderFallbackException) { throw Error("Expected UTF-8 or BOM-marked UTF-16 .hmi text."); }
    }

    private static List<Token> Tokens(string text)
    {
        var tokens = new List<Token>();
        for (var i = 0; i < text.Length;)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }
            var start = i;
            if (i + 1 < text.Length && text[i..(i + 2)] == "//")
            { while (i < text.Length && text[i] != '\n') i++; continue; }
            if (i + 1 < text.Length && text[i..(i + 2)] is "/*" or "(*")
            {
                var end = text.IndexOf(text[i] == '/' ? "*/" : "*)", i + 2, StringComparison.Ordinal);
                if (end < 0) throw Error("Unclosed comment.");
                i = end + 2; continue;
            }
            if (text[i] is '"' or '\'')
            {
                var quote = text[i++]; var closed = false;
                while (i < text.Length)
                {
                    if (text[i] == '$') { i += 2; continue; }
                    if (text[i++] != quote) continue;
                    if (i < text.Length && text[i] == quote) { i++; continue; }
                    closed = true; break;
                }
                if (!closed || i > text.Length) throw Error("Unclosed string.");
                tokens.Add(new(text[start..i], start, i, true)); continue;
            }
            if (i + 1 < text.Length && text[i..(i + 2)] is ":=" or "->" or "::") i += 2;
            else if (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '^')
            { while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '^')) i++; }
            else i++;
            tokens.Add(new(text[start..i], start, i));
            if (tokens.Count > 250000) throw Error("Too many .hmi tokens.");
        }
        return tokens;
    }

    private static void ValidateValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 65536) throw Error("Property value is empty or too large.");
        var t = Tokens(value); var arrow = t.FindIndex(x => x.Text == "->");
        var first = arrow < 0 ? t : t.Take(arrow).ToList();
        var literal = first.Count == 1 && first[0].Quoted ||
            first.Count > 0 && Regex.IsMatch(value[first[0].Start..first[^1].End], @"\A(?:[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?|true|false|\^?[A-Za-z_]\w*(?:\.\^?[A-Za-z_]\w*)+)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!literal || arrow >= 0 && (t.Count != arrow + 2 || !t[^1].Quoted))
            throw Error("Use a quoted string, number, boolean or enum, optionally followed by -> and a quoted binding expression.");
        // Reject comments/whitespace tricks that could disguise statement delimiters in the request.
        if (t.Any(x => !x.Quoted && x.Text is ";" or "{" or "}" or ":=")) throw Error("Value must be a single property expression.");
    }

    private static Parsed Read(string text)
    {
        var tokens = Tokens(text); var index = 0;
        var objects = new List<HmiObject>(); var props = new List<HmiProperty>(); var spans = new List<Span>();
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Scope(string parent, string end, int depth, bool parameters = false)
        {
            if (depth > 48) throw Error(".hmi nesting exceeds 48 levels.");
            while (index < tokens.Count && tokens[index].Text != end)
            {
                if (tokens[index].Text is ";" or ",") { index++; continue; }
                var header = new List<Token>();
                while (index < tokens.Count && tokens[index].Text is not (":=" or "{" or "(" or ";" or "," or "}" or ")")) header.Add(tokens[index++]);
                if (header.Count == 0 || index == tokens.Count) throw Error("Incomplete .hmi statement.");
                var next = tokens[index].Text;
                if (next == ":=")
                {
                    if (header.Count != 1 || parent.Length == 0) throw Error("Unsupported property declaration.");
                    index++; var valueStart = index; var balance = 0;
                    while (index < tokens.Count)
                    {
                        var s = tokens[index].Text;
                        if (balance == 0 && (s == ";" || parameters && s is "," or ")")) break;
                        if (!tokens[index].Quoted && s is "{" or "}" or ":=") throw Error("Unterminated property value.");
                        if (!tokens[index].Quoted && s is "(" or "[") balance++;
                        if (!tokens[index].Quoted && s is ")" or "]") balance--;
                        if (balance < 0) throw Error("Unbalanced property value.");
                        index++;
                    }
                    if (valueStart == index || index == tokens.Count || balance != 0) throw Error("Incomplete property value.");
                    var selector = parent + (parameters ? "/parameter:" : "/@") + Clean(header[0].Text);
                    if (!identities.Add(selector)) throw Error("Duplicate property or object identity: " + selector);
                    var start = tokens[valueStart].Start; var finish = tokens[index - 1].End;
                    props.Add(new(selector, text[start..finish])); spans.Add(new(selector, start, finish));
                    if (tokens[index].Text != ")") index++;
                    continue;
                }
                if (next == ";") { index++; continue; } // namespace/using and declarations remain untouched
                if (next is not ("{" or "(")) throw Error("Unexpected .hmi delimiter.");
                if (parameters) throw Error("Nested parameter declaration is unsupported.");
                var type = Clean(header[0].Text); var name = header.Count > 1 ? Clean(header[^1].Text) : null;
                var selectorBlock = parent + "/" + Uri.EscapeDataString(type) + (name is null ? "" : ":" + Uri.EscapeDataString(name));
                if (!identities.Add(selectorBlock)) throw Error("Duplicate object identity: " + selectorBlock);
                objects.Add(new(selectorBlock, type, name)); index++;
                if (next == "(") { Scope(selectorBlock, ")", depth + 1, true); if (index < tokens.Count && tokens[index].Text == "{") index++; else continue; }
                Scope(selectorBlock, "}", depth + 1);
            }
            if (end.Length != 0)
            { if (index >= tokens.Count || tokens[index].Text != end) throw Error("Unclosed .hmi block."); index++; }
        }
        Scope("", "", 0);
        if (!objects.Any()) throw Error("No .hmi objects found.");
        return new(new(text, objects.ToArray(), props.ToArray()), spans.ToArray());
    }
    private static string Clean(string value) => value.TrimStart('^');
    private static OperationException Error(string message) => new("invalid_hmi", message);
}
