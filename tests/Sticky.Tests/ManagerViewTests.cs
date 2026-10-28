using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sticky.App;
using Sticky.App.Controls;
using Sticky.App.ViewModels;
using Sticky.Core;

internal static partial class Program
{
    private static IEnumerable<T> ManagerVisuals<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in ManagerVisuals<T>(child)) yield return nested;
        }
    }
    private static void ManagerScreenshot(Window window, string name)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        using var output = File.Create(Path.Combine(_root, name)); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); png.Save(output);
    }
    private static void ManagerViews()
    {
        var workspace = new StickyWorkspace(Path.Combine(_root, "manager-views"));
        var group = new NoteGroup { Name = "工作" }; workspace.Groups.Add(group); workspace.Store.SaveGroups(workspace.Groups);
        foreach (var (title, color, grouped) in new[] { ("周末出行", "sage", false), ("采购清单", "amber", true), ("忽然想到", "lavender", false), ("阅读笔记", "sky", true) })
        {
            var note = new Note { Title = title, Color = color, GroupId = grouped ? group.Id : null, Blocks = [new NoteBlock { Inlines = [new NoteInline { Text = title + "\n这里是便签的正文预览。" }] }] };
            workspace.Store.Save(note); workspace.Notes.Add(note);
        }
        var main = new MainWindow(workspace); main.Show(); Pump(40); main.UpdateLayout();
        var vm = (MainViewModel)main.DataContext; var list = (ListBox)main.FindName("NotesList");
        try
        {
            Check(!vm.IsTitleView && vm.Cards.Count == 4 && ManagerVisuals<VirtualizingWrapPanel>(main).Count() == 1, "The default preview view or virtualized panel was missing.");
            ManagerScreenshot(main, "manager-preview.png");
            ((Button)main.FindName("ViewModeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(40); main.UpdateLayout();
            Check(vm.IsTitleView && workspace.Store.LoadManagerPreferences().ViewMode == "titles" && vm.Cards.Count == 4, "Switching to titles did not persist or altered the note filter.");
            ManagerScreenshot(main, "manager-titles.png");
            var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            var label = ManagerVisuals<Button>(container).Single(); var selected = (NoteCard)label.DataContext;
            Check(ManagerVisuals<TextBlock>(label).Any(t => t.Text == selected.Title) && !ManagerVisuals<TextBlock>(label).Any(t => t.Text == selected.Preview), "Compact mode showed a preview instead of only the title.");
            label.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(20);
            Check(workspace.Windows.ContainsKey(selected.Note.Id), "A compact title did not open its own note."); workspace.Windows[selected.Note.Id].Close(); Pump(20);
            list.UpdateLayout(); container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0); label = ManagerVisuals<Button>(container).Single(); selected = (NoteCard)label.DataContext;
            var menu = label.ContextMenu!; menu.PlacementTarget = label; menu.IsOpen = true; Pump(20);
            Check(menu.Items.OfType<MenuItem>().Any(i => i.Header?.ToString() == "移动到分组"), "Compact mode lost the move operation.");
            var delete = menu.Items.OfType<MenuItem>().Single(i => i.Header?.ToString() == "移入回收站"); menu.IsOpen = false; delete.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump(30);
            Check(selected.Note.DeletedAt is not null && vm.Cards.Count == 3, "Deleting a title did not remove the correct note.");
            vm.ShowDeleted = true; Pump(30); list.UpdateLayout();
            Check(vm.Cards.Count == 1 && vm.IsTitleView, "Recycle-bin filtering changed the chosen view.");
            container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0); label = ManagerVisuals<Button>(container).Single(); label.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(30);
            Check(selected.Note.DeletedAt is null && vm.Cards.Count == 0, "A compact recycle-bin title did not restore the correct note.");
            vm.SelectGroup(group.Id); Check(vm.Cards.All(c => c.Note.GroupId == group.Id) && vm.IsTitleView, "Group filtering did not preserve title mode.");
            workspace.RenameGroup(group, "工作记录"); Check(vm.GroupItems.Single().Label.StartsWith("工作记录") && vm.Cards.All(c => c.GroupName == "工作记录"), "Incremental refresh lost a group rename.");
        }
        finally { workspace.PrepareExit(); }
        var restarted = new StickyWorkspace(workspace.Store.Root);
        try
        {
            using var vm2 = new MainViewModel(restarted); Check(vm2.IsTitleView, "Restart did not remember the chosen view.");
            restarted.Store.SaveManagerPreferences(new ManagerPreferences { ViewMode = "preview" });
            var path = Path.Combine(restarted.Store.Root, "device", "manager.json"); File.WriteAllText(path, "{broken");
            Check(restarted.Store.LoadManagerPreferences().ViewMode == "titles", "A damaged view preference did not recover its last saved backup.");
            var before = File.ReadAllBytes(path);
            try { restarted.Store.SaveManagerPreferences(new ManagerPreferences { ViewMode = "unknown" }); throw new InvalidOperationException("Invalid view mode was accepted."); } catch (InvalidDataException) { }
            Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(before), "Invalid view preferences replaced a healthy file.");
            var future = "{\"schemaVersion\":2,\"viewMode\":\"future\"}"; File.WriteAllText(path, future);
            Check(restarted.Store.LoadManagerPreferences().ViewMode == "preview" && File.ReadAllText(path) == future, "A future preference schema was overwritten with a backup.");
        }
        finally { restarted.PrepareExit(); }
    }
    private static void ManagerScale()
    {
        var workspace = new StickyWorkspace(Path.Combine(_root, "manager-scale"));
        for (var g = 0; g < 100; g++) workspace.Groups.Add(new NoteGroup { Name = $"分组 {g:000}" });
        var start = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5000; i++) workspace.Notes.Add(new Note { Title = $"便签 {i:0000}", GroupId = workspace.Groups[i % 100].Id, ModifiedAt = start.AddSeconds(-i),
            Blocks = [new NoteBlock { Inlines = [new NoteInline { Text = "这是便签正文，用于验证只渲染当前屏幕内的卡片。" }] }] });
        var huge = workspace.Notes[^1]; huge.Title = ""; huge.Blocks[0].Inlines[0].Text = new string('字', 2_000_000);
        var timer = Stopwatch.StartNew(); var main = new MainWindow(workspace); main.Show(); Pump(30); main.UpdateLayout(); var previewMs = timer.Elapsed.TotalMilliseconds;
        var vm = (MainViewModel)main.DataContext; var list = (ListBox)main.FindName("NotesList");
        try
        {
            var panel = ManagerVisuals<VirtualizingWrapPanel>(main).Single();
            var previewContainers = ManagerVisuals<ListBoxItem>(list).Count();
            Check(vm.Cards.Count == 5000 && previewContainers is > 0 and < 100 && list.ItemContainerGenerator.ContainerFromIndex(4999) is null, "Preview view realized thousands of cards instead of the viewport.");
            Check(vm.GroupItems.All(g => g.Count == 50), "One-pass group counts were incorrect.");
            var first = vm.Cards[0]; var resets = 0; var events = 0;
            vm.Cards.CollectionChanged += (_, e) => { events++; if (e.Action == NotifyCollectionChangedAction.Reset) resets++; };
            var refreshTimes = new List<double>();
            for (var i = 0; i < 8; i++) { timer.Restart(); vm.Refresh(); refreshTimes.Add(timer.Elapsed.TotalMilliseconds); }
            Check(events == 0 && ReferenceEquals(first, vm.Cards[0]), "An unchanged refresh discarded the collection or rebuilt note cards.");
            first.Note.Title = "已修改的标题"; vm.Refresh(); main.UpdateLayout();
            Check(first.Title == "已修改的标题" && events == 0, "A title edit reset all cards or left stale text.");
            timer.Restart(); panel.SetVerticalOffset(double.PositiveInfinity); Pump(30); main.UpdateLayout(); var previewEndMs = timer.Elapsed.TotalMilliseconds;
            Check(list.ItemContainerGenerator.ContainerFromIndex(4999) is ListBoxItem && list.ItemContainerGenerator.ContainerFromIndex(0) is null
                && ManagerVisuals<ListBoxItem>(list).Count() < 100, "End scrolling failed to recycle preview containers.");
            Check(vm.Cards[^1].Preview.Length <= 241 && vm.Cards[^1].Title.Length <= 513, "A very large note produced unbounded card text.");
            timer.Restart(); ((Button)main.FindName("ViewModeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(30); main.UpdateLayout(); var titlesMs = timer.Elapsed.TotalMilliseconds;
            var titleContainers = ManagerVisuals<ListBoxItem>(list).Count();
            Check(vm.IsTitleView && titleContainers is > 0 and < 150 && vm.Cards.Count == 5000, "Title mode did not virtualize its compact cells.");
            panel = ManagerVisuals<VirtualizingWrapPanel>(main).Single(); panel.SetVerticalOffset(double.PositiveInfinity); Pump(30); main.UpdateLayout();
            var endContainer = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(4999);
            var label = ManagerVisuals<Button>(endContainer).Single();
            Check(((NoteCard)label.DataContext).Note.Id == huge.Id && ManagerVisuals<ListBoxItem>(list).Count() < 150, "A recycled title showed or targeted another note.");
            var recycledMenu = label.ContextMenu!; recycledMenu.PlacementTarget = label; recycledMenu.IsOpen = true; Pump(20);
            panel.SetVerticalOffset(0); Pump(30); main.UpdateLayout();
            Check(!recycledMenu.IsOpen, "Recycling an offscreen title left a menu targeting the previous note open.");
            main.Width = 900; main.Height = 600; Pump(30); main.UpdateLayout();
            Check(panel.ExtentHeight > panel.ViewportHeight && ManagerVisuals<ListBoxItem>(list).Count() < 150, "Resizing disabled virtualization.");
            huge.ModifiedAt = start.AddDays(1); vm.Refresh(); Pump(30); main.UpdateLayout();
            Check(vm.Cards[0].Note.Id == huge.Id && resets == 1 && events == 1, "Reordering did not use one batched reset or targeted the wrong note.");
            vm.SelectGroup(workspace.Groups[0].Id); Pump(20); main.UpdateLayout(); Check(vm.Cards.Count == 50, "Group filtering failed after recycling and reordering.");
            vm.SelectGroup(null); Pump(20); main.UpdateLayout();
            list.ScrollIntoView(vm.Cards[^1]); Pump(30); main.UpdateLayout(); Check(list.ItemContainerGenerator.ContainerFromIndex(4999) is not null, "ScrollIntoView could not realize an offscreen note.");
            File.WriteAllText(Path.Combine(_root, "manager-performance.json"), JsonSerializer.Serialize(new { notes = 5000, groups = 100, previewMs, previewContainers,
                previewEndMs, titlesMs, titleContainers, refreshMedianMs = refreshTimes.Order().ElementAt(refreshTimes.Count / 2) }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { workspace.PrepareExit(); }
    }
}
