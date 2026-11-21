using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Sticky.Core;

[Flags]
public enum MarkdownTextStyle
{
    Bold = 1,
    Italic = 2,
    Strike = 4,
    Code = 8,
    Link = 16,
    Syntax = 32
}

public readonly record struct MarkdownStyleRange(int Start, int End, MarkdownTextStyle Style, int HeadingLevel = 0);

// Offsets refer to the original Markdown buffer. Rendering never edits that buffer.
public readonly record struct MarkdownReplacement(int Start, int End, string Text, string? ImageId = null, double ImageWidth = 280);

public sealed record MarkdownVisuals(List<MarkdownStyleRange> Styles, List<MarkdownReplacement> Replacements);

public static partial class MarkdownBody
{
    private static readonly MarkdownPipeline VisualPipeline = CreatePipeline().UsePreciseSourceLocation().Build();

    public static MarkdownVisuals GetVisuals(string source)
    {
        var styles = new List<MarkdownStyleRange>();
        var replacements = new List<MarkdownReplacement>();
        VisitBlocks(Markdown.Parse(source, VisualPipeline));
        replacements.Sort((left, right) => left.Start.CompareTo(right.Start));
        return new MarkdownVisuals(styles, replacements);

        void Style(int start, int end, MarkdownTextStyle style, int heading = 0)
        {
            if (start >= 0 && start < end && end <= source.Length)
                styles.Add(new MarkdownStyleRange(start, end, style, heading));
        }

        void Replace(int start, int end, string text = "", string? image = null, double width = 280)
        {
            if (start < 0 || start >= end || end > source.Length || source.AsSpan(start, end - start).IndexOfAny('\r', '\n') >= 0)
                return;
            Style(start, end, MarkdownTextStyle.Syntax);
            replacements.Add(new MarkdownReplacement(start, end, text, image, width));
        }

        void VisitBlocks(ContainerBlock container)
        {
            foreach (var block in container)
            {
                var start = block.Span.Start;
                var end = block.Span.End + 1;
                if (start < 0 || start >= end || end > source.Length) continue;
                switch (block)
                {
                    case HeadingBlock heading:
                        if (heading.IsSetext)
                        {
                            var marker = end;
                            while (marker > start && source[marker - 1] is not ('\n' or '\r')) marker--;
                            Style(start, marker, MarkdownTextStyle.Bold, heading.Level);
                            Replace(marker, end);
                        }
                        else
                        {
                            var body = SkipSpaces(start + heading.Level, end);
                            Style(body, end, MarkdownTextStyle.Bold, heading.Level);
                            Replace(start, body);
                            if (heading.Inline?.LastChild is { } last)
                            {
                                var suffix = last.Span.End + 1;
                                if (suffix >= body && suffix < end && source.AsSpan(suffix, end - suffix).Trim().Contains('#'))
                                    Replace(suffix, end);
                            }
                        }
                        break;
                    case ListItemBlock when source[start] is '-' or '+' or '*':
                        var content = SkipSpaces(start + 1, end);
                        Replace(start, content, "• ");
                        break;
                    case ListBlock { IsOrdered: true } list:
                        var ordinal = int.TryParse(list.OrderedStart, out var firstNumber) ? firstNumber : 1;
                        foreach (var listItem in list.OfType<ListItemBlock>())
                        {
                            var marker = listItem.Span.Start;
                            if (marker < start || marker >= end) continue;
                            var terminator = marker;
                            while (terminator < end && char.IsAsciiDigit(source[terminator])) terminator++;
                            if (terminator > marker && terminator < end && source[terminator] is '.' or ')')
                            {
                                var entry = SkipSpaces(terminator + 1, end);
                                Replace(marker, entry, FormattableString.Invariant($"{ordinal}. "));
                            }
                            ordinal++;
                        }
                        break;
                    case FencedCodeBlock fence:
                        Style(start, end, MarkdownTextStyle.Code);
                        var firstLineEnd = source.AsSpan(start, end - start).IndexOfAny('\r', '\n');
                        var fenceEnd = firstLineEnd < 0 ? end : start + firstLineEnd;
                        Replace(start, fenceEnd);
                        if (fence.ClosingFencedCharCount > 0 && firstLineEnd >= 0)
                        {
                            var closing = end;
                            while (closing > fenceEnd && source[closing - 1] is not ('\r' or '\n')) closing--;
                            Replace(closing, end);
                        }
                        break;
                    case CodeBlock:
                        Style(start, end, MarkdownTextStyle.Code);
                        break;
                    case ThematicBreakBlock:
                        Replace(start, end, "────────");
                        break;
                }
                if (block is ContainerBlock child) VisitBlocks(child);
                if (block is LeafBlock { Inline: { } inline }) VisitInlines(inline);
            }
        }

        void VisitInlines(ContainerInline parent)
        {
            foreach (var inline in parent)
            {
                var start = inline.Span.Start;
                var end = inline.Span.End + 1;
                if (start < 0 || start >= end || end > source.Length) continue;
                switch (inline)
                {
                    case EmphasisInline emphasis:
                        var count = emphasis.DelimiterCount;
                        if (end - start < 2 * count || source[start] != emphasis.DelimiterChar || source[end - 1] != emphasis.DelimiterChar)
                            break;
                        var style = emphasis.DelimiterChar == '~' ? MarkdownTextStyle.Strike : (MarkdownTextStyle)0;
                        if (emphasis.DelimiterChar is '*' or '_')
                        {
                            if (count >= 2) style |= MarkdownTextStyle.Bold;
                            if (count % 2 == 1) style |= MarkdownTextStyle.Italic;
                        }
                        Style(start + count, end - count, style);
                        Replace(start, start + count);
                        Replace(end - count, end);
                        break;
                    case CodeInline code:
                        var delimiter = code.DelimiterCount;
                        Style(start + delimiter, end - delimiter, MarkdownTextStyle.Code);
                        Replace(start, start + delimiter);
                        Replace(end - delimiter, end);
                        break;
                    case LinkInline link:
                        var url = link.GetDynamicUrl?.Invoke() ?? link.Url ?? "";
                        if (LocalImage(link, url) is { } asset)
                        {
                            Replace(start, end, image: asset, width: ImageWidth(link.Title));
                            continue;
                        }
                        if (link.FirstChild is { } first && link.LastChild is { } last)
                        {
                            Style(first.Span.Start, last.Span.End + 1, MarkdownTextStyle.Link);
                            Replace(start, first.Span.Start);
                            Replace(last.Span.End + 1, end);
                        }
                        break;
                    case AutolinkInline:
                        Style(start + 1, end - 1, MarkdownTextStyle.Link);
                        Replace(start, start + 1);
                        Replace(end - 1, end);
                        break;
                    case LiteralInline { IsFirstCharacterEscaped: true } when source[start] == '\\':
                        Replace(start, start + 1);
                        break;
                }
                if (inline is ContainerInline child) VisitInlines(child);
            }
        }

        int SkipSpaces(int offset, int end)
        {
            while (offset < end && source[offset] is ' ' or '\t') offset++;
            return offset;
        }
    }
}
