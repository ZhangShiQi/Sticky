using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Sticky.App;

public static class NoteWindowPlacement
{
    public static Point Near(Rect source, Size size, Rect workArea, double scale = 1, IEnumerable<Rect>? occupied = null)
    {
        var obstacles = (occupied ?? []).Append(source).Where(r => !r.IsEmpty && r.Width > 0 && r.Height > 0 && r.IntersectsWith(workArea)).Distinct().ToArray();
        // Prefer breathing room, but use tighter non-overlapping slots before allowing any stacking.
        foreach (var gap in new[] { 12 * scale, 0.0 })
            if (FindFree(source, size, workArea, obstacles, gap) is { } free) return free;

        // No room nearby: cascade without repeatedly covering another note at the exact same origin.
        foreach (var (dx, dy) in new[] { (1, 1), (-1, -1), (-1, 1), (1, -1), (1, 0), (-1, 0), (0, 1), (0, -1) })
        for (var step = 1; step <= 12; step++)
        {
            var point = Clamp(new Point(source.Left + dx * step * 32 * scale, source.Top + dy * step * 32 * scale), size, workArea);
            if (!obstacles.Any(r => Math.Abs(r.Left - point.X) < 2 && Math.Abs(r.Top - point.Y) < 2)) return point;
        }
        return Clamp(new Point(source.Left + 32 * scale, source.Top + 32 * scale), size, workArea);
    }

    private static Point? FindFree(Rect source, Size size, Rect workArea, Rect[] obstacles, double gap)
    {
        bool IsFree(Point point)
        {
            var rectangle = new Rect(point, size);
            return workArea.Contains(rectangle) && !obstacles.Any(r => rectangle.Left < r.Right + gap && rectangle.Right > r.Left - gap
                && rectangle.Top < r.Bottom + gap && rectangle.Bottom > r.Top - gap);
        }
        // Keep the familiar adjacent positions first, then look for gaps around surrounding notes.
        Point[] adjacent = [new(source.Right + gap, source.Top), new(source.Left - size.Width - gap, source.Top),
            new(source.Left, source.Bottom + gap), new(source.Left, source.Top - size.Height - gap)];
        foreach (var point in adjacent.Select(p => Clamp(p, size, workArea)).Distinct())
            if (IsFree(point)) return point;

        var xs = new HashSet<double> { source.Left, source.Right - size.Width, workArea.Left, workArea.Right - size.Width };
        var ys = new HashSet<double> { source.Top, source.Bottom - size.Height, workArea.Top, workArea.Bottom - size.Height };
        foreach (var r in obstacles)
        {
            xs.UnionWith([r.Right + gap, r.Left - size.Width - gap, r.Left, r.Right - size.Width]);
            ys.UnionWith([r.Bottom + gap, r.Top - size.Height - gap, r.Top, r.Bottom - size.Height]);
        }
        // Stay within two neighboring rows/columns rather than jumping to a distant free corner.
        var minX = Math.Max(workArea.Left, source.Left - 2 * (size.Width + gap));
        var maxX = Math.Min(workArea.Right - size.Width, source.Right + size.Width + 2 * gap);
        var minY = Math.Max(workArea.Top, source.Top - 2 * (size.Height + gap));
        var maxY = Math.Min(workArea.Bottom - size.Height, source.Bottom + size.Height + 2 * gap);
        Point? best = null;
        var bestDistance = double.PositiveInfinity;
        foreach (var x in xs.Where(x => x >= minX && x <= maxX))
        foreach (var y in ys.Where(y => y >= minY && y <= maxY))
        {
            var point = new Point(x, y);
            var dx = x + size.Width / 2 - (source.Left + source.Width / 2);
            var dy = y + size.Height / 2 - (source.Top + source.Height / 2);
            var distance = dx * dx + dy * dy;
            if (distance < bestDistance && IsFree(point)) { best = point; bestDistance = distance; }
        }
        return best;
    }

    public static Point Clamp(Point position, Size size, Rect workArea) => new(
        Math.Clamp(position.X, workArea.Left, Math.Max(workArea.Left, workArea.Right - size.Width)),
        Math.Clamp(position.Y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - size.Height)));

    public static void Place(Window window, Window? near = null, IEnumerable<Window>? openNotes = null)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var sourceHandle = near is { IsVisible: true } ? new WindowInteropHelper(near).Handle : IntPtr.Zero;
        // Use screen coordinates throughout, without mixing WPF DIPs and monitor pixels.
        // A second pass accounts for a size change when Windows moves the note between DPI scales.
        for (var pass = 0; pass < 2; pass++)
        {
            if (!GetWindowRect(handle, out var bounds)) return;
            var screen = System.Windows.Forms.Screen.FromHandle(sourceHandle != IntPtr.Zero ? sourceHandle : handle);
            var area = screen.WorkingArea;
            var workArea = new Rect(area.Left, area.Top, area.Width, area.Height);
            var size = new Size(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
            var occupied = new List<Rect>();
            if (sourceHandle != IntPtr.Zero && openNotes is not null)
                foreach (var note in openNotes.Where(n => n != window && n.IsVisible && n.WindowState != WindowState.Minimized))
                    if (GetWindowRect(new WindowInteropHelper(note).Handle, out var other))
                        occupied.Add(new Rect(other.Left, other.Top, other.Right - other.Left, other.Bottom - other.Top));
            var position = sourceHandle != IntPtr.Zero && GetWindowRect(sourceHandle, out var source)
                ? Near(new Rect(source.Left, source.Top, source.Right - source.Left, source.Bottom - source.Top), size, workArea, GetDpiForWindow(sourceHandle) / 96.0, occupied)
                : Clamp(new Point(bounds.Left, bounds.Top), size, workArea);
            if (!SetWindowPos(handle, IntPtr.Zero, (int)Math.Round(position.X), (int)Math.Round(position.Y), 0, 0,
                    0x0001 | 0x0004 | 0x0010)) return; // NOSIZE | NOZORDER | NOACTIVATE
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
