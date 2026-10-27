using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sticky.Core;
using Sticky.Storage;
using WpfList = System.Windows.Documents.List;

namespace Sticky.App.Editor;

public static class DocumentCodec
{
    public static List<NoteBlock> Encode(FlowDocument document) => EncodeBlocks(document.Blocks);

    public static string GetDisplayTitle(FlowDocument document, string title)
    {
        if (!string.IsNullOrWhiteSpace(title)) return title.Trim();
        // Read the first text line without encoding the entire document on each keystroke.
        // Walking text contexts excludes images and generated list numbering.
        var line = new StringBuilder();
        for (var p = document.ContentStart; p is not null && p.CompareTo(document.ContentEnd) < 0;)
        {
            var context = p.GetPointerContext(LogicalDirection.Forward);
            if (context == TextPointerContext.Text)
            {
                var run = p.GetTextInRun(LogicalDirection.Forward);
                foreach (var ch in run)
                {
                    if (ch is '\r' or '\n')
                    {
                        var candidate = line.ToString().Trim();
                        if (candidate.Length > 0) return candidate;
                        line.Clear();
                    }
                    else line.Append(ch);
                }
                p = p.GetPositionAtOffset(run.Length, LogicalDirection.Forward);
            }
            else
            {
                if (context == TextPointerContext.ElementStart && p.GetAdjacentElement(LogicalDirection.Forward) is LineBreak
                    || context == TextPointerContext.ElementEnd && p.GetAdjacentElement(LogicalDirection.Forward) is Paragraph)
                {
                    var candidate = line.ToString().Trim();
                    if (candidate.Length > 0) return candidate;
                    line.Clear();
                }
                p = p.GetNextContextPosition(LogicalDirection.Forward);
            }
        }
        var last = line.ToString().Trim();
        return last.Length > 0 ? last : "未命名便签";
    }

    private static List<NoteBlock> EncodeBlocks(BlockCollection blocks)
    {
        var result = new List<NoteBlock>();
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    var item = new NoteBlock { Alignment = paragraph.TextAlignment.ToString() };
                    EncodeInlines(paragraph.Inlines, item.Inlines, false, false);
                    result.Add(item);
                    break;
                case WpfList list:
                    result.Add(new NoteBlock { Kind = "list", Marker = list.MarkerStyle.ToString(),
                        Children = list.ListItems.Select(li => new NoteBlock { Kind = "listItem", Children = EncodeBlocks(li.Blocks) }).ToList() });
                    break;
                case Section section:
                    result.AddRange(EncodeBlocks(section.Blocks));
                    break;
                default:
                    throw new InvalidDataException("正文包含不支持的元素，请先复制为纯文本。");
            }
        }
        return result;
    }

    private static void EncodeInlines(InlineCollection inlines, List<NoteInline> result, bool underline, bool strike)
    {
        foreach (var inline in inlines)
        {
            var hasUnderline = underline || inline.TextDecorations.Any(d => d.Location == TextDecorationLocation.Underline);
            var hasStrike = strike || inline.TextDecorations.Any(d => d.Location == TextDecorationLocation.Strikethrough);
            switch (inline)
            {
                case Run run:
                    result.Add(new NoteInline { Text = run.Text, Bold = run.FontWeight.ToOpenTypeWeight() >= 600,
                        Italic = run.FontStyle == FontStyles.Italic || run.FontStyle == FontStyles.Oblique,
                        Underline = hasUnderline, Strike = hasStrike,
                        Foreground = EncodeColor(run.Foreground), UsesDefaultFontSize = UsesDefaultSize(run),
                        FontSize = UsesDefaultSize(run) ? 16 : run.FontSize });
                    break;
                case LineBreak lineBreak:
                    result.Add(new NoteInline { Text = "\n", Underline = hasUnderline, Strike = hasStrike,
                        UsesDefaultFontSize = UsesDefaultSize(lineBreak), FontSize = UsesDefaultSize(lineBreak) ? 16 : lineBreak.FontSize });
                    break;
                case Span span:
                    EncodeInlines(span.Inlines, result, hasUnderline, hasStrike);
                    break;
                case InlineUIContainer { Child: Image image } when image.Tag is string id:
                    result.Add(new NoteInline { Kind = "image", AssetId = id, Width = double.IsFinite(image.Width) ? image.Width : 280 });
                    break;
                default:
                    throw new InvalidDataException("正文包含不支持的内嵌元素。");
            }
        }
    }

    private static bool UsesDefaultSize(TextElement element)
    {
        // A document's font size is the device preference; local run/span/paragraph values are explicit formatting.
        for (FrameworkContentElement? node = element; node is not null && node is not FlowDocument; node = node.Parent as FrameworkContentElement)
            if (node.ReadLocalValue(TextElement.FontSizeProperty) != DependencyProperty.UnsetValue) return false;
        return true;
    }

    private static string? EncodeColor(Brush brush)
    {
        var color = (brush as SolidColorBrush)?.Color.ToString();
        return color is "#FF292B32" or "#FF000000" or "#FFECECEC" ? null : color;
    }

    public static FlowDocument Decode(IEnumerable<NoteBlock> blocks, NoteStore store, bool darkTheme = false, EditorPreferences? preferences = null)
    {
        var document = new FlowDocument { FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"),
            PagePadding = new Thickness(0), Foreground = darkTheme ? new SolidColorBrush(Color.FromRgb(236, 236, 236)) : new SolidColorBrush(Color.FromRgb(41, 43, 50)) };
        foreach (var block in blocks) document.Blocks.Add(DecodeBlock(block, store, darkTheme));
        if (document.Blocks.Count == 0) document.Blocks.Add(new Paragraph());
        ApplyDefaults(document, preferences ?? new EditorPreferences());
        return document;
    }

    public static void ApplyDefaults(FlowDocument document, EditorPreferences preferences)
    {
        NoteStore.ValidateEditorPreferences(preferences);
        document.FontSize = preferences.DefaultFontSize;
        // Cover empty/new documents and paragraphs inserted by Enter, not only decoded paragraphs.
        // Keep natural line height so larger fonts and inline images can grow the line normally.
        var paragraphStyle = new Style(typeof(Paragraph));
        paragraphStyle.Setters.Add(new Setter(Block.MarginProperty, new Thickness(0, 0, 0, preferences.ParagraphSpacing)));
        document.Resources[typeof(Paragraph)] = paragraphStyle;
        void ApplyBlocks(BlockCollection blocks)
        {
            foreach (var block in blocks)
            {
                if (block is Paragraph paragraph) paragraph.ClearValue(Block.MarginProperty);
                else if (block is WpfList list)
                {
                    list.Margin = new Thickness(0, 4, 0, preferences.ParagraphSpacing);
                    foreach (var item in list.ListItems) ApplyBlocks(item.Blocks);
                }
                else if (block is Section section) ApplyBlocks(section.Blocks);
            }
        }
        ApplyBlocks(document.Blocks);
    }

    private static Block DecodeBlock(NoteBlock block, NoteStore store, bool darkTheme)
    {
        if (block.Kind == "list")
        {
            var list = new WpfList { Padding = new Thickness(24, 0, 0, 0), Margin = new Thickness(0, 4, 0, 8),
                MarkerStyle = block.Marker is "Decimal" ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc };
            foreach (var item in block.Children)
            {
                var li = new ListItem();
                foreach (var child in item.Children) li.Blocks.Add(DecodeBlock(child, store, darkTheme));
                if (li.Blocks.Count == 0) li.Blocks.Add(new Paragraph());
                list.ListItems.Add(li);
            }
            return list;
        }
        var paragraph = new Paragraph { TextAlignment = block.Alignment switch { "Center" => TextAlignment.Center, "Right" => TextAlignment.Right, _ => TextAlignment.Left } };
        foreach (var inline in block.Inlines)
        {
            if (inline.Kind == "image" && inline.AssetId is { } id)
            {
                var image = CreateImage(id, inline.Width, store);
                paragraph.Inlines.Add(new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Center });
            }
            else
            {
                var run = new Run(inline.Text) { FontWeight = inline.Bold ? FontWeights.Bold : FontWeights.Normal,
                    FontStyle = inline.Italic ? FontStyles.Italic : FontStyles.Normal };
                if (!(inline.UsesDefaultFontSize ?? inline.FontSize == 16))
                    run.FontSize = double.IsFinite(inline.FontSize) ? Math.Clamp(inline.FontSize, 10, 48) : 16;
                if (!(darkTheme && inline.Foreground is "#FF292B32" or "#FF000000") && inline.Foreground is { Length: 9 } color && color[0] == '#' && uint.TryParse(color.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out var rgba))
                    run.Foreground = new SolidColorBrush(Color.FromArgb((byte)(rgba >> 24), (byte)(rgba >> 16), (byte)(rgba >> 8), (byte)rgba));
                var decorations = new TextDecorationCollection();
                if (inline.Underline) decorations.Add(TextDecorations.Underline[0]);
                if (inline.Strike) decorations.Add(TextDecorations.Strikethrough[0]);
                run.TextDecorations = decorations;
                paragraph.Inlines.Add(run);
            }
        }
        return paragraph;
    }

    public static Image CreateImage(string id, double width, NoteStore store)
    {
        var image = new Image { Tag = id, Width = double.IsFinite(width) ? Math.Clamp(width, 60, 800) : 280,
            MaxWidth = 800, Stretch = Stretch.Uniform, Margin = new Thickness(0, 8, 0, 8), ToolTip = "双击放大查看；右键调整图片大小或移除图片" };
        try
        {
            using var stream = File.OpenRead(store.GetAssetPath(id));
            image.Source = LoadBitmap(stream);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            image.Height = 52;
            image.ToolTip = "图片缺失或无法读取，引用已保留：" + id;
        }
        return image;
    }

    public static BitmapSource LoadBitmap(Stream stream)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = 1600;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    public static string StoreBitmap(BitmapSource source, NoteStore store)
    {
        if (source.PixelWidth < 1 || source.PixelHeight < 1 || (long)source.PixelWidth * source.PixelHeight > 100_000_000)
            throw new InvalidDataException("图片尺寸过大，请选择较小的图片。");
        if (Math.Max(source.PixelWidth, source.PixelHeight) > 1600)
        {
            var scale = 1600.0 / Math.Max(source.PixelWidth, source.PixelHeight);
            source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return store.AddImage(stream.ToArray());
    }
}
