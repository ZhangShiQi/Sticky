using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using Sticky.Capture.Services;

namespace Sticky.Capture;

public sealed class CaptureCoordinator : IDisposable
{
    private HotkeyService? hotkey;
    private CaptureSession? session;
    private bool starting, disposed, started;
    private readonly Func<bool> canStart;
    private readonly List<Window> hidden = new();
    public CapturePreferences Preferences { get; private set; } = new();
    public string Status { get; private set; } = "截图尚未启动";
    public bool IsActive => starting || session is not null;
    public event Action? Changed;
    internal Func<DesktopSnapshot> AcquireSnapshot { get; set; } = CaptureService.Capture;
    internal Func<IReadOnlyList<WindowCandidate>> AcquireTargets { get; set; } = () => WindowDetectionService.Capture();
    public CaptureCoordinator(string dataDirectory, Func<bool> canStart)
    {
        CaptureEnvironment.Configure(dataDirectory); this.canStart = canStart;
        try { Preferences = CapturePreferences.Load(); }
        catch (Exception ex) { Status = "无法读取截图设置，原文件已保留：" + ex.Message; }
    }
    public void Start()
    {
        if (started || disposed) return;
        started = true;
        hotkey = new HotkeyService(); hotkey.Pressed += OnHotkey;
        ApplyRegistration();
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
        _ = Task.Run(RecordingCache.CleanupOrphans);
    }
    private async void OnHotkey() => await StartCaptureAsync();
    private void ApplyRegistration()
    {
        if (hotkey is null) return;
        if (!Preferences.Enabled) { hotkey.Unregister(); Status = "截图快捷键已关闭，仍可点击开始截图。"; }
        else Status = hotkey.TryRegister(Preferences.Modifiers, Preferences.Key) ? "截图快捷键：" + Preferences
            : "快捷键被占用或无法注册（" + hotkey.LastError + "），请修改快捷键或点击开始截图。";
        Changed?.Invoke();
    }
    public void SavePreferences(CapturePreferences requested)
    {
        requested.Validate(); var previous = Preferences;
        if (hotkey is not null && requested.Enabled && !hotkey.TryRegister(requested.Modifiers, requested.Key))
            throw new InvalidOperationException("快捷键被占用，原快捷键保持有效。错误：" + hotkey.LastError);
        try { requested.Save(); }
        catch { if (hotkey is not null) { if (previous.Enabled) hotkey.TryRegister(previous.Modifiers, previous.Key); else hotkey.Unregister(); } throw; }
        Preferences = requested; ApplyRegistration();
    }
    public async Task StartCaptureAsync(Window? initiator = null)
    {
        if (disposed || IsActive || !canStart()) return;
        if (CaptureEnvironment.IsEditingCredentials) { Status = "请先关闭服务设置窗口。"; Changed?.Invoke(); return; }
        starting = true;
        var previousFocus = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        try
        {
            if (initiator?.IsVisible == true) { hidden.Add(initiator); initiator.Hide(); }
            await Task.Delay(160);
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (disposed) return;
                var targets = AcquireTargets();
                session = new CaptureSession(AcquireSnapshot(), targets);
                session.Completed += () => { session = null; RestoreWindows(); Status = "截图已结束；" + (Preferences.Enabled ? "快捷键：" + Preferences : "可点击开始截图"); Changed?.Invoke(); };
                session.Show(); Status = "截图中";
            });
        }
        catch (Exception ex) { await Application.Current.Dispatcher.InvokeAsync(() => { session?.Abort(); session = null; RestoreWindows(); Status = "无法开始截图：" + ex.Message; }); }
        finally { await Application.Current.Dispatcher.InvokeAsync(() => { starting = false; Changed?.Invoke(); }); }
        void RestoreWindows()
        {
            foreach (var window in hidden.ToArray()) { if (!disposed) window.Show(); }
            hidden.Clear();
            if (!disposed && previousFocus?.IsVisible == true) previousFocus.Activate();
        }
    }
    public bool TryPrepareExit(bool updating)
    {
        if (!IsActive) return true;
        if (starting || session?.Busy == true || updating)
        {
            session?.ActivateResult();
            MessageBox.Show("请先完成或取消当前截图、识别、长图编辑或录制，再退出或更新。录制可通过控制条停止并保存。", "Sticky", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        if (session?.Annotations?.Items.Count > 0 && MessageBox.Show("当前截图包含尚未保存的标注，放弃并退出？", "Sticky", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return false;
        session?.Abort(); return true;
    }
    private void DisplayChanged(object? sender, EventArgs e) => Application.Current.Dispatcher.BeginInvoke(new Action(() => session?.Abort()));
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        SystemEvents.DisplaySettingsChanged -= DisplayChanged;
        hotkey?.Dispose(); hotkey = null; session?.Abort(); session = null;
        hidden.Clear();
    }
}
