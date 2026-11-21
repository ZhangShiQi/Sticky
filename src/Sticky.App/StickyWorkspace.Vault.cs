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
    public List<Note> PrivateNotes { get; } = [];
    private readonly Dictionary<Guid, Core.WindowState> _vaultWindows = [];
    private NoteStore? _privateStore;
    // Keep classification after lock so stale UI references can never fall back to plaintext storage.
    private readonly HashSet<Guid> _privateIds = [];
    public bool VaultViewsHidden { get; private set; }
    public bool IsVaultNote(Guid id) => _privateIds.Contains(id);
    public NoteStore StoreFor(Note note) => IsVaultNote(note.Id) ? _privateStore ??= new NoteStore(Store.Root, Vault) : Store;
    private Core.WindowState VaultWindowState(Guid id)
    {
        if (!_vaultWindows.TryGetValue(id, out var state)) _vaultWindows[id] = state = new Core.WindowState();
        return state;
    }
    public VaultWindow EnsureVaultController()
    {
        if (VaultWindow is null)
        {
            VaultWindow = new VaultWindow(this);
            VaultWindow.Closed += (_, _) => VaultWindow = null;
        }
        return VaultWindow;
    }
    public void ShowVault()
    {
        ShowManager();
        Manager!.SelectVault();
        EnsureVaultController();
    }
    public void RefreshPrivateNotes()
    {
        var existing = PrivateNotes.ToDictionary(n => n.Id);
        PrivateNotes.Clear();
        if (Vault.IsUnlocked)
            foreach (var note in Vault.Notes)
            {
                _privateIds.Add(note.Id);
                PrivateNotes.Add(existing.GetValueOrDefault(note.Id) ?? note);
            }
        NotifyChanged();
    }
    public void TouchVault() => VaultWindow?.Touch();
    public void CreateVaultNote(NoteWindow? near = null)
    {
        if (!Vault.IsUnlocked || VaultViewsHidden) return;
        var note = NewNote();
        try { Vault.Save(note); _privateIds.Add(note.Id); PrivateNotes.Insert(0, note); NotifyChanged(); OpenNote(note, true, near); }
        catch (Exception ex) { ShowError("无法创建私密便签", ex); }
    }
    public bool SavePrivateNotes()
    {
        foreach (var window in Windows.Where(p => IsVaultNote(p.Key)).Select(p => p.Value).ToArray())
            if (!window.SaveNow(false)) return false;
        return true;
    }
    public bool LockPrivateNotes()
    {
        VaultViewsHidden = true; NotifyChanged();
        var windows = Windows.Where(p => IsVaultNote(p.Key)).Select(p => p.Value).ToArray();
        if (!SavePrivateNotes())
        {
            foreach (var window in windows) window.HidePrivateContent();
            return false;
        }
        foreach (var window in windows) { window.Close(); window.ClearPrivateContent(); }
        foreach (var note in PrivateNotes) { note.Title = ""; note.SetRichText([]); }
        PrivateNotes.Clear(); _vaultWindows.Clear(); Vault.Lock(); VaultViewsHidden = false; NotifyChanged();
        return true;
    }
    public async void MoveIntoVault(Note note)
    {
        if (VaultTransferInProgress) return;
        if (Sync.IsBusy) { MessageBox.Show("请等待当前同步完成后，再移入保险箱。", "Sticky"); return; }
        VaultTransferInProgress = true;
        try
        {
            ShowVault();
            if (!await EnsureVaultController().EnsureUnlockedAsync()) return;
            if (!SaveAllNotes() || !EnsureVaultController().SaveCurrent()) return;
            if (MessageBox.Show(Manager!, "将加密此便签、图片和本地历史，并清理应用管理的原便签与备份。\n\n以前导出的文件、Git 提交及本地 Git 缓存仍可能保留明文，需另行处理；删除文件也不保证擦除磁盘痕迹。保险箱通过加密导出文件迁移，不参与 Git 同步。继续？",
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
            RefreshPrivateNotes();
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
        var used = snapshot.Notes.Values.SelectMany(n => n.GetImageIds()).ToHashSet();
        foreach (var asset in snapshot.Assets.Keys.Where(id => !used.Contains(id)).ToArray()) snapshot.Assets.Remove(asset);
    }
}
