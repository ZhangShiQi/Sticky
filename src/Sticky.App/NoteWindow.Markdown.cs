using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Runtime.InteropServices;
using Sticky.App.Editor;
using Sticky.Core;
using Sticky.Sync.Git;

namespace Sticky.App;

public partial class NoteWindow
{
    private void ReloadBody()
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            MarkdownEditor.LoadSource(_note.MarkdownSource ?? "");
            MarkdownEditor.FontSize = _workspace.EditorPreferences.DefaultFontSize;
            if (!_note.IsMarkdown)
            {
                Editor.Document = DocumentCodec.Decode(_note, _workspace.StoreFor(_note), darkTheme: true, _workspace.EditorPreferences);
                AttachImageMenus();
            }
            else Editor.Document = new System.Windows.Documents.FlowDocument();
            UpdateBodyMode();
            FitBodyImages();
        }
        finally { _loading = wasLoading; }
    }

    private void UpdateBodyMode()
    {
        Editor.Visibility = _note.IsMarkdown ? Visibility.Collapsed : Visibility.Visible;
        MarkdownEditor.Visibility = _note.IsMarkdown ? Visibility.Visible : Visibility.Collapsed;
        MarkdownEditor.SetEditable(_note.IsMarkdown && IsEditing);
        RichTextTools.Visibility = _note.IsMarkdown ? Visibility.Collapsed : Visibility.Visible;
        Editor.IsReadOnly = !IsEditing || _note.IsMarkdown;
        Editor.VerticalScrollBarVisibility = IsEditing ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden;
    }

    private void FocusBody()
    {
        if (_note.IsMarkdown) MarkdownEditor.TextArea.Focus();
        else Editor.Focus();
    }

    private void ConvertNoteFormat()
    {
        if (!SaveNow()) return;
        _modalDepth++;
        try
        {
            var priorState = SyncJson.Bytes(_note);
            var priorFormat = _note.FormatLabel;
            var conversion = NoteFormatConversion.Preview(_note);
            var result = conversion.Result;
            if (!ConfirmConversion(conversion)) return;
            if (!IsVisible || _workspace.IsExiting || _note.DeletedAt is not null
                || _workspace.IsVaultNote(_note.Id) && (!_workspace.Vault.IsUnlocked || _workspace.VaultViewsHidden)) return;
            if (_dirty || !priorState.AsSpan().SequenceEqual(SyncJson.Bytes(_note)))
            {
                MessageBox.Show(this, "预览期间便签已更新，请重新打开转换预览。", "转换格式", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var store = _workspace.StoreFor(_note);
            result.ModifiedAt = DateTimeOffset.UtcNow;
            using (store.History.WithContext("local", Guid.NewGuid().ToString("N"), $"转换格式 · {priorFormat} → {result.FormatLabel}"))
                store.Save(result);
            _note.CopyBodyFrom(result);
            _note.ModifiedAt = result.ModifiedAt;
            _historyBatch = Guid.NewGuid().ToString("N");
            SetValue(IsEditingProperty, false);
            ReloadAfterSync(true, "已转换为 " + result.FormatLabel);
            _workspace.NotifyChanged();
        }
        catch (Exception ex) { StickyWorkspace.ShowError("无法完成格式转换", ex); }
        finally { _modalDepth--; }
    }

    private bool ConfirmConversion(NoteFormatConversion conversion)
    {
        var dialog = new Window
        {
            Title = conversion.Result.IsMarkdown ? "转换为 Markdown" : "转换为富文本",
            Owner = this, Topmost = Topmost, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 740, Height = 500, MinWidth = 460, MinHeight = 340
        };
        var layout = new Grid { Margin = new Thickness(20) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        var changes = conversion.Changes.Count == 0 ? "" : string.Join("\n", conversion.Changes) + "\n";
        var details = new TextBlock
        {
            Text = changes + "转换前的版本可从历史恢复。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetColumnSpan(details, 3);
        layout.Children.Add(details);
        AddPreview(_note, "原版", 0);
        AddPreview(conversion.Result, "转换后", 2);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true };
        var confirm = new Button { Content = "确认转换", IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
        confirm.SetResourceReference(StyleProperty, "PrimaryButton");
        confirm.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        Grid.SetRow(buttons, 2);
        Grid.SetColumnSpan(buttons, 3);
        layout.Children.Add(buttons);
        dialog.Content = layout;
        if (_workspace.IsVaultNote(_note.Id))
        {
            dialog.PreviewKeyDown += (_, _) => _workspace.TouchVault();
            dialog.PreviewMouseDown += (_, _) => _workspace.TouchVault();
        }
        return dialog.ShowDialog() == true;

        void AddPreview(Note note, string label, int column)
        {
            var panel = new DockPanel();
            var heading = new TextBlock { Text = $"{label} · {note.FormatLabel}", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 7) };
            DockPanel.SetDock(heading, Dock.Top);
            panel.Children.Add(heading);
            var preview = new RichTextBox
            {
                Document = DocumentCodec.Decode(note, _workspace.StoreFor(_note), preferences: _workspace.EditorPreferences),
                IsReadOnly = true, IsDocumentEnabled = false, AllowDrop = false, Padding = new Thickness(10),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            System.Windows.Automation.AutomationProperties.SetName(preview, heading.Text);
            if (_workspace.IsVaultNote(_note.Id))
                CommandManager.AddPreviewExecutedHandler(preview, (_, e) =>
                {
                    if (e.Command != ApplicationCommands.Copy) return;
                    e.Handled = true;
                    if (preview.Selection.IsEmpty) return;
                    try { _workspace.EnsureVaultController().CopyPrivateText(preview.Selection.Text); }
                    catch (ExternalException) { details.Text = changes + "剪贴板忙，请重试。转换前的版本可从历史恢复。"; }
                });
            panel.Children.Add(preview);
            Grid.SetRow(panel, 1);
            Grid.SetColumn(panel, column);
            layout.Children.Add(panel);
        }
    }
}
