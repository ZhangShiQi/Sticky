using System;
using System.Windows;
using System.Windows.Threading;

namespace Sticky.Capture;

// WPF ShowDialog disables every top-level window on its UI thread. A capture
// module must leave the host's notes usable while scrolling or recording them.
// Keep the existing synchronous session contract, but disable only the explicit
// tool owner; a closed/cancelled window still controls completion of this frame.
internal static class CaptureDialogHost
{
    internal static void Show(Window window)
    {
        window.Dispatcher.VerifyAccess();
        var owner = window.Owner;
        var ownerEnabled = owner?.IsEnabled ?? false;
        var frame = new DispatcherFrame();
        void Closed(object? sender, EventArgs args) => frame.Continue = false;
        window.Closed += Closed;
        try
        {
            if (owner is not null) owner.IsEnabled = false;
            window.Show();
            if (frame.Continue) Dispatcher.PushFrame(frame);
        }
        finally
        {
            window.Closed -= Closed;
            if (owner is not null) owner.IsEnabled = ownerEnabled;
        }
    }
}
