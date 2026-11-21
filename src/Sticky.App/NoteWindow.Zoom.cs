using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Rendering;

namespace Sticky.App;

public partial class NoteWindow
{
    private const int DefaultZoom = 100;
    private const int MinimumZoom = 50;
    private const int MaximumZoom = 400;
    private const int ZoomStep = 10;

    private readonly DispatcherTimer _zoomBadgeTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private int _zoomPercent = DefaultZoom;
    private int _zoomWheelDelta;
    private bool _zoomNeedsSave;

    private void InitializeContentZoom()
    {
        var stored = _state.ContentZoomPercent;
        if (stored is null && !_workspace.IsVaultNote(_note.Id))
            stored = _workspace.Store.LoadNoteZoom(_note.Id);
        _zoomPercent = Math.Clamp(stored ?? DefaultZoom, MinimumZoom, MaximumZoom);
        ApplyContentScale();
        _zoomBadgeTimer.Tick += (_, _) =>
        {
            _zoomBadgeTimer.Stop();
            ZoomBadge.Visibility = Visibility.Collapsed;
            SaveContentZoom();
        };
    }

    private bool HandleZoomKey(KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if ((modifiers & ~ModifierKeys.Shift) != ModifierKeys.Control) return false;

        // The IME owns keys WPF reports as Key.ImeProcessed.
        int? percent = e.Key switch
        {
            Key.OemPlus or Key.Add => _zoomPercent + ZoomStep,
            Key.OemMinus or Key.Subtract when modifiers == ModifierKeys.Control => _zoomPercent - ZoomStep,
            Key.D0 or Key.NumPad0 when modifiers == ModifierKeys.Control => DefaultZoom,
            _ => null
        };
        if (percent is null) return false;
        ChangeContentZoom(percent.Value);
        return true;
    }

    private void OnWindowMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control)
        {
            _zoomWheelDelta = 0;
            return;
        }

        e.Handled = true;
        _zoomWheelDelta += e.Delta;
        var steps = _zoomWheelDelta / Mouse.MouseWheelDeltaForOneLine;
        _zoomWheelDelta %= Mouse.MouseWheelDeltaForOneLine;
        if (steps != 0) ChangeContentZoom(_zoomPercent + steps * ZoomStep);
    }

    private MenuItem BuildZoomMenu()
    {
        var menu = new MenuItem { Header = $"内容缩放 · {_zoomPercent}%" };
        Add("放大", "Ctrl+=", _zoomPercent + ZoomStep, _zoomPercent < MaximumZoom);
        Add("缩小", "Ctrl+-", _zoomPercent - ZoomStep, _zoomPercent > MinimumZoom);
        Add("恢复 100%", "Ctrl+0", DefaultZoom, _zoomPercent != DefaultZoom);
        return menu;

        void Add(string label, string shortcut, int percent, bool enabled)
        {
            var item = new MenuItem { Header = label, InputGestureText = shortcut, IsEnabled = enabled };
            item.Click += (_, _) => ChangeContentZoom(percent);
            menu.Items.Add(item);
        }
    }

    private void ChangeContentZoom(int requestedPercent)
    {
        var isPrivate = _workspace.IsVaultNote(_note.Id);
        if (isPrivate) _workspace.TouchVault();
        var percent = Math.Clamp(requestedPercent, MinimumZoom, MaximumZoom);
        if (percent == _zoomPercent) return;

        _zoomPercent = percent;
        _state.ContentZoomPercent = percent;
        _zoomNeedsSave = !isPrivate;
        ZoomBodyView();

        ZoomBadgeText.Text = $"{percent}%";
        ZoomBadge.Visibility = Visibility.Visible;
        _zoomBadgeTimer.Stop();
        _zoomBadgeTimer.Start();
    }

    private void ApplyContentScale()
    {
        var scale = _zoomPercent / 100.0;
        ContentScale.ScaleX = scale;
        ContentScale.ScaleY = scale;
    }

    private void ZoomBodyView()
    {
        var markdown = MarkdownEditor.IsVisible;
        Func<Rect>? anchor;
        if (markdown)
        {
            if (IsEditing)
                anchor = () => RelativeToMarkdownViewport(MarkdownEditor.TextArea.Caret.CalculateCaretRectangle());
            else if (MarkdownEditor.VerticalOffset > 0 && MarkdownEditor.GetPositionFromPoint(new Point(6, 1)) is { } firstVisible)
                anchor = () => RelativeToMarkdownViewport(new Rect(
                    MarkdownEditor.TextArea.TextView.GetVisualPosition(firstVisible, VisualYPosition.LineTop), new Size()));
            else anchor = null;
        }
        else
        {
            var position = IsEditing ? Editor.CaretPosition
                : Editor.VerticalOffset > 0 ? Editor.GetPositionFromPoint(new Point(1, 1), snapToText: true) : null;
            anchor = position is null ? null : () => position.GetCharacterRect(position.LogicalDirection);
        }

        var before = anchor?.Invoke() ?? Rect.Empty;
        ApplyContentScale();
        if (anchor is null || before.IsEmpty) return;

        // Resolve the anchor again after the document has wrapped to the new viewport width.
        UpdateLayout();
        var after = anchor();
        if (after.IsEmpty) return;
        var height = markdown ? MarkdownEditor.ViewportHeight : Editor.ViewportHeight;
        var targetTop = IsEditing ? Math.Clamp(before.Top, 0, Math.Max(0, height - after.Height)) : before.Top;
        if (markdown) MarkdownEditor.ScrollToVerticalOffset(Math.Max(0, MarkdownEditor.VerticalOffset + after.Top - targetTop));
        else Editor.ScrollToVerticalOffset(Math.Max(0, Editor.VerticalOffset + after.Top - targetTop));

        Rect RelativeToMarkdownViewport(Rect bounds)
        {
            if (!bounds.IsEmpty) bounds.Offset(0, -MarkdownEditor.VerticalOffset);
            return bounds;
        }
    }

    private void SaveContentZoom()
    {
        if (!_zoomNeedsSave) return;
        try
        {
            _workspace.Store.SaveNoteZoom(_note.Id, _zoomPercent);
            _zoomNeedsSave = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("无法保存便签的缩放比例：{0}", ex.Message);
        }
    }
}
