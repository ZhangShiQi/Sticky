using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Sticky.App;
using Sticky.App.ViewModels;
using Sticky.Core;
using Sticky.Storage;

internal static partial class Program
{
    private static void InteractionPreferences()
    {
        var legacy = Store("legacy-interaction-settings");
        File.WriteAllText(Path.Combine(legacy.Root, "device", "editor.json"), "{\"schemaVersion\":1,\"defaultFontSize\":20,\"paragraphSpacing\":4,\"showHeaderOnHover\":true}");
        var loaded = legacy.LoadEditorPreferences();
        Check(loaded.RandomizeNewNoteColor && loaded.ShowHeaderOnHover && !loaded.AutoHideHeader && loaded.DefaultFontSize == 20, "Old preference files did not keep titles visible by default.");
        var workspace = new StickyWorkspace(Path.Combine(_root, "interaction-settings"));
        var original = new Note { Color = "rose" }; workspace.Store.Save(original); workspace.Notes.Add(original); workspace.OpenNote(original);
        try
        {
            Check(workspace.EditorPreferences.RandomizeNewNoteColor && workspace.EditorPreferences.ShowHeaderOnHover && !workspace.EditorPreferences.AutoHideHeader, "New settings did not keep the title bar visible by default.");
            workspace.CreateNote();
            var randomized = workspace.Notes[0];
            Check(NotePalette.Choices.Any(c => c.Key == randomized.Color) && workspace.Store.LoadNotes().Single(n => n.Id == randomized.Id).Color == randomized.Color, "New-note color was invalid or not saved.");
            workspace.ShowSettings(SettingsSection.Typography);
            var settings = workspace.Settings!;
            ((CheckBox)settings.FindName("RandomColorCheckBox")).IsChecked = false;
            ((CheckBox)settings.FindName("AutoHideHeaderCheckBox")).IsChecked = true;
            ((CheckBox)settings.FindName("HoverHeaderCheckBox")).IsChecked = false;
            ClickSaveTypography(settings);
            Check(!workspace.EditorPreferences.RandomizeNewNoteColor && !workspace.EditorPreferences.ShowHeaderOnHover && workspace.EditorPreferences.AutoHideHeader, "Settings UI did not save the new switches.");
            workspace.CreateNote();
            Check(workspace.Notes[0].Color == "amber" && original.Color == "rose", "Disabling random colors changed old notes or failed to restore the default color.");
            Check(!workspace.Store.LoadEditorPreferences().RandomizeNewNoteColor && !workspace.Store.LoadEditorPreferences().ShowHeaderOnHover && workspace.Store.LoadEditorPreferences().AutoHideHeader, "Behavior settings did not persist.");
            ((TextBox)settings.FindName("DefaultFontSizeBox")).Text = "22"; ClickSaveTypography(settings);
            Check(!workspace.EditorPreferences.RandomizeNewNoteColor && !workspace.EditorPreferences.ShowHeaderOnHover && workspace.EditorPreferences.AutoHideHeader, "Saving typography reset the behavior switches.");
        }
        finally { workspace.PrepareExit(); }
        var restarted = new StickyWorkspace(workspace.Store.Root);
        try { Check(!restarted.EditorPreferences.RandomizeNewNoteColor && !restarted.EditorPreferences.ShowHeaderOnHover && restarted.EditorPreferences.AutoHideHeader, "Restart reset the new switches."); }
        finally { restarted.PrepareExit(); }
    }

    private static void ClickSaveTypography(SettingsWindow settings)
    {
        var tab = (TabItem)((TabControl)settings.FindName("SettingsTabs")).SelectedItem;
        var panel = (StackPanel)((ScrollViewer)tab.Content).Content;
        panel.Children.OfType<StackPanel>().Last().Children.OfType<Button>().First().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static void HoverHeader()
    {
        var workspace = new StickyWorkspace(Path.Combine(_root, "hover-header"));
        workspace.UpdateEditorPreferences(new EditorPreferences { AutoHideHeader = true }); workspace.CreateNote(); Pump(30);
        var note = workspace.Notes[0]; var window = workspace.Windows[note.Id];
        var editor = (RichTextBox)window.FindName("Editor");
        var header = (Border)window.FindName("HeaderBar");
        var formatting = (Border)window.FindName("FormattingBar");
        void Pointer(bool entered)
        {
            window.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = entered ? Mouse.MouseEnterEvent : Mouse.MouseLeaveEvent });
            window.UpdateLayout();
        }
        try
        {
            window.FinishEditing(); Pointer(false);
            var modified = note.ModifiedAt;
            Check(!window.IsHeaderExpanded && header.ActualHeight == 8, "Idle header did not shrink to a narrow strip.");
            Pointer(true);
            Check(window.IsHeaderExpanded && header.ActualHeight == 32 && ((DockPanel)header.Child).IsVisible, "Pointer entry did not reveal header controls.");
            Check(!window.IsEditing && editor.IsReadOnly && !formatting.IsVisible, "Hover entered editing or showed the bottom toolbar.");
            Pointer(false); Check(!window.IsHeaderExpanded && header.ActualHeight == 8, "Pointer leave did not collapse the header.");
            window.EnterEditMode(); Pointer(false);
            Check(window.IsHeaderExpanded && window.IsEditing && header.ActualHeight == 32, "Pointer leave collapsed the header during editing.");
            var menu = editor.ContextMenu!; menu.PlacementTarget = editor; menu.IsOpen = true; Pump(20); Pointer(false);
            Check(window.IsHeaderExpanded, "Leaving the note to use its menu collapsed the header.");
            menu.IsOpen = false; Pump(150); Pointer(false);
            Check(window.IsHeaderExpanded == window.IsEditing, "Closing the menu did not restore the editing/reading header state.");
            workspace.UpdateEditorPreferences(new EditorPreferences { AutoHideHeader = true, ShowHeaderOnHover = false });
            window.FinishEditing(); Pointer(true);
            Check(!window.IsHeaderExpanded, "The hover switch did not apply to an open window.");
            window.EnterEditMode(); Pointer(false);
            Check(window.IsHeaderExpanded && header.ActualHeight == 32, "Disabling hover did not preserve the existing editing toolbar.");
            window.FinishEditing();
            Check(window.SaveNow(false) && note.ModifiedAt == modified, "Hover changed saved note content or modification time.");
        }
        finally { workspace.PrepareExit(); }
    }

    private static int InstanceProbe(string[] args)
    {
        using var host = new SingleInstanceHost(args[1]);
        if (args[2] == "primary") return host.IsPrimary ? 0 : 21;
        if (host.IsPrimary) return 22;
        return host.NotifyExistingAsync().GetAwaiter().GetResult() ? 0 : 23;
    }

    private static void EditingHeaderStability()
    {
        var workspace = new StickyWorkspace(Path.Combine(_root, "editing-header-stability"));
        workspace.UpdateEditorPreferences(new EditorPreferences { AutoHideHeader = true });
        workspace.CreateNote(); Pump(30);
        var note = workspace.Notes[0]; var window = workspace.Windows[note.Id];
        var editor = (RichTextBox)window.FindName("Editor");
        var header = (Border)window.FindName("HeaderBar");
        var formatting = (Border)window.FindName("FormattingBar");
        void Pointer(bool entered)
        {
            window.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = entered ? Mouse.MouseEnterEvent : Mouse.MouseLeaveEvent });
            window.UpdateLayout();
        }
        try
        {
            window.EnterEditMode(); Pointer(true);
            editor.AppendText("这个软件还挺有意思的");
            editor.CaretPosition = editor.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
            window.UpdateLayout();
            var caret = editor.CaretPosition;
            var caretY = caret.GetCharacterRect(LogicalDirection.Forward).Y;
            var editorTop = editor.TranslatePoint(new Point(), window).Y;
            for (var i = 0; i < 12; i++)
            {
                Pointer(false);
                Check(window.IsHeaderExpanded && header.ActualHeight == 32, "A transient pointer leave collapsed the editing header.");
                Check(window.IsEditing && !editor.IsReadOnly && formatting.IsVisible, "Pointer leave interrupted editing.");
                Check(Math.Abs(editor.TranslatePoint(new Point(), window).Y - editorTop) < 0.5, "The document jumped when a popup covered the pointer.");
                Check(editor.CaretPosition.CompareTo(caret) == 0 && Math.Abs(caret.GetCharacterRect(LogicalDirection.Forward).Y - caretY) < 0.5, "The caret moved during transient mouse events.");
                Pointer(true);
            }
            editor.AppendText("，非常有趣");
            Check(window.SaveNow(false) && workspace.Store.LoadNotes().Single().PlainText.Contains("非常有趣"), "Typing or saving failed after transient mouse events.");
            window.FinishEditing(); Pointer(false);
            Check(!window.IsHeaderExpanded && header.ActualHeight == 8, "Finishing editing did not restore the compact reading state.");
        }
        finally { workspace.PrepareExit(); }
    }

    private static void SingleInstance()
    {
        var key = "Sticky.Test." + Guid.NewGuid().ToString("N");
        using var activated = new AutoResetEvent(false);
        using (var primary = new SingleInstanceHost(key))
        {
            Check(primary.IsPrimary, "First process could not acquire the application lock.");
            primary.StartListening(() => activated.Set());
            RunInstanceProbe(key, "secondary", Path.Combine(_root, "another-workspace"));
            Check(activated.WaitOne(2000), "A second process did not activate the existing instance.");
            RunInstanceProbe(key, "secondary", Path.Combine(_root, "third-workspace"));
            Check(activated.WaitOne(2000), "Activation stopped working after the first duplicate launch.");
        }
        RunInstanceProbe(key, "primary", Path.Combine(_root, "restart-workspace"));
    }

    private static void RunInstanceProbe(string key, string mode, string root)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var argument in new[] { "--instance-probe", key, mode, "--data-dir", root }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        if (!process.WaitForExit(8000)) { process.Kill(); throw new Exception("Secondary process did not exit promptly."); }
        Check(process.ExitCode == 0, $"Instance probe failed: {process.ExitCode}.");
        Check(!Directory.Exists(root), "A duplicate process initialized a second workspace.");
    }
}
