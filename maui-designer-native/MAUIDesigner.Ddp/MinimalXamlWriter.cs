using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

using MAUIDesigner.Fresh.Core.Documents;

namespace MAUIDesigner.Ddp;

/// <summary>
/// Writes the current document back as the ORIGINAL text with only the edits applied - the
/// child-model obligation "an edit changes only what it edits" (OpenDevelop designer-common.md,
/// "Document ownership modes"). The regular writer regenerates the whole file (attributes in
/// dictionary order, whitespace and comments normalised), so the first designer save reformatted
/// every page.
/// <para>
/// It patches TEXT, not an XLinq tree: XLinq cannot preserve whitespace between attributes. Each
/// node read from the source is located by its line info; its start tag is patched attribute by
/// attribute, and its children are rebuilt from their own original text, keeping the text between
/// them (whitespace, comments, non-visual elements) where it was. New nodes are serialised with the
/// indentation of their siblings. Anything it cannot express returns null, and the caller falls
/// back to the regular writer - a reformatted file, never a wrong one.
/// </para>
/// </summary>
public static class MinimalXamlWriter
{
    const string XamlNamespace = "http://schemas.microsoft.com/winfx/2009/xaml";

    public static string? Write(string originalText, IReadOnlyDictionary<string, XElement> sourceMap, DesignerDocument current)
    {
        try
        {
            var context = new Context(originalText, sourceMap, current);
            if (!sourceMap.TryGetValue(current.Root.Id.Value, out XElement? rootElement))
                return null;
            var span = context.ElementSpan(rootElement);
            string root = context.Render(current.Root, "");
            return originalText[..span.Start] + root + originalText[span.End..];
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or XmlException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    sealed class Context
    {
        readonly string text;
        readonly IReadOnlyDictionary<string, XElement> map;
        readonly DesignerDocument document;
        readonly int[] lineStarts;

        public Context(string text, IReadOnlyDictionary<string, XElement> map, DesignerDocument document)
        {
            this.text = text;
            this.map = map;
            this.document = document;
            var starts = new List<int> { 0 };
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                    starts.Add(i + 1);
            }

            lineStarts = starts.ToArray();
        }

        /// <summary>The node's text: its original element patched, or a fresh serialisation.</summary>
        public string Render(DesignerNode node, string indent)
        {
            if (!map.TryGetValue(node.Id.Value, out XElement? element))
                return Serialize(node, indent);

            var span = ElementSpan(element);
            var startTag = StartTagSpan(span.Start);
            string patchedStartTag = PatchAttributes(text[startTag.Start..startTag.End], element, node);

            var visualChildren = node.Children.Where(c => c.ParentPropertyName is null).ToList();
            var originalChildren = element.Descendants()
                .Where(d => map.Values.Contains(d) && NearestMappedAncestor(d) == element)
                .ToList();
            var originalIds = originalChildren.Select(IdOf).ToList();
            var currentIds = visualChildren.Select(c => c.Id.Value).ToList();

            // Property-element children (Border.Content style with a named visual property) are
            // not reordered by the designer; only default content is.
            if (node.Children.Any(c => c.ParentPropertyName is not null))
            {
                if (!node.Children.Where(c => c.ParentPropertyName is not null).All(c => map.ContainsKey(c.Id.Value)))
                    throw new InvalidOperationException("Not expressible as a minimal edit.");
            }

            bool selfClosing = text[startTag.End - 2] == '/';
            if (selfClosing)
            {
                if (visualChildren.Count == 0)
                    return patchedStartTag;
                // A childless element gains its first children: open it up.
                string childIndent = indent + "    ";
                var sb = new StringBuilder();
                sb.Append(patchedStartTag[..^2].TrimEnd()).Append('>');
                foreach (DesignerNode child in visualChildren)
                    sb.Append('\n').Append(childIndent).Append(Render(child, childIndent));
                sb.Append('\n').Append(indent).Append("</").Append(QualifiedName(element)).Append('>');
                return sb.ToString();
            }

            // All default-content children must share one container element (the element itself,
            // or one X.Children/X.Content property element) for slot-based rebuilding.
            var containers = originalChildren.Select(c => c.Parent).Distinct().ToList();
            if (containers.Count > 1)
                throw new InvalidOperationException("Not expressible as a minimal edit.");

            var body = new StringBuilder();
            body.Append(patchedStartTag);
            int cursor = startTag.End;
            if (originalChildren.Count == 0)
            {
                // No original children to take slots from: append before the end tag.
                int endTagStart = text.LastIndexOf("</", span.End - 1, StringComparison.Ordinal);
                string inner = text[cursor..endTagStart];
                string childIndent = LineIndent(span.Start) + "    ";
                body.Append(inner.TrimEnd(' ', '\t'));
                foreach (DesignerNode child in visualChildren)
                    body.Append(inner.EndsWith('\n') ? "" : "\n").Append(childIndent).Append(Render(child, childIndent)).Append('\n');
                if (visualChildren.Count > 0)
                    body.Append(LineIndent(span.Start));
                else
                    body.Append(inner[inner.TrimEnd(' ', '\t').Length..]);
                body.Append(text[endTagStart..span.End]);
                return body.ToString();
            }

            var childSpans = originalChildren.Select(ElementSpan).ToList();
            string defaultIndent = LineIndent(childSpans[0].Start);
            // Slots: text before the first child, between children, after the last child.
            var gaps = new List<string> { text[cursor..childSpans[0].Start] };
            for (int i = 1; i < childSpans.Count; i++)
                gaps.Add(text[childSpans[i - 1].End..childSpans[i].Start]);
            string tail = text[childSpans[^1].End..span.End];

            if (originalIds.SequenceEqual(currentIds))
            {
                // Same children in the same order: keep every gap, recurse into each child.
                for (int i = 0; i < visualChildren.Count; i++)
                    body.Append(gaps[i]).Append(Render(visualChildren[i], LineIndent(childSpans[i].Start)));
                body.Append(tail);
                return body.ToString();
            }

            // Changed: a removed child takes its own line with it - the gap AFTER it (its newline
            // and the next line's indent), or the gap before it when it was the last child - so a
            // blank line or comment before it stays. The remaining gaps keep their order; extra
            // children reuse the last gap ("\n" + sibling indent).
            var dropped = new HashSet<int>();
            for (int i = 0; i < originalIds.Count; i++)
            {
                if (currentIds.Contains(originalIds[i]))
                    continue;
                dropped.Add(i < originalIds.Count - 1 ? i + 1 : i);
            }

            var keptGaps = gaps.Where((_, i) => !dropped.Contains(i) || (i == 0 && dropped.Count == originalIds.Count)).ToList();

            string separator = gaps.Count > 1 ? gaps[^1] : "\n" + defaultIndent;
            for (int i = 0; i < visualChildren.Count; i++)
            {
                string gap = i < keptGaps.Count ? keptGaps[i] : separator;
                string childIndent = map.TryGetValue(visualChildren[i].Id.Value, out XElement? existing)
                    ? LineIndent(ElementSpan(existing).Start)
                    : defaultIndent;
                body.Append(gap).Append(Render(visualChildren[i], childIndent));
            }

            if (visualChildren.Count == 0)
            {
                // Everything removed: close right after the gap before the first child's line.
                body.Append(gaps[0].TrimEnd(' ', '\t'));
                body.Append(tail.TrimStart('\r', '\n'));
            }
            else
            {
                body.Append(tail);
            }

            return body.ToString();
        }

        XElement? NearestMappedAncestor(XElement element)
        {
            for (XElement? parent = element.Parent; parent != null; parent = parent.Parent)
            {
                if (map.Values.Contains(parent))
                    return parent;
            }

            return null;
        }

        string IdOf(XElement element) => map.First(pair => pair.Value == element).Key;

        /// <summary>Patches only the attributes whose values differ; appends new ones, removes gone ones.</summary>
        string PatchAttributes(string startTag, XElement element, DesignerNode node)
        {
            var wanted = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in node.Properties)
                wanted[property.Key] = property.Value.Text;
            if (node.Bounds is { } bounds)
            {
                wanted["AbsoluteLayout.LayoutBounds"] = string.Join(",", new[] { bounds.X, bounds.Y, bounds.Width, bounds.Height }
                    .Select(v => v.ToString(CultureInfo.InvariantCulture)));
                wanted["AbsoluteLayout.LayoutFlags"] = "None";
            }

            var attributes = ParseAttributes(startTag);
            var result = new StringBuilder(startTag);
            // Right to left, so earlier offsets stay valid.
            foreach (var attribute in attributes.OrderByDescending(a => a.NameStart))
            {
                if (attribute.Name.StartsWith("xmlns", StringComparison.Ordinal))
                    continue;
                if (!wanted.TryGetValue(attribute.Name, out string? value))
                {
                    result.Remove(attribute.LeadingSpaceStart, attribute.End - attribute.LeadingSpaceStart);
                    continue;
                }

                string current = element.Attributes().FirstOrDefault(a => Qualified(element, a) == attribute.Name)?.Value ?? "";
                if (current != value)
                {
                    result.Remove(attribute.ValueStart, attribute.ValueEnd - attribute.ValueStart);
                    result.Insert(attribute.ValueStart, Escape(value, attribute.Quote));
                }
            }

            var present = attributes.Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
            var added = wanted.Where(pair => !present.Contains(pair.Key)).ToList();
            if (added.Count > 0)
            {
                string tagText = result.ToString();
                int insertAt = attributes.Count > 0 && attributes.All(a => wanted.ContainsKey(a.Name) || a.Name.StartsWith("xmlns", StringComparison.Ordinal))
                    ? tagText.Length - (tagText.EndsWith("/>", StringComparison.Ordinal) ? 2 : 1)
                    : tagText.Length - (tagText.EndsWith("/>", StringComparison.Ordinal) ? 2 : 1);
                // Before any whitespace that precedes "/>" or ">".
                while (insertAt > 0 && char.IsWhiteSpace(tagText[insertAt - 1]))
                    insertAt--;
                var insert = new StringBuilder();
                foreach (var pair in added.OrderBy(p => p.Key == "x:Name" ? 0 : 1).ThenBy(p => p.Key, StringComparer.Ordinal))
                    insert.Append(' ').Append(pair.Key).Append("=\"").Append(Escape(pair.Value, '"')).Append('"');
                result.Insert(insertAt, insert.ToString());
            }

            return result.ToString();
        }

        static string Qualified(XElement element, XAttribute attribute)
        {
            string? prefix = attribute.Name.Namespace == XNamespace.None ? null : element.GetPrefixOfNamespace(attribute.Name.Namespace);
            return string.IsNullOrEmpty(prefix) ? attribute.Name.LocalName : prefix + ":" + attribute.Name.LocalName;
        }

        static string QualifiedName(XElement element)
        {
            string? prefix = element.GetPrefixOfNamespace(element.Name.Namespace);
            return string.IsNullOrEmpty(prefix) ? element.Name.LocalName : prefix + ":" + element.Name.LocalName;
        }

        /// <summary>A new node: serialised like its siblings would be written by hand.</summary>
        string Serialize(DesignerNode node, string indent)
        {
            string name = ElementName(node.ControlType.XamlNamespace, node.ControlType.XamlName);
            var sb = new StringBuilder("<").Append(name);
            var properties = node.Properties.ToDictionary(p => p.Key, p => p.Value.Text, StringComparer.Ordinal);
            if (node.Bounds is { } bounds)
            {
                properties["AbsoluteLayout.LayoutBounds"] = string.Join(",", new[] { bounds.X, bounds.Y, bounds.Width, bounds.Height }
                    .Select(v => v.ToString(CultureInfo.InvariantCulture)));
                properties["AbsoluteLayout.LayoutFlags"] = "None";
            }

            foreach (var pair in properties.OrderBy(p => p.Key == "x:Name" ? 0 : 1).ThenBy(p => p.Key, StringComparer.Ordinal))
                sb.Append(' ').Append(pair.Key).Append("=\"").Append(Escape(pair.Value, '"')).Append('"');
            if (node.Children.Length == 0 && node.PreservedContent.IsDefaultOrEmpty)
                return sb.Append(" />").ToString();
            sb.Append('>');
            string inner = indent + "    ";
            foreach (XamlSyntaxFragment fragment in node.PreservedContent.IsDefault ? [] : node.PreservedContent)
                sb.Append('\n').Append(inner).Append(fragment.Xml);
            foreach (var group in node.Children.Where(c => c.ParentPropertyName is not null).GroupBy(c => c.ParentPropertyName!))
            {
                sb.Append('\n').Append(inner).Append('<').Append(name).Append('.').Append(group.Key).Append('>');
                foreach (DesignerNode child in group)
                    sb.Append('\n').Append(inner).Append("    ").Append(Serialize(child, inner + "    "));
                sb.Append('\n').Append(inner).Append("</").Append(name).Append('.').Append(group.Key).Append('>');
            }

            foreach (DesignerNode child in node.Children.Where(c => c.ParentPropertyName is null))
                sb.Append('\n').Append(inner).Append(Serialize(child, inner));
            return sb.Append('\n').Append(indent).Append("</").Append(name).Append('>').ToString();
        }

        string ElementName(string xamlNamespace, string localName)
        {
            foreach (var pair in document.Namespaces)
            {
                if (pair.Value == xamlNamespace)
                    return pair.Key.Length == 0 ? localName : pair.Key + ":" + localName;
            }

            throw new InvalidOperationException($"No prefix for '{xamlNamespace}'.");
        }

        static string Escape(string value, char quote)
        {
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                sb.Append(c switch
                {
                    '&' => "&amp;",
                    '<' => "&lt;",
                    '"' when quote == '"' => "&quot;",
                    '\'' when quote == '\'' => "&apos;",
                    _ => c.ToString(),
                });
            }

            return sb.ToString();
        }

        string LineIndent(int offset)
        {
            int lineStart = text.LastIndexOf('\n', Math.Max(0, offset - 1)) + 1;
            int i = lineStart;
            while (i < offset && (text[i] == ' ' || text[i] == '\t'))
                i++;
            return text[lineStart..i];
        }

        /// <summary>[Start, End) of the element in the original text.</summary>
        public (int Start, int End) ElementSpan(XElement element)
        {
            int start = StartOffset(element);
            var tag = StartTagSpan(start);
            if (text[tag.End - 2] == '/')
                return (start, tag.End);
            int depth = 1;
            int i = tag.End;
            while (i < text.Length)
            {
                if (Starts(i, "<!--")) { i = text.IndexOf("-->", i, StringComparison.Ordinal) + 3; continue; }
                if (Starts(i, "<![CDATA[")) { i = text.IndexOf("]]>", i, StringComparison.Ordinal) + 3; continue; }
                if (Starts(i, "<?")) { i = text.IndexOf("?>", i, StringComparison.Ordinal) + 2; continue; }
                if (Starts(i, "</"))
                {
                    int close = text.IndexOf('>', i);
                    depth--;
                    if (depth == 0)
                        return (start, close + 1);
                    i = close + 1;
                    continue;
                }

                if (text[i] == '<')
                {
                    var inner = StartTagSpan(i);
                    if (text[inner.End - 2] != '/')
                        depth++;
                    i = inner.End;
                    continue;
                }

                i++;
            }

            throw new InvalidOperationException("Unterminated element.");
        }

        bool Starts(int i, string token) => string.CompareOrdinal(text, i, token, 0, token.Length) == 0;

        int StartOffset(XElement element)
        {
            var info = (IXmlLineInfo)element;
            if (!info.HasLineInfo())
                throw new InvalidOperationException("The source has no line info.");
            int offset = lineStarts[info.LineNumber - 1] + info.LinePosition - 1;
            // Line info points at the name; the element starts at the '<' before it.
            while (offset > 0 && text[offset] != '<')
                offset--;
            return offset;
        }

        /// <summary>[Start, End) of the start tag beginning at <paramref name="start"/> ('&lt;').</summary>
        (int Start, int End) StartTagSpan(int start)
        {
            char quote = '\0';
            for (int i = start + 1; i < text.Length; i++)
            {
                char c = text[i];
                if (quote != '\0')
                {
                    if (c == quote)
                        quote = '\0';
                }
                else if (c == '"' || c == '\'')
                {
                    quote = c;
                }
                else if (c == '>')
                {
                    return (start, i + 1);
                }
            }

            throw new InvalidOperationException("Unterminated start tag.");
        }

        sealed record Attribute(string Name, int LeadingSpaceStart, int NameStart, int ValueStart, int ValueEnd, int End, char Quote);

        /// <summary>The attributes of a start tag, with offsets relative to the tag text.</summary>
        static List<Attribute> ParseAttributes(string tag)
        {
            var list = new List<Attribute>();
            int i = 1;
            while (i < tag.Length && !char.IsWhiteSpace(tag[i]) && tag[i] != '/' && tag[i] != '>')
                i++;
            while (i < tag.Length)
            {
                int spaceStart = i;
                while (i < tag.Length && char.IsWhiteSpace(tag[i]))
                    i++;
                if (i >= tag.Length || tag[i] == '/' || tag[i] == '>')
                    break;
                int nameStart = i;
                while (i < tag.Length && tag[i] != '=' && !char.IsWhiteSpace(tag[i]))
                    i++;
                string name = tag[nameStart..i];
                while (i < tag.Length && (char.IsWhiteSpace(tag[i]) || tag[i] == '='))
                    i++;
                char quote = tag[i];
                int valueStart = i + 1;
                int valueEnd = tag.IndexOf(quote, valueStart);
                i = valueEnd + 1;
                list.Add(new Attribute(name, spaceStart, nameStart, valueStart, valueEnd, i, quote));
            }

            return list;
        }
    }
}
