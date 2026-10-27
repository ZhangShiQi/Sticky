using System.Windows;

namespace Sticky.App;

public sealed class StartupPresentation(MainWindow window, bool background)
{
    public async Task RenderAsync(CancellationToken token)
    {
        window.IsEnabled = false;
        window.ShowActivated = false;
        // Opacity=0 on an ordinary HWND renders a black client surface, not an invisible window.
        window.Opacity = 1;
        ((FrameworkElement)window.FindName("StartupOverlay")).Visibility = Visibility.Visible;
        if (background)
        {
            // Exercise real WPF rendering without flashing the manager during logon startup.
            window.ShowInTaskbar = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = SystemParameters.VirtualScreenLeft - window.Width - 4096;
            window.Top = SystemParameters.VirtualScreenTop;
        }
        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnRendered(object? sender, EventArgs e) => rendered.TrySetResult();
        window.ContentRendered += OnRendered;
        try { window.Show(); await rendered.Task.WaitAsync(token); }
        finally { window.ContentRendered -= OnRendered; }
    }

    public void Commit()
    {
        if (background)
        {
            window.Hide();
            var area = SystemParameters.WorkArea;
            window.Left = area.Left + Math.Max(0, (area.Width - window.Width) / 2);
            window.Top = area.Top + Math.Max(0, (area.Height - window.Height) / 2);
            window.ShowInTaskbar = true;
        }
        ((FrameworkElement)window.FindName("StartupOverlay")).Visibility = Visibility.Collapsed;
        window.IsEnabled = true;
        window.ShowActivated = true;
        if (!background) window.Activate();
    }
}
