using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sticky.Storage;

namespace Sticky.App;

public partial class ImageViewerWindow : Window
{
    private readonly BitmapSource _bitmap;
    private double _zoom = 1;
    private bool _fit = true, _closed, _closing, _settingZoom;
    private Point? _panStart;
    private Point _panOffsets;
    public double Zoom => _zoom;

    public ImageViewerWindow(NoteStore store, string assetId)
    {
        var path = store.GetAssetPath(assetId);
        if (new FileInfo(path).Length > 20 * 1024 * 1024) throw new InvalidDataException("图片超过 20 MB，无法预览。");
        using (var stream = File.OpenRead(path))
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
            if ((long)decoder.Frames[0].PixelWidth * decoder.Frames[0].PixelHeight > 100_000_000) throw new InvalidDataException("图片尺寸过大，无法预览。");
            stream.Position = 0;
            _bitmap = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            _bitmap.Freeze();
        }
        InitializeComponent(); PreviewImage.Source = _bitmap;
        ImageInfo.Text = $"图片预览 · {_bitmap.PixelWidth} × {_bitmap.PixelHeight}";
        Loaded += (_, _) => { PlaceWithinMonitor(); FitImage(); };
        Closing += (_, _) => _closing = true;
        Closed += (_, _) => { _closed = true; StopPan(); PreviewImage.Source = null; };
    }

    private void PlaceWithinMonitor()
    {
        var screen = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle).WorkingArea;
        var scale = VisualTreeHelper.GetDpi(this);
        var area = new Rect(screen.Left / scale.DpiScaleX, screen.Top / scale.DpiScaleY, screen.Width / scale.DpiScaleX, screen.Height / scale.DpiScaleY);
        MinWidth = Math.Min(MinWidth, area.Width); MinHeight = Math.Min(MinHeight, area.Height);
        Width = Math.Min(Width, area.Width); Height = Math.Min(Height, area.Height);
        Left = Math.Clamp(Left, area.Left, area.Right - Width); Top = Math.Clamp(Top, area.Top, area.Bottom - Height);
    }
    private double FitScale => Math.Min(1, Math.Max(.001, Math.Min(Math.Max(1, Viewport.ActualWidth - 36) / _bitmap.PixelWidth,
        Math.Max(1, Viewport.ActualHeight - 36) / _bitmap.PixelHeight)));
    private void FitImage()
    {
        _fit = true; SetZoom(FitScale, null);
        Viewport.ScrollToHorizontalOffset(0); Viewport.ScrollToVerticalOffset(0);
    }
    private void SetZoom(double scale, Point? anchor)
    {
        if (_closed || _settingZoom) return;
        _settingZoom = true;
        try
        {
            var point = anchor ?? new Point(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2);
            var before = PreviewImage.TranslatePoint(new Point(), Viewport);
            var imagePoint = new Point((point.X - before.X) / _zoom, (point.Y - before.Y) / _zoom);
            _zoom = Math.Clamp(scale, Math.Min(.05, FitScale), 16);
            PreviewImage.Width = _bitmap.PixelWidth * _zoom; PreviewImage.Height = _bitmap.PixelHeight * _zoom;
            ZoomLabel.Text = _zoom < .1 ? $"{_zoom:P1}" : $"{_zoom:P0}";
            Viewport.UpdateLayout();
            if (!_fit)
            {
                var after = PreviewImage.TranslatePoint(new Point(), Viewport);
                Viewport.ScrollToHorizontalOffset(Viewport.HorizontalOffset + after.X + imagePoint.X * _zoom - point.X);
                Viewport.ScrollToVerticalOffset(Viewport.VerticalOffset + after.Y + imagePoint.Y * _zoom - point.Y);
            }
            Viewport.Cursor = Viewport.ScrollableWidth > 0 || Viewport.ScrollableHeight > 0 ? Cursors.Hand : Cursors.Arrow;
        }
        finally { _settingZoom = false; }
    }
    private void ZoomBy(double factor, Point? anchor = null) { _fit = false; SetZoom(_zoom * factor, anchor); }
    private void ZoomWheel(object sender, MouseWheelEventArgs e)
    { ZoomBy(Math.Pow(1.2, Math.Clamp(e.Delta / 120.0, -8, 8)), e.GetPosition(Viewport)); e.Handled = true; }
    private void ZoomInClicked(object sender, RoutedEventArgs e) => ZoomBy(1.25);
    private void ZoomOutClicked(object sender, RoutedEventArgs e) => ZoomBy(1 / 1.25);
    private void ActualSizeClicked(object sender, RoutedEventArgs e) { _fit = false; SetZoom(1, null); }
    private void FitClicked(object sender, RoutedEventArgs e) => FitImage();
    private void ViewportSizeChanged(object sender, SizeChangedEventArgs e) { if (IsLoaded && _fit && !_closed && !_settingZoom) FitImage(); }
    private void CloseViewer() { if (!_closed && !_closing) { _closing = true; Close(); } }
    private void ViewerDeactivated(object? sender, EventArgs e) => CloseViewer();
    private void CloseClicked(object sender, RoutedEventArgs e) => CloseViewer();
    private void ViewerKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CloseViewer(); e.Handled = true; }
        else if (e.Key is Key.Add or Key.OemPlus) { ZoomBy(1.25); e.Handled = true; }
        else if (e.Key is Key.Subtract or Key.OemMinus) { ZoomBy(1 / 1.25); e.Handled = true; }
    }
    private void BeginPan(object sender, MouseButtonEventArgs e)
    {
        if (!PreviewImage.IsMouseOver || Viewport.ScrollableWidth <= 0 && Viewport.ScrollableHeight <= 0) return;
        if (!Viewport.CaptureMouse()) return;
        _panStart = e.GetPosition(Viewport); _panOffsets = new Point(Viewport.HorizontalOffset, Viewport.VerticalOffset); e.Handled = true;
    }
    private void Pan(object sender, MouseEventArgs e)
    {
        if (_panStart is not Point start) return;
        if (e.LeftButton != MouseButtonState.Pressed) { StopPan(); return; }
        var delta = e.GetPosition(Viewport) - start;
        Viewport.ScrollToHorizontalOffset(_panOffsets.X - delta.X); Viewport.ScrollToVerticalOffset(_panOffsets.Y - delta.Y); e.Handled = true;
    }
    private void EndPan(object sender, MouseButtonEventArgs e) { if (_panStart is not null) { StopPan(); e.Handled = true; } }
    private void PanCaptureLost(object sender, MouseEventArgs e) => _panStart = null;
    private void StopPan() { _panStart = null; if (Viewport.IsMouseCaptured) Viewport.ReleaseMouseCapture(); }
    private void DragViewer(object sender, MouseButtonEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is ButtonBase) return;
        if (e.LeftButton == MouseButtonState.Pressed) { DragMove(); e.Handled = true; }
    }
}
