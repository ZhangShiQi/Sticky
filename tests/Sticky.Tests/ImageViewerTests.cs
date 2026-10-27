using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sticky.App;
using Sticky.App.Editor;
using Sticky.Core;

internal static partial class Program
{
    private static void ImageViewerInteraction()
    {
        var workspace = new StickyWorkspace(Path.Combine(_root, "image-viewer"));
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            dc.DrawRectangle(Brushes.WhiteSmoke, null, new Rect(0, 0, 1600, 1000));
            for (var x = 0; x < 1600; x += 100) dc.DrawLine(new Pen(Brushes.LightGray, 2), new Point(x, 0), new Point(x, 1000));
            for (var y = 0; y < 1000; y += 100) dc.DrawLine(new Pen(Brushes.LightGray, 2), new Point(0, y), new Point(1600, y));
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(47, 83, 70)), null, new Rect(180, 180, 1240, 640), 30, 30);
            dc.DrawText(new FormattedText("Sticky 图片预览", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 80, Brushes.White, 1), new Point(320, 340));
            dc.DrawText(new FormattedText("1600 × 1000 · 缩放不改变便签", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 38, Brushes.WhiteSmoke, 1), new Point(325, 480));
        }
        var bitmap = new RenderTargetBitmap(1600, 1000, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing); bitmap.Freeze();
        var asset = DocumentCodec.StoreBitmap(bitmap, workspace.Store);
        var note = new Note { Blocks = [new NoteBlock { Inlines = [new NoteInline { Text = "图片查看测试" }, new NoteInline { Kind = "image", AssetId = asset, Width = 200 }] }] };
        workspace.Store.Save(note); workspace.Notes.Add(note); workspace.OpenNote(note); Pump(30);
        var window = workspace.Windows[note.Id]; var editor = (RichTextBox)window.FindName("Editor");
        var paragraph = (Paragraph)editor.Document.Blocks.FirstBlock;
        var image = (Image)paragraph.Inlines.OfType<InlineUIContainer>().Single().Child;
        var notePath = Path.Combine(workspace.Store.NotesDirectory, note.Id + ".json");
        var before = File.ReadAllBytes(notePath); var historyCount = workspace.Store.History.ReadTimeline().Events.Count(e => e.Target.StartsWith("notes/", StringComparison.Ordinal));
        void ClickImage(int count)
        {
            window.Activate(); Pump(15);
            var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent };
            typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount), BindingFlags.Public | BindingFlags.Instance)!.SetValue(args, count);
            image.RaiseEvent(args); Pump(30);
        }
        void Button(ImageViewerWindow viewer, string name) { ((Button)viewer.FindName(name)).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); viewer.UpdateLayout(); Pump(15); }
        try
        {
            window.FinishEditing(); ClickImage(1);
            Check(!window.IsEditing && !Application.Current.Windows.OfType<ImageViewerWindow>().Any(), "A single image click entered editing or opened a viewer.");
            ClickImage(2);
            var viewer = Application.Current.Windows.OfType<ImageViewerWindow>().Single();
            var viewport = (ScrollViewer)viewer.FindName("Viewport"); var preview = (Image)viewer.FindName("PreviewImage");
            Check(viewer.Owner == window && !viewer.ShowInTaskbar && !window.IsEditing && viewer.Zoom < 1
                && ((BitmapSource)preview.Source).PixelWidth == 1600 && image.Width == 200, "Double-click did not open a non-modal fitted image without editing the note.");
            using (File.Open(workspace.Store.GetAssetPath(asset), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            var fitted = viewer.Zoom; Button(viewer, "ZoomInButton"); Check(viewer.IsVisible && viewer.Zoom > fitted, "Zooming deactivated the viewer or did not enlarge the image.");
            Button(viewer, "ZoomOutButton"); Check(Math.Abs(viewer.Zoom - fitted) < .001, "Zoom out did not reverse the zoom step.");
            Button(viewer, "ActualSizeButton"); Check(viewer.Zoom == 1 && viewport.ScrollableWidth > 0 && viewport.ScrollableHeight > 0, "Native pixel size did not provide scrollable oversized content.");
            viewport.ScrollToHorizontalOffset(180); viewport.ScrollToVerticalOffset(90); Pump(20);
            Check(viewport.HorizontalOffset > 0 && viewport.VerticalOffset > 0, "The enlarged image could not be moved into view.");
            viewport.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent }); Pump(20);
            Check(viewer.Zoom > 1, "The mouse wheel did not zoom.");
            Button(viewer, "FitButton"); viewer.Width = 720; viewer.Height = 520; Pump(30); viewer.UpdateLayout();
            Check(preview.ActualWidth <= viewport.ViewportWidth && preview.ActualHeight <= viewport.ViewportHeight, "Fit mode clipped the image after resizing.");
            var capture = new RenderTargetBitmap((int)viewer.ActualWidth, (int)viewer.ActualHeight, 96, 96, PixelFormats.Pbgra32); capture.Render(viewer);
            using (var output = File.Create(Path.Combine(_root, "image-viewer.png"))) { var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(capture)); png.Save(output); }
            var outside = new Window { Title = "另一窗口", Width = 240, Height = 180 };
            try
            {
                outside.Show(); outside.Activate(); Pump(40);
                Check(!viewer.IsVisible && !Application.Current.Windows.OfType<ImageViewerWindow>().Any() && outside.IsActive,
                    "Switching to another window did not dismiss the viewer or stole focus back to the note.");
            }
            finally { outside.Close(); }
            ClickImage(2); viewer = Application.Current.Windows.OfType<ImageViewerWindow>().Single();
            var source = PresentationSource.FromVisual(viewer)!;
            viewer.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent }); Pump(20);
            Check(!viewer.IsVisible, "Escape did not close the viewer.");
            ClickImage(2); viewer = Application.Current.Windows.OfType<ImageViewerWindow>().Single(); window.Close(); Pump(20);
            Check(!viewer.IsVisible, "Closing the note left an orphaned viewer.");
            Check(File.ReadAllBytes(notePath).AsSpan().SequenceEqual(before) && note.Blocks[0].Inlines[1].Width == 200
                && workspace.Store.History.ReadTimeline().Events.Count(e => e.Target.StartsWith("notes/", StringComparison.Ordinal)) == historyCount, "Viewing or zooming rewrote note content, image size or history.");
            workspace.OpenNote(note); window = workspace.Windows[note.Id]; editor = (RichTextBox)window.FindName("Editor");
            var run = ((Paragraph)editor.Document.Blocks.FirstBlock).Inlines.OfType<Run>().First();
            run.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            Check(window.IsEditing, "Text clicks stopped entering editing after image event handling was added.");
            try { _ = new ImageViewerWindow(workspace.Store, new string('f', 64) + ".png"); throw new InvalidOperationException("A missing image unexpectedly opened."); }
            catch (IOException) { }
        }
        finally { workspace.PrepareExit(); }
    }
}
