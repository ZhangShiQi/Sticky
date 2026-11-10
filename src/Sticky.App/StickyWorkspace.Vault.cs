using System.Windows;
using Sticky.Core;
using Sticky.Storage;
using Sticky.Sync.Git;

namespace Sticky.App;

public sealed partial class StickyWorkspace
{
    public VaultStore Vault { get; }
    public VaultMigration VaultMigrations { get; }
    public VaultWindow? VaultWindow { get; private set; }
    public bool VaultTransferInProgress { get; private set; }
    public void ShowVault()
    {
        if (VaultWindow is null)
        {
            VaultWindow = new VaultWindow(this);
            VaultWindow.Closed += (_, _) => VaultWindow = null;
        }
        if (VaultWindow.WindowState == System.Windows.WindowState.Minimized) VaultWindow.WindowState = System.Windows.WindowState.Normal;
        VaultWindow.Show(); VaultWindow.Activate();
    }
    public async void MoveIntoVault(Note note)
    {
        if (VaultTransferInProgress) return;
        if (Sync.IsBusy) { MessageBox.Show("请等待当前同步完成后，再移入保险箱。", "Sticky"); return; }
        VaultTransferInProgress = true;
        try
        {
            ShowVault();
            if (!await VaultWindow!.EnsureUnlockedAsync()) return;
            if (!SaveAllNotes() || !VaultWindow.SaveCurrent()) return;
            if (MessageBox.Show(VaultWindow, "将加密此便签、图片和本地历史，并清理应用管理的原便签与备份。\n\n以前导出的文件、Git 提交及本地 Git 缓存仍可能保留明文，需另行处理；删除文件也不保证擦除磁盘痕迹。保险箱通过加密导出文件迁移，不参与 Git 同步。继续？",
                "移入保险箱", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            // Close all views that might retain old content or later write it back.
            Search?.Close(); History?.Close();
            if (Windows.TryGetValue(note.Id, out var window))
            {
                window.Close();
                if (Windows.ContainsKey(note.Id)) return;
            }
            VaultMigrations.MoveIn(Vault, note);
            Notes.Remove(note); Device.Windows.Remove(note.Id); SaveDevice(); NotifyChanged();
            VaultWindow.RefreshNotes(note.Id);
        }
        catch (Exception ex)
        {
            // Once journaled, source cleanup is resumable and must never be presented as
            // an ordinary note again, even if deletion failed halfway through.
            if (VaultMigrations.MovedIds.Contains(note.Id)) { Notes.Remove(note); NotifyChanged(); }
            ShowError("移入未完成，请保留数据并重启重试；已写入的加密副本会保留", ex);
        }
        finally { VaultTransferInProgress = false; }
    }
    private void ExcludeVaultSources(SyncSnapshot snapshot)
    {
        var moved = VaultMigrations.MovedIds;
        foreach (var id in snapshot.Notes.Where(p => moved.Contains(p.Key) || p.Value.ConflictOf is { } source && moved.Contains(source)).Select(p => p.Key).ToArray())
            snapshot.Notes.Remove(id);
        foreach (var transfer in VaultMigrations.Transfers) snapshot.Notes[transfer.Id] = VaultMigrations.Tombstone(transfer);
        var used = snapshot.Notes.Values.SelectMany(n => VaultStore.ImageIds(n.Blocks)).ToHashSet();
        foreach (var asset in snapshot.Assets.Keys.Where(id => !used.Contains(id)).ToArray()) snapshot.Assets.Remove(asset);
    }
}
