using System.Xml;

namespace Studio5000.Core;

/// <summary>Bounded branch structure only; not a native instruction/compiler check.</summary>
public static class LadderBranchGrammar
{
    public const string CheckName = "rll-branch-grammar";
    private sealed class Branch(bool keyword)
    {
        internal bool Keyword = keyword;
        internal bool Content;
        internal int Separators;
    }
    public static void Validate(string text)
    {
        if (text.Length > 65536) throw new OperationException("ladder_limit", "Rung text exceeds 64 KiB.");
        var branches = new Stack<Branch>(); var parentheses = 0; var quote = '\0'; var position = 0;
        OperationException Error(string message) => new("invalid_ladder", message + $" At character {position}.");
        void Content() { if (branches.TryPeek(out var branch)) branch.Content = true; }
        void Begin(bool keyword)
        {
            if (branches.Count >= 16) throw new OperationException("ladder_limit", "Branch nesting exceeds 16 levels.");
            branches.Push(new(keyword));
        }
        void Next(bool keyword)
        {
            if (!branches.TryPeek(out var branch) || branch.Keyword != keyword) throw Error("Unexpected branch separator");
            if (!branch.Content) throw Error("A parallel branch leg cannot be empty");
            branch.Separators++; branch.Content = false;
        }
        void End(bool keyword)
        {
            if (!branches.TryPeek(out var branch) || branch.Keyword != keyword) throw Error("Unexpected branch terminator");
            if (!branch.Content) throw Error("A parallel branch leg cannot be empty");
            if (branch.Separators == 0) throw Error(keyword
                ? "A BST branch needs at least two legs separated by NXB"
                : "A bracket branch needs at least two legs separated by a comma at its own depth");
            branches.Pop(); Content();
        }
        for (; position < text.Length; position++)
        {
            var c = text[position];
            if (quote != '\0')
            {
                if (c == '$' && position + 1 < text.Length) { position++; continue; }
                if (c == quote)
                {
                    if (position + 1 < text.Length && text[position + 1] == quote) { position++; continue; }
                    quote = '\0';
                }
                continue;
            }
            if (parentheses != 0)
            {
                if (c is '\'' or '"') quote = c;
                else if (c == '(' && ++parentheses > 32) throw new OperationException("ladder_limit", "Operand nesting exceeds 32 levels.");
                else if (c == ')') parentheses--;
                continue; // Operand commas and array brackets never separate branch legs.
            }
            if (char.IsWhiteSpace(c)) continue;
            if (c == '(') { parentheses = 1; Content(); continue; }
            if (c == ')') throw Error("Unexpected instruction operand terminator");
            if (c == '[') { Begin(false); continue; }
            if (c == ']') { End(false); continue; }
            if (c == ',') { Next(false); continue; }
            if (c == ';' && branches.Count != 0) throw Error("Unclosed parallel branch");
            if (char.IsAsciiLetter(c) || c == '_')
            {
                var start = position;
                while (position + 1 < text.Length && (char.IsAsciiLetterOrDigit(text[position + 1]) || text[position + 1] == '_')) position++;
                var word = text[start..(position + 1)]; var next = position + 1;
                while (next < text.Length && char.IsWhiteSpace(text[next])) next++;
                if (next == text.Length || text[next] != '(')
                {
                    if (word.Equals("BST", StringComparison.OrdinalIgnoreCase)) { Begin(true); continue; }
                    if (word.Equals("NXB", StringComparison.OrdinalIgnoreCase)) { Next(true); continue; }
                    if (word.Equals("BND", StringComparison.OrdinalIgnoreCase)) { End(true); continue; }
                }
            }
            Content();
        }
        if (parentheses != 0 || quote != '\0') throw Error("Unclosed instruction operand list");
        if (branches.Count != 0) throw Error("Unclosed parallel branch");
    }

    // Consume the already bounded, DTD-disabled import reader without loading
    // an entire component export or interpreting comments/ST/encoded payloads.
    internal static void ValidateImport(XmlReader reader)
    {
        var contentDepth = -1; var rungDepth = -1; var rungNumber = "?";
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (reader.Depth == contentDepth) { contentDepth = -1; rungDepth = -1; }
                else if (reader.Depth == rungDepth) rungDepth = -1;
            }
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (reader.LocalName == "RLLContent" && !reader.IsEmptyElement) { contentDepth = reader.Depth; rungDepth = -1; }
            else if (contentDepth >= 0 && reader.Depth == contentDepth + 1 && reader.LocalName == "Rung" && !reader.IsEmptyElement)
            {
                rungDepth = reader.Depth; rungNumber = reader.GetAttribute("Number") ?? "?";
                if (rungNumber.Length > 64) rungNumber = rungNumber[..64];
            }
            else if (rungDepth >= 0 && reader.Depth == rungDepth + 1 && reader.LocalName == "Text")
            {
                using var text = reader.ReadSubtree(); text.MoveToContent();
                try { Validate(text.ReadElementContentAsString()); }
                catch (OperationException ex) { throw new OperationException(ex.Code, $"Import rung {rungNumber}: {ex.Message}"); }
            }
        }
    }
}
