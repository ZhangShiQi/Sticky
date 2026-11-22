using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Sticky.App;

public partial class NoteWindow
{
    private readonly DispatcherTimer _scrollBarTimer = new() { Interval = TimeSpan.FromMilliseconds(850) };
    private ScrollViewer? _bodyScrollViewer;

    private void OnBodyScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer { IsVisible: true } viewer) return;
        if (Editor.IsVisible && e.ViewportWidthChange != 0) FitBodyImages();

        _bodyScrollViewer = viewer;
        BodyScrollBar.Maximum = viewer.ScrollableHeight;
        BodyScrollBar.ViewportSize = e.ViewportHeight;
        BodyScrollBar.Value = e.VerticalOffset;
        if (viewer.ScrollableHeight <= 0 || e.ViewportHeight <= 0) HideBodyScrollBar();
        else if (e.VerticalChange != 0) ShowBodyScrollBar();
    }

    private void ShowBodyScrollBar()
    {
        BodyScrollBar.BeginAnimation(OpacityProperty, null);
        BodyScrollBar.Visibility = Visibility.Visible;
        BodyScrollBar.IsHitTestVisible = true;
        _scrollBarTimer.Stop();
        if (!BodyScrollBar.IsMouseCaptureWithin) _scrollBarTimer.Start();
    }

    private void HideBodyScrollBar()
    {
        _scrollBarTimer.Stop();
        BodyScrollBar.IsHitTestVisible = false;
        BodyScrollBar.Visibility = Visibility.Hidden;
        BodyScrollBar.BeginAnimation(OpacityProperty, null);
    }

    private void OnScrollBarIdle(object? sender, EventArgs e)
    {
        _scrollBarTimer.Stop();
        if (BodyScrollBar.IsMouseCaptureWithin) return;
        if (!SystemParameters.ClientAreaAnimation)
        {
            HideBodyScrollBar();
            return;
        }

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
        fade.Completed += (_, _) =>
        {
            if (BodyScrollBar.Opacity == 0) HideBodyScrollBar();
        };
        BodyScrollBar.BeginAnimation(OpacityProperty, fade);
    }

    private void OnOverlayScroll(object sender, ScrollEventArgs e)
    {
        if (_bodyScrollViewer is { IsVisible: true } viewer) viewer.ScrollToVerticalOffset(e.NewValue);
    }

    private void OnOverlayMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_bodyScrollViewer is not { IsVisible: true } viewer) return;
        viewer.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = Mouse.MouseWheelEvent });
        e.Handled = true;
    }

    private void OnScrollThumbDragStarted(object sender, DragStartedEventArgs e) => ShowBodyScrollBar();

    private void OnScrollThumbDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (BodyScrollBar.IsVisible) _scrollBarTimer.Start();
    }
}
