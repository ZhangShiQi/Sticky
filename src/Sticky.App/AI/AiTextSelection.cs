using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Text;

namespace Sticky.App.AI;

/// <summary>A request is bound to one unchanged document and its original selection, never the later caret.</summary>
public sealed class AiTextSelection : IDisposable
{
    private readonly RichTextBox _editor;
    private readonly FlowDocument _document;
    private readonly TextPointer _start, _end;
    private readonly string _originalRangeText;
    private readonly Dictionary<DependencyProperty, object> _format = [];
    private readonly bool _defaultFontSize;
    private bool _changed, _applied, _disposed;
    public string OriginalText { get; }
    private AiTextSelection(RichTextBox editor)
    {
        _editor = editor; _document = editor.Document; _start = editor.Selection.Start; _end = editor.Selection.End;
        _originalRangeText = new TextRange(_start, _end).Text;
        OriginalText = PlainText(_start, _end);
        if (string.IsNullOrWhiteSpace(OriginalText) || OriginalText.Length > AiClient.MaxInputCharacters) throw new AiException("请选中 1–20,000 字符的文字。");
        for (var p = _start; p is not null && p.CompareTo(_end) < 0; p = p.GetNextContextPosition(LogicalDirection.Forward))
            if (p.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.EmbeddedElement) throw new AiException("当前选区包含图片，请仅选择需要润色的文字。");
        var sample = _start;
        while (sample is not null && sample.CompareTo(_end) < 0 && sample.GetPointerContext(LogicalDirection.Forward) != TextPointerContext.Text)
            sample = sample.GetNextContextPosition(LogicalDirection.Forward);
        sample = sample?.GetPositionAtOffset(1, LogicalDirection.Forward) ?? _start;
        foreach (var property in new[] { TextElement.FontWeightProperty, TextElement.FontStyleProperty, TextElement.FontSizeProperty, TextElement.ForegroundProperty })
            _format[property] = ((DependencyObject)sample.Parent).GetValue(property);
        var decorations = new TextDecorationCollection();
        _defaultFontSize = true;
        for (FrameworkContentElement? element = sample.Parent as FrameworkContentElement; element is not null && element is not FlowDocument; element = element.Parent as FrameworkContentElement)
        {
            if (element.ReadLocalValue(TextElement.FontSizeProperty) != DependencyProperty.UnsetValue) _defaultFontSize = false;
            if (element is Inline inline) foreach (var decoration in inline.TextDecorations)
                if (!decorations.Any(d => d.Location == decoration.Location)) decorations.Add(decoration.Clone());
        }
        _format[Inline.TextDecorationsProperty] = decorations;
        editor.TextChanged += Changed;
    }
    public static AiTextSelection Capture(RichTextBox editor) => new(editor);
    private static string PlainText(TextPointer start, TextPointer end)
    {
        // TextRange.Text may synthesize list markers for clipboard export. Those markers already
        // belong to the document's list structure and must not be sent back as literal list text.
        var text = new StringBuilder();
        for (var p = start; p is not null && p.CompareTo(end) < 0;)
        {
            var context = p.GetPointerContext(LogicalDirection.Forward);
            if (context == TextPointerContext.Text)
            {
                var run = p.GetTextInRun(LogicalDirection.Forward);
                var count = Math.Min(run.Length, p.GetOffsetToPosition(end));
                text.Append(run.AsSpan(0, count)); p = p.GetPositionAtOffset(count, LogicalDirection.Forward);
            }
            else
            {
                if (context == TextPointerContext.ElementStart && p.GetAdjacentElement(LogicalDirection.Forward) is LineBreak
                    || context == TextPointerContext.ElementEnd && p.GetAdjacentElement(LogicalDirection.Forward) is Paragraph) text.Append("\r\n");
                p = p.GetNextContextPosition(LogicalDirection.Forward);
            }
        }
        return text.ToString();
    }
    private void Changed(object sender, TextChangedEventArgs e) => _changed = true;
    public bool CanApply => !_disposed && !_changed && !_applied && ReferenceEquals(_editor.Document, _document) && new TextRange(_start, _end).Text == _originalRangeText;
    public bool Apply(string result)
    {
        if (!CanApply) return false;
        if (string.IsNullOrWhiteSpace(result) || result.Length > AiClient.MaxOutputCharacters) throw new AiException("润色结果不能为空或超过 60,000 字符。");
        // Keep selection-edge whitespace (including paragraph boundaries) owned by the original document.
        var leading = 0; var trailing = OriginalText.Length;
        while (leading < trailing && char.IsWhiteSpace(OriginalText[leading])) leading++;
        while (trailing > leading && char.IsWhiteSpace(OriginalText[trailing - 1])) trailing--;
        var replacement = OriginalText[..leading] + result.Trim() + OriginalText[trailing..];
        _editor.BeginChange();
        try
        {
            // A standalone range tracks WPF's normalized insertion boundaries. Old pointers and
            // TextSelection can collapse when insertion merges with the neighboring Run.
            var inserted = new TextRange(_start, _end);
            inserted.Text = replacement;
            foreach (var (property, value) in _format) inserted.ApplyPropertyValue(property, value);
            if (_defaultFontSize)
            {
                // Applying a uniform size splits the inserted runs from their neighbors. Keep ordinary
                // default-sized text tied to document preferences instead of turning it into a fixed size.
                var runs = new HashSet<Run>();
                for (var p = inserted.Start; p is not null && p.CompareTo(inserted.End) < 0; p = p.GetNextContextPosition(LogicalDirection.Forward))
                    if (p.Parent is Run run && run.ContentStart.CompareTo(inserted.Start) >= 0 && run.ContentEnd.CompareTo(inserted.End) <= 0) runs.Add(run);
                foreach (var run in runs)
                {
                    var overridden = false;
                    for (FrameworkContentElement? parent = run.Parent as FrameworkContentElement; parent is not null && parent is not FlowDocument; parent = parent.Parent as FrameworkContentElement)
                        if (parent.ReadLocalValue(TextElement.FontSizeProperty) != DependencyProperty.UnsetValue) overridden = true;
                    if (!overridden) run.ClearValue(TextElement.FontSizeProperty);
                }
            }
            _editor.Selection.Select(inserted.Start, inserted.End); _applied = true;
        }
        finally { _editor.EndChange(); }
        return true;
    }
    public void Dispose() { _disposed = true; _editor.TextChanged -= Changed; }
}
