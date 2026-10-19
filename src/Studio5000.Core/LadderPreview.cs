using System.Text.Json;
using System.Xml.Linq;

namespace Studio5000.Core;

public sealed record LadderPreviewRequest(string ExpectedSha256, string RoutineXPath, int Offset = 0, int Limit = 5);
public sealed record LadderNode(int Id, string Kind, string Instruction, string[] Operands, int X, int Y, int Width, int Height);
public sealed record LadderWire(int X1, int Y1, int X2, int Y2);
public sealed record LadderDiagram(int Width, int Height, LadderNode[] Nodes, LadderWire[] Wires, string Svg);
public sealed record LadderRungPreview(int Index, string? Number, string? OnlineEditType, string RawText, bool RawTruncated,
    string Comment, bool CommentTruncated, string Status, string[] Diagnostics, LadderDiagram? Diagram);
public sealed record LadderPreviewReport(ProjectInfo Project, string RoutineXPath, int Total, int Offset, int Limit,
    LadderRungPreview[] Rungs, string Boundary);

public static class LadderPreview
{
    private sealed record Part(string Instruction, string[] Operands, List<List<Part>>? Legs = null);
    private sealed class Parser(string text)
    {
        private int position, nodes;
        private char Current => position < text.Length ? text[position] : '\0';
        private void Space() { while (char.IsWhiteSpace(Current)) position++; }
        private OperationException Error(string message) => new("invalid_ladder", message + $" At character {position}.");
        private string Word()
        {
            var start = position;
            if (!(char.IsAsciiLetter(Current) || Current == '_')) throw Error("Expected an instruction name");
            while (char.IsAsciiLetterOrDigit(Current) || Current == '_') position++;
            return text[start..position];
        }
        private bool Keyword(string word)
        {
            Space();
            return text.AsSpan(position).StartsWith(word, StringComparison.OrdinalIgnoreCase) &&
                (position + word.Length == text.Length || !(char.IsAsciiLetterOrDigit(text[position + word.Length]) || text[position + word.Length] == '_'));
        }
        public List<Part> Parse()
        {
            if (text.Length > 65536) throw new OperationException("ladder_limit", "Rung text exceeds 64 KiB.");
            if (string.IsNullOrWhiteSpace(text)) throw Error("Rung text is empty");
            var result = Sequence(0, false, false); Space();
            if (Current == ';') { position++; Space(); }
            if (Current != '\0') throw Error("Unexpected trailing rung text");
            return result;
        }
        private List<Part> Sequence(int depth, bool bracket, bool keyword)
        {
            if (depth > 16) throw new OperationException("ladder_limit", "Branch nesting exceeds 16 levels.");
            var result = new List<Part>();
            while (true)
            {
                Space();
                if (Current is '\0' or ';' || (bracket && Current is ',' or ']') || keyword && (Keyword("NXB") || Keyword("BND"))) return result;
                if (++nodes > 256) throw new OperationException("ladder_limit", "A rung exceeds 256 instructions/branches.");
                if (Current == '[')
                {
                    position++; var legs = new List<List<Part>>();
                    while (true)
                    {
                        legs.Add(Sequence(depth + 1, true, false)); Space();
                        if (Current == ']') { position++; break; }
                        if (Current != ',') throw Error("Unclosed parallel branch");
                        position++;
                    }
                    result.Add(new("", [], legs)); continue;
                }
                var name = Word(); Space();
                if (name.Equals("BST", StringComparison.OrdinalIgnoreCase) && Current != '(')
                {
                    var legs = new List<List<Part>>();
                    while (true)
                    {
                        legs.Add(Sequence(depth + 1, false, true)); Space();
                        if (Keyword("BND")) { position += 3; break; }
                        if (!Keyword("NXB")) throw Error("Unclosed BST/NXB/BND branch");
                        position += 3;
                    }
                    result.Add(new("", [], legs)); continue;
                }
                if (Current != '(') throw Error("Expected an instruction operand list");
                position++; var start = position; var parentheses = 1; var brackets = 0; var quote = '\0'; var operands = new List<string>();
                while (parentheses > 0)
                {
                    if (Current == '\0') throw Error("Unclosed instruction operand list");
                    var c = Current;
                    if (quote != '\0')
                    {
                        if (c == '$' && position + 1 < text.Length) { position += 2; continue; }
                        if (c == quote)
                        {
                            if (position + 1 < text.Length && text[position + 1] == quote) { position += 2; continue; }
                            quote = '\0';
                        }
                    }
                    else if (c is '\'' or '"') quote = c;
                    else if (c == '(') { if (++parentheses > 32) throw new OperationException("ladder_limit", "Operand nesting exceeds 32 levels."); }
                    else if (c == ')')
                    {
                        if (--parentheses == 0)
                        {
                            if (brackets != 0) throw Error("Unbalanced operand index brackets");
                            if (position > start || operands.Count > 0) operands.Add(text[start..position].Trim());
                        }
                    }
                    else if (c == '[') { if (++brackets > 32) throw new OperationException("ladder_limit", "Operand index nesting exceeds 32 levels."); }
                    else if (c == ']' && --brackets < 0) throw Error("Unbalanced operand index brackets");
                    else if (c == ',' && parentheses == 1 && brackets == 0) { operands.Add(text[start..position].Trim()); start = position + 1; }
                    position++;
                }
                if (operands.Any(string.IsNullOrEmpty)) throw Error("Empty instruction operand");
                result.Add(new(name, operands.ToArray()));
            }
        }
    }

    public static LadderDiagram Render(string text)
    {
        var parts = new Parser(text).Parse();
        var nodes = new List<LadderNode>(); var wires = new List<LadderWire>();
        (int Width, int Height) Measure(List<Part> sequence)
        {
            var sizes = sequence.Select(PartSize).ToArray();
            return (sizes.Length == 0 ? 80 : sizes.Sum(s => s.Width), sizes.Length == 0 ? 100 : sizes.Max(s => s.Height));
        }
        (int Width, int Height) PartSize(Part part)
        {
            if (part.Legs is null) return (210, 100);
            var legs = part.Legs.Select(Measure).ToArray();
            return (legs.Max(s => s.Width) + 40, legs.Sum(s => s.Height));
        }
        void Wire(int x1, int y1, int x2, int y2) { if (x1 != x2 || y1 != y2) wires.Add(new(x1, y1, x2, y2)); }
        int Draw(List<Part> sequence, int x, int y)
        {
            if (sequence.Count == 0) { Wire(x, y + 45, x + 80, y + 45); return x + 80; }
            foreach (var part in sequence)
            {
                var size = PartSize(part);
                if (part.Legs is null)
                {
                    Wire(x, y + 45, x + 15, y + 45); Wire(x + 195, y + 45, x + 210, y + 45);
                    var kind = part.Instruction.ToUpperInvariant() switch { "XIC" => "contact", "XIO" => "contact_not",
                        "OTE" => "coil", "OTL" => "coil_latch", "OTU" => "coil_unlatch", _ => "block" };
                    nodes.Add(new(nodes.Count, kind, part.Instruction, part.Operands, x + 15, y + 10, 180, 75));
                }
                else
                {
                    var top = y;
                    foreach (var leg in part.Legs)
                    {
                        var end = Draw(leg, x + 20, top);
                        Wire(end, top + 45, x + size.Width - 20, top + 45);
                        top += Measure(leg).Height;
                    }
                    var lastY = top - Measure(part.Legs[^1]).Height + 45;
                    Wire(x, y + 45, x + 20, y + 45); Wire(x + size.Width - 20, y + 45, x + size.Width, y + 45);
                    Wire(x + 20, y + 45, x + 20, lastY); Wire(x + size.Width - 20, y + 45, x + size.Width - 20, lastY);
                }
                x += size.Width;
            }
            return x;
        }
        var bounds = Measure(parts);
        if (bounds.Width > 20000 || bounds.Height > 20000) throw new OperationException("ladder_limit", "Diagram dimensions exceed 20000 pixels.");
        var width = bounds.Width + 80; var height = bounds.Height + 40;
        Wire(15, 10, 15, height - 10); Wire(width - 15, 10, width - 15, height - 10);
        Wire(15, 65, 40, 65); var output = Draw(parts, 40, 20); Wire(output, 65, width - 15, 65);
        return new(width, height, nodes.ToArray(), wires.ToArray(), Svg(width, height, nodes, wires));
    }

    private static string Svg(int width, int height, List<LadderNode> nodes, List<LadderWire> wires)
    {
        XNamespace ns = "http://www.w3.org/2000/svg";
        var svg = new XElement(ns + "svg", new XAttribute("width", width), new XAttribute("height", height), new XAttribute("viewBox", $"0 0 {width} {height}"),
            new XElement(ns + "title", "Read-only ladder structure; no live power-flow or execution state"),
            new XElement(ns + "rect", new XAttribute("width", "100%"), new XAttribute("height", "100%"), new XAttribute("fill", "white")));
        foreach (var wire in wires) svg.Add(new XElement(ns + "line", new XAttribute("x1", wire.X1), new XAttribute("y1", wire.Y1), new XAttribute("x2", wire.X2), new XAttribute("y2", wire.Y2), new XAttribute("stroke", "#435165"), new XAttribute("stroke-width", 2)));
        foreach (var node in nodes)
        {
            var group = new XElement(ns + "g", new XElement(ns + "title", node.Instruction + "(" + string.Join(",", node.Operands) + ")"));
            group.Add(new XElement(ns + "rect", new XAttribute("x", node.X), new XAttribute("y", node.Y), new XAttribute("width", node.Width), new XAttribute("height", node.Height), new XAttribute("rx", 4), new XAttribute("fill", "#f3f6fa"), new XAttribute("stroke", "#435165")));
            var symbol = node.Kind switch { "contact" => "—| |—", "contact_not" => "—|/|—", "coil" => "—( )—", "coil_latch" => "—(L)—", "coil_unlatch" => "—(U)—", _ => node.Instruction };
            void Text(int y, string value) => group.Add(new XElement(ns + "text", new XAttribute("x", node.X + node.Width / 2), new XAttribute("y", y), new XAttribute("text-anchor", "middle"), new XAttribute("font-family", "Consolas, monospace"), new XAttribute("font-size", 12), value));
            Text(node.Y + 18, node.Instruction); Text(node.Y + 39, symbol);
            var operands = string.Join(", ", node.Operands); Text(node.Y + 61, operands.Length > 23 ? operands[..22] + "…" : operands);
            svg.Add(group);
        }
        return svg.ToString(SaveOptions.DisableFormatting);
    }

    public static LadderPreviewReport Build(ProjectInfo project, XElement routine, LadderPreviewRequest request)
    {
        if (request.Offset is < 0 or > 100000 || request.Limit is < 1 or > 20)
            throw new OperationException("invalid_ladder_query", "Use offset 0..100000 and limit 1..20.");
        var all = routine.Elements("RLLContent").SelectMany(e => e.Elements("Rung")).ToArray();
        var rows = all.Skip(request.Offset).Take(request.Limit).Select((rung, i) =>
        {
            var text = (string?)rung.Element("Text") ?? ""; var comment = (string?)rung.Element("Comment") ?? "";
            LadderDiagram? diagram = null; string[] diagnostics = []; var status = "rendered";
            try { diagram = Render(text); }
            catch (OperationException ex) { status = ex.Code == "ladder_limit" ? "unsupported" : "invalid"; diagnostics = [ex.Message]; }
            return new LadderRungPreview(request.Offset + i, (string?)rung.Attribute("Number"), (string?)rung.Parent?.Attribute("OnlineEditType"),
                text.Length > 65536 ? text[..65536] : text, text.Length > 65536, comment.Length > 4096 ? comment[..4096] : comment, comment.Length > 4096, status, diagnostics, diagram);
        }).ToArray();
        var result = new LadderPreviewReport(project, request.RoutineXPath, all.Length, request.Offset, request.Limit, rows,
            "Read-only structural rendering of balanced instruction calls and bracket or BST/NXB/BND branches. Unknown instructions/AOIs are generic blocks; operands are preserved, not evaluated. No instruction validation, live power-flow, simulation, native Studio acceptance or controller connection.");
        if (JsonSerializer.SerializeToUtf8Bytes(result, ProjectService.Json).Length > 4 * 1024 * 1024)
            throw new OperationException("ladder_page_too_large", "Ladder preview exceeds 4 MiB; request a smaller limit.");
        return result;
    }
}
