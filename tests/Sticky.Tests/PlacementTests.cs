using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Sticky.App;

internal static partial class Program
{
    private static void NearbyPlacementEdges()
    {
        var size = new Size(390, 320); var area = new Rect(0, 0, 1920, 1040);
        var right = NoteWindowPlacement.Near(new Rect(700, 200, 390, 320), size, area);
        Check(right == new Point(1102, 200), "Open space to the right was ignored.");
        var left = NoteWindowPlacement.Near(new Rect(1500, 700, 390, 320), size, area);
        Check(left == new Point(1098, 700), "Right-edge notes did not use adjacent space to the left.");
        var narrow = new Rect(0, 0, 640, 480);
        var overlap = NoteWindowPlacement.Near(new Rect(180, 100, 390, 320), size, narrow);
        Check(overlap == new Point(212, 132), "A narrow monitor did not cascade close to the source.");
        var bottom = NoteWindowPlacement.Near(new Rect(200, 850, 390, 180), size, area);
        Check(bottom.Y == 720 && area.Contains(new Rect(bottom, size)), "New note extended below the work area.");
        var secondary = new Rect(-1920, -200, 1920, 1040);
        var negative = NoteWindowPlacement.Near(new Rect(-1200, 0, 390, 320), size, secondary);
        Check(negative.X < 0 && secondary.Contains(new Rect(negative, size)), "A left-side secondary monitor was clamped to the primary screen.");
        var scaled = NoteWindowPlacement.Near(new Rect(1920, 150, 585, 480), new Size(585, 480), new Rect(1920, 0, 2560, 1400), 1.5);
        Check(scaled == new Point(2523, 150), "The gap did not scale with the source monitor.");
        var oversized = NoteWindowPlacement.Clamp(new Point(-9999, 9999), new Size(1200, 900), narrow);
        Check(oversized == new Point(0, 0), "An oversized window did not keep its header accessible.");
    }

    private static void NearbyNoteWindows()
    {
        var workspace = new StickyWorkspace(Path.Combine(_root, "nearby-windows"));
        try
        {
            var group = workspace.CreateGroup("附近创建")!;
            workspace.CreateNote(group.Id); Pump(40);
            var source = workspace.Windows[workspace.Notes[0].Id];
            var screens = System.Windows.Forms.Screen.AllScreens;
            foreach (var screen in screens)
            {
                var area = screen.WorkingArea;
                var handle = new WindowInteropHelper(source).Handle;
                MoveTestWindow(handle, IntPtr.Zero, area.Left + 40, area.Top + 40, 0, 0, 0x0015);
                Pump(60);
                var bounds = WindowPixels(source);
                // Move again after a possible DPI resize, without waiting for layout persistence.
                MoveTestWindow(handle, IntPtr.Zero, area.Right - (int)bounds.Width - 24, area.Top + 80, 0, 0, 0x0015);
                source.EnterEditMode(); Pump(30);
                var header = (DockPanel)((Border)source.FindName("HeaderBar")).Child;
                header.Children.OfType<Button>().First().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(60);
                var created = workspace.Notes[0]; var child = workspace.Windows[created.Id];
                Check(created.GroupId == group.Id && child.IsEditing, "Creating nearby lost the group or edit mode.");
                AssertNearby(source, child, screen);
                var before = WindowPixels(child); child.Close(); workspace.OpenNote(created); Pump(60);
                var after = WindowPixels(workspace.Windows[created.Id]);
                Check(Math.Abs(before.Left - after.Left) <= 2 && Math.Abs(before.Top - after.Top) <= 2, "Reopening relocated the newly created note.");
                source.Activate(); source.EnterEditMode();
                ApplicationCommands.New.Execute(null, source); Pump(60);
                Check(workspace.Notes[0].GroupId == group.Id, "Ctrl+N lost group membership.");
                AssertNearby(source, workspace.Windows[workspace.Notes[0].Id], screen);
            }
        }
        finally { workspace.PrepareExit(); }
    }

    private static void NearbyAvoidance()
    {
        var area = new Rect(0, 0, 1920, 1080); var source = new Rect(600, 350, 390, 320);
        var occupied = new List<Rect> { source };
        for (var i = 0; i < 8; i++)
        {
            var bounds = new Rect(NoteWindowPlacement.Near(source, source.Size, area, occupied: occupied), source.Size);
            Check(area.Contains(bounds), "Repeated creation left the work area.");
            Check(occupied.All(r => !Overlaps(r, bounds)), "A new note overlapped despite available nearby space.");
            occupied.Add(bounds);
        }
        var unevenSource = new Rect(0, 0, 300, 220);
        var uneven = NoteWindowPlacement.Near(unevenSource, unevenSource.Size, new Rect(0, 0, 1000, 700),
            occupied: [new Rect(312, 0, 400, 220), new Rect(0, 232, 300, 300)]);
        Check(uneven == new Point(312, 232), "The diagonal gap between differently sized notes was not found.");
        var tightSource = new Rect(0, 0, 390, 320);
        var tight = new Rect(NoteWindowPlacement.Near(tightSource, tightSource.Size, new Rect(0, 0, 790, 320)), tightSource.Size);
        Check(!Overlaps(tightSource, tight) && tight.Right <= 790, "A narrow but usable gap caused unnecessary stacking.");
        var leftMonitor = new Rect(-1920, -200, 1920, 1080);
        var negativeSource = new Rect(-1200, 0, 390, 320);
        var negative = NoteWindowPlacement.Near(negativeSource, negativeSource.Size, leftMonitor,
            occupied: [new Rect(-798, 0, 390, 320), new Rect(0, 0, 1920, 1080)]);
        Check(leftMonitor.Contains(new Rect(negative, negativeSource.Size)) && negative.X < negativeSource.Left,
            "Avoidance lost secondary-monitor coordinates or considered a non-overlapping monitor occupied.");
    }

    private static void CrowdedNearbyPlacement()
    {
        var area = new Rect(0, 0, 640, 480); var source = new Rect(180, 100, 390, 320);
        var occupied = new List<Rect> { source };
        for (var i = 0; i < 6; i++)
        {
            var bounds = new Rect(NoteWindowPlacement.Near(source, source.Size, area, occupied: occupied), source.Size);
            Check(area.Contains(bounds), "Cascaded note was off-screen.");
            Check(occupied.All(r => r.TopLeft != bounds.TopLeft), "Crowded creation reused the exact same origin.");
            occupied.Add(bounds);
        }
        var farAway = NoteWindowPlacement.Near(new Rect(0, 0, 390, 320), new Size(390, 320), new Rect(0, 0, 5000, 2000),
            occupied: [new Rect(0, 0, 1700, 1400)]);
        Check(farAway == new Point(32, 32), "A crowded neighborhood caused a jump to a distant free corner.");
    }

    private static void RepeatedNearbyWindows()
    {
        var workspace = new StickyWorkspace(Path.Combine(_root, "repeated-nearby-windows"));
        try
        {
            workspace.CreateNote(); Pump(40);
            var source = workspace.Windows[workspace.Notes[0].Id];
            var sourceHandle = new WindowInteropHelper(source).Handle;
            var area = System.Windows.Forms.Screen.FromHandle(sourceHandle).WorkingArea;
            MoveTestWindow(sourceHandle, IntPtr.Zero, area.Left + 24, area.Top + 24, 0, 0, 0x0015); Pump(40);
            var initial = WindowPixels(source);
            var scale = System.Windows.Media.VisualTreeHelper.GetDpi(source).DpiScaleX;
            var columns = Math.Min(3, (int)((area.Width - 48) / (initial.Width + 12 * scale)));
            var rows = Math.Min(3, (int)((area.Height - 48) / (initial.Height + 12 * scale)));
            var count = Math.Min(6, Math.Max(1, columns * rows - 1));
            NoteWindow? firstChild = null;
            for (var i = 0; i < count; i++)
            {
                var previous = workspace.Windows.Values.ToDictionary(w => w, WindowPixels);
                // Exercise both repeated clicks on the same note and a chain of Ctrl+N from new notes.
                var origin = i % 2 == 0 ? source : workspace.Windows[workspace.Notes[0].Id];
                origin.Activate(); origin.EnterEditMode();
                if (i % 2 == 0)
                    ((DockPanel)((Border)origin.FindName("HeaderBar")).Child).Children.OfType<Button>().First().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                else ApplicationCommands.New.Execute(null, origin);
                Pump(60);
                var child = workspace.Windows[workspace.Notes[0].Id]; firstChild ??= child;
                var bounds = WindowPixels(child);
                if (columns * rows >= 2)
                    Check(previous.Values.All(r => !Overlaps(r, bounds)), "Visible notes overlapped during repeated button/shortcut creation.");
                foreach (var (window, before) in previous)
                    Check(WindowPixels(window) == before, "Avoidance rearranged an already open note.");
            }
            var vacant = WindowPixels(firstChild!);
            firstChild!.WindowState = System.Windows.WindowState.Minimized; Pump(40);
            source.Activate(); ApplicationCommands.New.Execute(null, source); Pump(60);
            var reused = WindowPixels(workspace.Windows[workspace.Notes[0].Id]);
            Check(Math.Abs(reused.Left - vacant.Left) <= 1 && Math.Abs(reused.Top - vacant.Top) <= 1,
                "A minimized note continued to reserve its old screen position.");
        }
        finally { workspace.PrepareExit(); }
    }

    private static bool Overlaps(Rect first, Rect second)
    {
        var intersection = Rect.Intersect(first, second);
        return !intersection.IsEmpty && intersection.Width > 0 && intersection.Height > 0;
    }

    private static void AssertNearby(Window source, Window created, System.Windows.Forms.Screen screen)
    {
        var sourceBounds = WindowPixels(source); var bounds = WindowPixels(created);
        var handle = new WindowInteropHelper(created).Handle;
        Check(System.Windows.Forms.Screen.FromHandle(handle).DeviceName == screen.DeviceName, "New note jumped to a different monitor.");
        var area = screen.WorkingArea;
        Check(bounds.Left >= area.Left && bounds.Top >= area.Top && bounds.Right <= area.Right + 1 && bounds.Bottom <= area.Bottom + 1, "New note was outside the monitor's work area.");
        var horizontalGap = Math.Max(0, Math.Max(bounds.Left - sourceBounds.Right, sourceBounds.Left - bounds.Right));
        var verticalGap = Math.Max(0, Math.Max(bounds.Top - sourceBounds.Bottom, sourceBounds.Top - bounds.Bottom));
        Check(horizontalGap <= bounds.Width + 48 && verticalGap <= bounds.Height + 48, "New note was not near the source's live position.");
        Check(Math.Abs(bounds.Left - sourceBounds.Left) > 1 || Math.Abs(bounds.Top - sourceBounds.Top) > 1, "The new note completely covered the source at the same position.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PlacementBounds { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", EntryPoint = "GetWindowRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadTestWindow(IntPtr window, out PlacementBounds bounds);
    [DllImport("user32.dll", EntryPoint = "SetWindowPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveTestWindow(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    private static Rect WindowPixels(Window window)
    {
        Check(ReadTestWindow(new WindowInteropHelper(window).Handle, out var bounds), "Could not read actual window coordinates.");
        return new Rect(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
    }
}
