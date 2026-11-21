using System.Windows;
using Sticky.App.AI;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Sticky.Core;

namespace Sticky.App;

public partial class NoteWindow
{
    private void AiPolishClicked(object sender, RoutedEventArgs e)
    {
        if (_workspace.IsVaultNote(_note.Id)) return;
        try
        {
            var actions = _workspace.AiSettings.EditingCatalog().Actions.Where(a => a.Enabled && a.DeletedAt is null).OrderBy(a => a.Order).ThenBy(a => a.Id).ToList();
            var menu = new ContextMenu { PlacementTarget = AiButton, Placement = PlacementMode.Top };
            foreach (var action in actions)
            {
                var item = new MenuItem { Header = action.Name, Icon = AiActionIcons.Create(action.Icon), Tag = action.Id,
                    IsEnabled = !string.IsNullOrWhiteSpace(_note.IsMarkdown ? MarkdownEditor.SelectedText : Editor.Selection.Text), ToolTip = action.Prompt };
                item.Click += (_, _) => ExecuteAiAction(action); menu.Items.Add(item);
            }
            if (actions.Count == 0) menu.Items.Add(new MenuItem { Header = "尚无启用的 AI 功能", IsEnabled = false });
            menu.Items.Add(new Separator());
            var settings = new MenuItem { Header = "管理 AI 功能与接入…" };
            settings.Click += (_, _) => _workspace.ShowSettings(SettingsSection.AI); menu.Items.Add(settings);
            menu.Opened += (_, _) => { _menuOpen = true; UpdateHeaderState(); };
            menu.Closed += (_, _) =>
            {
                _menuOpen = false; UpdateHeaderState();
                Dispatcher.BeginInvoke(() => { if (IsVisible && !IsActive && _modalDepth == 0) FinishEditing(); }, DispatcherPriority.Background);
            };
            AiButton.ContextMenu = menu; menu.IsOpen = true;
        }
        catch (Exception) { _workspace.ShowSettings(SettingsSection.AI); }
    }
    private void ExecuteAiAction(AiAction action)
    {
        var configuration = _workspace.AiSettings.Load();
        if (!configuration.CanRun)
        { _workspace.ShowSettings(SettingsSection.AI); return; }
        try
        {
            using var selection = _note.IsMarkdown ? null : AiTextSelection.Capture(Editor);
            if (!SaveNow()) return;
            var original = selection?.OriginalText ?? MarkdownEditor.SelectedText;
            if (string.IsNullOrWhiteSpace(original) || original.Length > AiClient.MaxInputCharacters)
                throw new AiException("请选中 1–20,000 字符的文字。");
            var markdown = MarkdownEditor.Text;
            var start = MarkdownEditor.SelectionStart;
            var priorModified = _note.ModifiedAt;
            bool CanApply() => !_workspace.IsExiting && IsVisible && _note.DeletedAt is null
                && (selection?.CanApply ?? (_note.IsMarkdown && _note.ModifiedAt == priorModified && MarkdownEditor.Text == markdown));

            _modalDepth++;
            try
            {
                var dialog = new AiPolishWindow(_workspace.AiClient, configuration, original, CanApply, result =>
                {
                    if (!CanApply()) return false;
                    EnterEditMode();
                    _historyBatch = Guid.NewGuid().ToString("N");
                    if (selection is not null)
                    {
                        if (!selection.Apply(result)) return false;
                    }
                    else
                    {
                        if (string.IsNullOrWhiteSpace(result) || result.Length > AiClient.MaxOutputCharacters)
                            throw new AiException("结果不能为空或超过 60,000 字符。");
                        var trimmed = original.Trim();
                        var offset = original.IndexOf(trimmed, StringComparison.Ordinal);
                        MarkdownEditor.Select(start + offset, trimmed.Length);
                        MarkdownEditor.SelectedText = result.Trim();
                    }
                    SaveNow(false, "AI · " + action.Name);
                    _historyBatch = Guid.NewGuid().ToString("N");
                    return true;
                }, action) { Owner = this };
                dialog.ShowDialog();
            }
            finally { _modalDepth--; if (IsVisible && !_workspace.IsExiting) { Activate(); FocusBody(); } }
        }
        catch (Exception ex) { MessageBox.Show(this, ex is AiException ? ex.Message : "无法打开 AI 功能，请检查配置后重试。", "AI 功能", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
}
