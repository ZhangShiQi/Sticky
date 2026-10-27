using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Sticky.Core;

namespace Sticky.App.AI;

public partial class AiPolishWindow : Window
{
    private readonly AiClient _client;
    private readonly AiConfiguration _configuration;
    private readonly AiTextSelection _selection;
    private readonly Func<string, bool> _apply;
    private CancellationTokenSource? _request;
    private bool _closed;
    private readonly AiAction _action;
    public AiPolishWindow(AiClient client, AiConfiguration configuration, AiTextSelection selection, Func<string, bool> apply, AiAction? action = null)
    {
        _client = client; _configuration = configuration; _selection = selection; _apply = apply;
        _action = action ?? AiAction.Polish();
        InitializeComponent(); OriginalBox.Text = selection.OriginalText;
        Title = "AI · " + _action.Name; ActionHeading.Text = _action.Name;
        ServiceLabel.Text = configuration.CompletionUri().Authority + " · " + configuration.Model
            + (configuration.EffectiveThinkingControl == "none" ? " · 思考由服务商决定" : configuration.ThinkingEnabled ? " · 思考已开启" : " · 思考已关闭");
        Loaded += async (_, _) => await GenerateAsync();
        Closed += (_, _) => { _closed = true; _request?.Cancel(); };
    }
    private async void GenerateClicked(object sender, RoutedEventArgs e) => await GenerateAsync();
    private async Task GenerateAsync()
    {
        if (_request is not null || _closed) return;
        using var source = new CancellationTokenSource(); _request = source;
        GenerateButton.IsEnabled = false; ApplyButton.IsEnabled = false; ResultBox.IsEnabled = false; ResultBox.Clear();
        StatusText.Foreground = Brushes.DarkGreen; StatusText.Text = "正在执行「" + _action.Name + "」…关闭窗口可取消，原文保持不变。";
        try
        {
            var result = await _client.ExecuteAsync(_configuration, _action, _selection.OriginalText, source.Token);
            if (_closed) return;
            ResultBox.IsEnabled = true; ResultBox.Text = result;
            StatusText.Text = _selection.CanApply ? "请检查结果。点击「替换原选区」后才会修改便签。" : "便签已发生变化，不能直接替换。可复制结果，或关闭后重新选择文字。";
        }
        catch (OperationCanceledException) { if (!_closed) StatusText.Text = "已取消，原文未被修改。"; }
        catch (Exception ex)
        {
            if (!_closed) { StatusText.Foreground = Brushes.Firebrick; StatusText.Text = ex is AiException ? ex.Message : "AI 操作暂未完成，原文未被修改，请重试。"; }
        }
        finally { _request = null; if (!_closed) { GenerateButton.IsEnabled = true; UpdateApply(); } }
    }
    private void ResultChanged(object sender, TextChangedEventArgs e) { if (ApplyButton is not null) UpdateApply(); }
    private void UpdateApply() => ApplyButton.IsEnabled = _request is null && _selection.CanApply && !string.IsNullOrWhiteSpace(ResultBox.Text);
    private void ApplyClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_selection.CanApply || !_apply(ResultBox.Text))
            { ApplyButton.IsEnabled = false; StatusText.Text = "原便签已发生变化，未替换任何内容。请重新选中文字后再试。"; return; }
            DialogResult = true;
        }
        catch (Exception ex) { StatusText.Foreground = Brushes.Firebrick; StatusText.Text = ex is AiException ? ex.Message : "替换未完成，请保留预览结果并检查便签。"; }
    }
}
