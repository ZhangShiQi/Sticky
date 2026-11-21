using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Sticky.Core;

namespace Sticky.App.Editor;

public sealed class MarkdownLiveEditor : TextEditor
{
    private static readonly FontFamily CodeFont = new("Consolas, Microsoft YaHei UI");
    private static readonly Brush CaretBrush = FrozenBrush(236, 236, 236);
    private static readonly Brush SyntaxBrush = FrozenBrush(175, 175, 175);
    private static readonly Brush LinkBrush = FrozenBrush(159, 203, 255);
    private static readonly Brush CodeBackground = FrozenBrush(60, 60, 60);
    private ITextSourceVersion? _visualVersion;
    private ILookup<int, MarkdownStyleRange> _styles = Enumerable.Empty<MarkdownStyleRange>().ToLookup(style => style.Start);
    private ILookup<int, MarkdownReplacement> _replacements = Enumerable.Empty<MarkdownReplacement>().ToLookup(replacement => replacement.Start);
    private int _sourceLine;
    private bool _composing;

    public Func<MarkdownReplacement, Image>? CreateAttachment { get; set; }

    public MarkdownLiveEditor()
    {
        Options.EnableHyperlinks = false;
        Options.EnableEmailHyperlinks = false;
        Options.EnableTextDragDrop = false;
        Options.HideCursorWhileTyping = false;
        TextArea.Caret.CaretBrush = CaretBrush;
        TextArea.TextView.ElementGenerators.Add(new Replacements(this));
        TextArea.TextView.LineTransformers.Add(new Styles(this));
        TextChanged += (_, _) =>
        {
            _sourceLine = SourceLine();
            TextArea.TextView.Redraw(DispatcherPriority.Render);
        };
        TextArea.Caret.PositionChanged += (_, _) => RefreshSourceLine();
        SizeChanged += (_, e) =>
        {
            if (e.WidthChanged && CreateAttachment is not null) TextArea.TextView.Redraw(DispatcherPriority.Render);
        };
        TextCompositionManager.AddPreviewTextInputStartHandler(TextArea, (_, _) => _composing = true);
        TextCompositionManager.AddPreviewTextInputHandler(TextArea, (_, _) => EndComposition());
        TextArea.LostKeyboardFocus += (_, _) => EndComposition();
    }

    public void SetEditable(bool editing)
    {
        IsReadOnly = !editing;
        RefreshSourceLine();
    }

    public void LoadSource(string source)
    {
        if (Text != source) Text = source;
        Document.UndoStack.ClearAll();
    }

    private int SourceLine() => IsReadOnly ? 0 : Document.GetLineByOffset(CaretOffset).LineNumber;

    private void EndComposition()
    {
        if (!_composing) return;
        _composing = false;
        RefreshSourceLine();
    }

    private void RefreshSourceLine()
    {
        if (_composing && !IsReadOnly) return;
        var next = SourceLine();
        var previous = _sourceLine;
        if (next == previous) return;
        _sourceLine = next;
        // A caret move can affect the preview of the two logical lines it crosses.
        // Text changes already invalidate the view, including when Enter splits a line.
        RedrawLine(previous);
        RedrawLine(next);
    }

    private void RedrawLine(int number)
    {
        if (number == 0 || number > Document.LineCount) return;
        var line = Document.GetLineByNumber(number);
        if (line.Length > 0)
            TextArea.TextView.Redraw(line.Offset, line.Length, DispatcherPriority.Render);
    }

    private void EnsureVisuals()
    {
        var version = Document.Version;
        if (ReferenceEquals(version, _visualVersion)) return;
        var visuals = MarkdownBody.GetVisuals(Text);
        _styles = visuals.Styles.SelectMany(style =>
        {
            var first = Document.GetLineByOffset(style.Start).LineNumber;
            var last = Document.GetLineByOffset(style.End - 1).LineNumber;
            return Enumerable.Range(first, last - first + 1).Select(line => (line, style));
        }).ToLookup(item => item.line, item => item.style);
        _replacements = visuals.Replacements.ToLookup(replacement => Document.GetLineByOffset(replacement.Start).LineNumber);
        _visualVersion = version;
    }

    private static Brush FrozenBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private sealed class Replacements(MarkdownLiveEditor editor) : VisualLineElementGenerator
    {
        private MarkdownReplacement[] _line = [];
        private int _index;

        public override void StartGeneration(ITextRunConstructionContext context)
        {
            base.StartGeneration(context);
            _index = 0;
            var number = context.VisualLine.FirstDocumentLine.LineNumber;
            if (number == editor._sourceLine)
            {
                _line = [];
                return;
            }
            editor.EnsureVisuals();
            _line = editor._replacements[number].ToArray();
        }

        public override int GetFirstInterestedOffset(int startOffset)
        {
            while (_index < _line.Length)
            {
                var replacement = _line[_index];
                if (replacement.Start >= startOffset) return replacement.Start;
                _index++;
            }
            return -1;
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            if (_index >= _line.Length || _line[_index].Start != offset) return null;
            var replacement = _line[_index];
            var length = replacement.End - replacement.Start;
            if (replacement.ImageId is not null)
            {
                if (editor.CreateAttachment is not { } create) return null;
                var image = create(replacement);
                var viewportWidth = editor.TextArea.TextView.ActualWidth;
                if (viewportWidth > 0) image.MaxWidth = Math.Clamp(viewportWidth - 12, 1, 800);
                return new InlineObjectElement(length, image);
            }
            return new FormattedTextElement(replacement.Text.Length == 0 ? "\u200B" : replacement.Text, length);
        }
    }

    private sealed class Styles(MarkdownLiveEditor editor) : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(DocumentLine line)
        {
            if (line.LineNumber == editor._sourceLine) return;
            editor.EnsureVisuals();
            foreach (var span in editor._styles[line.LineNumber])
            {
                var start = Math.Max(span.Start, line.Offset);
                var end = Math.Min(span.End, line.EndOffset);
                if (start < end) ChangeLinePart(start, end, element => ApplyStyle(element, span));
            }
        }

        private void ApplyStyle(VisualLineElement element, MarkdownStyleRange span)
        {
            var properties = element.TextRunProperties;
            var typeface = properties.Typeface;
            var style = span.Style;
            var code = style.HasFlag(MarkdownTextStyle.Code);
            var bold = style.HasFlag(MarkdownTextStyle.Bold);
            var italic = style.HasFlag(MarkdownTextStyle.Italic);
            if (code || bold || italic)
                properties.SetTypeface(new Typeface(code ? CodeFont : typeface.FontFamily,
                    italic ? FontStyles.Italic : typeface.Style, bold ? FontWeights.Bold : typeface.Weight, typeface.Stretch));
            if (span.HeadingLevel > 0)
                properties.SetFontRenderingEmSize(editor.FontSize * (1.8 - 0.12 * (span.HeadingLevel - 1)));
            if (code) properties.SetBackgroundBrush(CodeBackground);
            if (style.HasFlag(MarkdownTextStyle.Link))
            {
                properties.SetForegroundBrush(LinkBrush);
                properties.SetTextDecorations(TextDecorations.Underline);
            }
            if (style.HasFlag(MarkdownTextStyle.Strike)) properties.SetTextDecorations(TextDecorations.Strikethrough);
            if (style.HasFlag(MarkdownTextStyle.Syntax)) properties.SetForegroundBrush(SyntaxBrush);
        }
    }
}
