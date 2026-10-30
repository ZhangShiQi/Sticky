using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Sticky.Capture.Services;

namespace Sticky.Capture;

public partial class TranslationSettingsWindow : Window
{
    private readonly TranslationSettingsStore store;
    private readonly ITranslationService service;
    private readonly CancellationTokenSource lifetime = new();
    private bool testing;
    private bool embedded;
    public FrameworkElement CreateEmbeddedView() { embedded = true; var view = (FrameworkElement)Content; Content = null; view.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Sticky.Capture;component/CaptureResources.xaml") }); return view; }
    public void ReleaseEmbeddedView() { lifetime.Cancel(); Close(); }
    public void ReloadSavedSettings() { if (testing) return; try { SetFields(store.Load()); StatusText.Text = "已重新载入保存的设置。"; } catch (TranslationException ex) { StatusText.Text = ex.Message; } }
    public bool WasSaved { get; private set; }
    public TranslationSettingsWindow() : this(new(), new AzureTranslationService()) { }
    internal TranslationSettingsWindow(TranslationSettingsStore store, ITranslationService service)
    {
        CaptureEnvironment.ApplyStyles(this); InitializeComponent();
        this.store = store;
        this.service = service;
        try { SetFields(store.Load()); }
        catch (TranslationException ex) { SetFields(new()); StatusText.Text = ex.Message; }
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Closed += (_, _) => lifetime.Cancel();
    }
    internal void SetFields(TranslationSettings settings)
    {
        EndpointText.Text = settings.Endpoint; RegionText.Text = settings.Region; KeyText.Password = settings.ApiKey;
    }
    private TranslationSettings ReadFields() => new() { Endpoint = EndpointText.Text.Trim(), Region = RegionText.Text.Trim().ToLowerInvariant(), ApiKey = KeyText.Password.Trim() };
    private void CancelClick(object sender, RoutedEventArgs e) { if (!embedded) Close(); else { try { SetFields(store.Load()); StatusText.Text = "已重新载入保存的设置。"; } catch (Exception ex) { StatusText.Text = ex.Message; } } }
    private void HelpClick(object sender, RoutedEventArgs e)
    {
        try { LinkService.Open("https://learn.microsoft.com/azure/ai-services/translator/text-translation/quickstart/rest-api"); }
        catch { StatusText.Text = "无法打开浏览器。请在 Azure 门户创建 Translator 资源，并复制“密钥和终结点”中的配置。"; }
    }
    private void SaveClick(object sender, RoutedEventArgs e) => SaveSettings();
    internal void SaveSettings()
    {
        if (testing || lifetime.IsCancellationRequested) return;
        try { store.Save(ReadFields()); WasSaved = true; if (!embedded) Close(); else StatusText.Text = "设置已保存。"; }
        catch (TranslationException ex) { StatusText.Text = ex.Message; }
        catch { StatusText.Text = "保存失败，请检查当前用户配置目录的写入权限后重试。"; }
    }
    private async void TestClick(object sender, RoutedEventArgs e) => await TestConnectionAsync();
    internal async Task TestConnectionAsync()
    {
        if (testing || lifetime.IsCancellationRequested) return;
        testing = true; SetBusy();
        StatusText.Text = "正在测试连接… 关闭此窗口可取消。";
        try
        {
            var settings = ReadFields(); settings.Validate();
            var result = await service.TranslateAsync("Hello", "en", "zh-Hans", settings, lifetime.Token);
            if (!lifetime.IsCancellationRequested) StatusText.Text = "连接成功，示例译文：" + result.Text + "。点击保存后生效。";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (TranslationException ex) { if (!lifetime.IsCancellationRequested) StatusText.Text = ex.Message; }
        catch { if (!lifetime.IsCancellationRequested) StatusText.Text = "连接测试失败，请检查服务配置后重试。"; }
        finally { testing = false; SetBusy(); }
    }
    private void SetBusy() { SettingsFields.IsEnabled = !testing; SaveButton.IsEnabled = !testing; TestButton.IsEnabled = !testing; }
    internal string Status => StatusText.Text;
}
