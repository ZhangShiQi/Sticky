using System.Globalization;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Sticky.Core;

public sealed record MarkdownProjection(List<NoteBlock> Blocks, IReadOnlyList<string> ConversionNotes);

public static partial class MarkdownBody
{
    private const string AssetPrefix = "sticky-asset:";
    private static readonly MarkdownPipeline Pipeline = CreatePipeline().Build();
    private static MarkdownPipelineBuilder CreatePipeline() => new MarkdownPipelineBuilder()
        .DisableHtml().UseEmphasisExtras(EmphasisExtraOptions.Strikethrough);

    public static string ImageSyntax(string id, double width)
        => FormattableString.Invariant($"![图片]({AssetPrefix}{id} \"width={Math.Clamp(double.IsFinite(width) ? width : 280, 60, 800):R}\")");

    public static MarkdownProjection Project(string source)
    {
        var notes = new List<string>();
        var blocks = ReadBlocks(Markdown.Parse(source, Pipeline), notes, depth: 0);
        return new MarkdownProjection(blocks, notes);
    }

    private static List<NoteBlock> ReadBlocks(ContainerBlock container, List<string> notes, int depth)
    {
        var result = new List<NoteBlock>();
        foreach (var block in container)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    var headingParagraph = new NoteBlock();
                    ReadInlines(heading.Inline, headingParagraph.Inlines, notes, bold: true);
                    foreach (var inline in headingParagraph.Inlines.Where(i => i.Kind == "text"))
                    {
                        inline.FontSize = 32 - 2 * heading.Level;
                        inline.UsesDefaultFontSize = false;
                    }
                    result.Add(headingParagraph);
                    NoteChange(notes, "标题转换为大号粗体。");
                    break;
                case ParagraphBlock paragraph:
                    var text = new NoteBlock();
                    ReadInlines(paragraph.Inline, text.Inlines, notes);
                    result.Add(text);
                    break;
                case CodeBlock code:
                    var codeParagraph = TextBlock(code.Lines.ToString());
                    codeParagraph.Inlines[0].MarkdownCode = true;
                    result.Add(codeParagraph);
                    NoteChange(notes, "代码保留为文字，代码块语言和等宽样式会移除。");
                    break;
                case ListBlock list:
                    var items = list.OfType<ListItemBlock>().Select(item => new NoteBlock
                    {
                        Kind = "listItem",
                        Children = ReadBlocks(item, notes, depth + 1)
                    }).ToList();
                    if (depth < 7)
                    {
                        result.Add(new NoteBlock { Kind = "list", Marker = list.IsOrdered ? "Decimal" : "Disc", Children = items });
                        if (list.IsOrdered && list.OrderedStart is not (null or "1"))
                            NoteChange(notes, "编号列表从 1 开始。");
                    }
                    else
                    {
                        result.AddRange(items.SelectMany(item => item.Children));
                        NoteChange(notes, "超过七层的嵌套列表会展开。");
                    }
                    break;
                case QuoteBlock quote:
                    var quoted = ReadBlocks(quote, notes, depth);
                    if (quoted.FirstOrDefault()?.Kind == "paragraph")
                        quoted[0].Inlines.Insert(0, new NoteInline { Text = "> " });
                    else quoted.Insert(0, TextBlock(">"));
                    result.AddRange(quoted);
                    NoteChange(notes, "引用用 > 标记保留。");
                    break;
                case ThematicBreakBlock:
                    result.Add(TextBlock("────────"));
                    NoteChange(notes, "分隔线转换为文字线条。");
                    break;
            }
        }
        return result;
    }

    private static void ReadInlines(ContainerInline? container, List<NoteInline> output, List<string> notes,
        bool bold = false, bool italic = false, bool strike = false)
    {
        if (container is null) return;
        void AddText(string text, bool code = false) => output.Add(new NoteInline
            { Text = text, Bold = bold, Italic = italic, Strike = strike, MarkdownCode = code });

        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    AddText(literal.Content.ToString());
                    break;
                case HtmlEntityInline entity:
                    AddText(entity.Transcoded.ToString());
                    break;
                case LineBreakInline:
                    AddText("\n");
                    break;
                case CodeInline code:
                    AddText(code.Content, code: true);
                    NoteChange(notes, "行内代码保留为文字，等宽样式会移除。");
                    break;
                case EmphasisInline emphasis:
                    ReadInlines(emphasis, output, notes,
                        bold || (emphasis.DelimiterChar is '*' or '_' && emphasis.DelimiterCount >= 2),
                        italic || (emphasis.DelimiterChar is '*' or '_' && emphasis.DelimiterCount % 2 == 1),
                        strike || emphasis.DelimiterChar == '~');
                    break;
                case LinkInline link:
                    var url = link.GetDynamicUrl?.Invoke() ?? link.Url ?? "";
                    if (LocalImage(link, url) is { } assetId)
                    {
                        output.Add(new NoteInline { Kind = "image", AssetId = assetId, Width = ImageWidth(link.Title) });
                    }
                    else
                    {
                        var start = output.Count;
                        ReadInlines(link, output, notes, bold, italic, strike);
                        var hasLabel = output.Count > start;
                        if (url.Length > 0 && (!hasLabel || string.Concat(output.Skip(start).Select(i => i.Text)) != url))
                            AddText(hasLabel ? $" ({url})" : url);
                        NoteChange(notes, link.IsImage ? "外部图片保留描述和地址。" : "链接保留文字和地址。");
                    }
                    break;
                case AutolinkInline autolink:
                    AddText(autolink.Url);
                    NoteChange(notes, "链接保留文字和地址。");
                    break;
                case ContainerInline nested:
                    ReadInlines(nested, output, notes, bold, italic, strike);
                    break;
            }
        }
    }

    private static NoteBlock TextBlock(string text) => new() { Inlines = [new NoteInline { Text = text }] };

    private static string? LocalImage(LinkInline link, string url)
    {
        if (!link.IsImage || !url.StartsWith(AssetPrefix, StringComparison.Ordinal)) return null;
        var id = url[AssetPrefix.Length..];
        return AssetName().IsMatch(id) ? id : null;
    }

    private static double ImageWidth(string? title)
        => title is not null && title.StartsWith("width=", StringComparison.Ordinal)
            && double.TryParse(title.AsSpan(6), NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
            && double.IsFinite(width) ? Math.Clamp(width, 60, 800) : 280;

    private static void NoteChange(List<string> notes, string note)
    {
        if (!notes.Contains(note)) notes.Add(note);
    }

    [GeneratedRegex(@"\A[0-9a-f]{64}\.png\z", RegexOptions.CultureInvariant)]
    private static partial Regex AssetName();
}
