using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Konscious.Security.Cryptography;
using Sticky.Core;

namespace Sticky.Storage;

// This store never materializes decrypted documents or images in the filesystem.
public sealed class VaultStore : IDisposable
{
    public const string FileName = "vault.stickyvault";
    private const int Limit = 64 * 1024 * 1024;
    private readonly string _root;
    private byte[]? _key;
    private Envelope? _envelope;
    private Payload? _payload;
    public string FilePath => Path.Combine(_root, FileName);
    public bool Exists => File.Exists(FilePath);
    public bool IsUnlocked => _key is not null;
    public IReadOnlyList<Note> Notes => Clone(Require().Notes);
    public VaultStore(string root) => _root = Path.GetFullPath(root);

    public string Create(string password)
    {
        if (Exists || IsUnlocked) throw new InvalidOperationException("保险箱已存在。");
        ValidatePassword(password);
        var recovery = RandomNumberGenerator.GetBytes(32);
        var key = RandomNumberGenerator.GetBytes(32);
        var envelope = new Envelope { Id = Guid.NewGuid(), Salt = RandomNumberGenerator.GetBytes(16) };
        var derived = Derive(password, envelope.Salt);
        try
        {
            envelope.PasswordKey = Seal(key, derived, Aad(envelope, "password"));
            envelope.RecoveryKey = Seal(key, recovery, Aad(envelope, "recovery"));
            var payload = new Payload();
            Persist(envelope, payload, key);
            _key = key; _envelope = envelope; _payload = payload;
            return Convert.ToHexString(recovery);
        }
        catch { CryptographicOperations.ZeroMemory(key); throw; }
        finally { CryptographicOperations.ZeroMemory(derived); CryptographicOperations.ZeroMemory(recovery); }
    }

    public void Unlock(string password) => Open(password, false);
    public void UnlockWithRecovery(string recovery) => Open(recovery, true);
    private void Open(string secret, bool recovery)
    {
        if (IsUnlocked) throw new InvalidOperationException("请先锁定保险箱。");
        var envelope = ReadEnvelope(FilePath);
        byte[]? derived = null; byte[]? key = null;
        try
        {
            derived = recovery ? Convert.FromHexString(secret.Replace("-", "").Replace(" ", "")) : Derive(secret, envelope.Salt);
            if (derived.Length != 32) throw new CryptographicException();
            key = Unseal(recovery ? envelope.RecoveryKey : envelope.PasswordKey, derived, Aad(envelope, recovery ? "recovery" : "password"));
            var plain = Unseal(envelope.Content, key, Aad(envelope, "content"));
            try
            {
                var payload = JsonSerializer.Deserialize<Payload>(plain, NoteStore.JsonOptions) ?? throw new InvalidDataException("保险箱内容为空。");
                Validate(payload);
                _key = key; key = null; _envelope = envelope; _payload = payload;
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        { throw new CryptographicException("主密码或恢复密钥不正确，或保险箱文件已损坏。", ex); }
        finally
        {
            if (derived is not null) CryptographicOperations.ZeroMemory(derived);
            if (key is not null) CryptographicOperations.ZeroMemory(key);
        }
    }

    public void ChangePassword(string password)
    {
        ValidatePassword(password);
        var payload = Require(); var next = Clone(_envelope!);
        next.Salt = RandomNumberGenerator.GetBytes(16);
        var derived = Derive(password, next.Salt);
        try
        {
            next.PasswordKey = Seal(_key!, derived, Aad(next, "password"));
            Persist(next, payload, _key!); _envelope = next;
        }
        finally { CryptographicOperations.ZeroMemory(derived); }
    }

    public void Save(Note note, IReadOnlyDictionary<string, byte[]>? assets = null, IEnumerable<Note>? history = null, Dictionary<string, byte[]>? sourceBackups = null)
    {
        _ = NoteStore.ParseNote(JsonSerializer.SerializeToUtf8Bytes(note, NoteStore.JsonOptions), note.Id);
        var next = Clone(Require());
        if (sourceBackups is not null) next.SourceBackups[note.Id] = Clone(sourceBackups);
        if (assets is not null) foreach (var (id, bytes) in assets) next.Assets[id] = bytes.ToArray();
        var prior = next.Notes.FirstOrDefault(n => n.Id == note.Id);
        if (!next.History.TryGetValue(note.Id, out var versions)) next.History[note.Id] = versions = [];
        if (history is not null) versions.AddRange(Clone(history.ToList()));
        if (prior is not null) versions.Add(prior);
        next.Notes.RemoveAll(n => n.Id == note.Id); next.Notes.Add(Clone(note));
        Validate(next); Commit(next);
    }

    public IReadOnlyList<Note> Versions(Guid id) => Require().History.TryGetValue(id, out var versions) ? Clone(versions) : [];
    public string AddImage(byte[] bytes)
    {
        var id = Convert.ToHexStringLower(SHA256.HashData(bytes)) + ".png";
        var next = Clone(Require());
        next.Assets[id] = bytes.ToArray();
        Commit(next);
        return id;
    }
    public byte[] ReadImage(string id) => Require().Assets.TryGetValue(id, out var bytes) ? bytes.ToArray() : throw new FileNotFoundException("保险箱中缺少图片。");
    public void Remove(Guid id)
    {
        var next = Clone(Require()); next.Notes.RemoveAll(n => n.Id == id); next.History.Remove(id); next.SourceBackups.Remove(id);
        var used = next.Notes.Concat(next.History.Values.SelectMany(v => v)).SelectMany(n => ImageIds(n.Blocks)).ToHashSet();
        foreach (var asset in next.Assets.Keys.Where(a => !used.Contains(a)).ToArray()) next.Assets.Remove(asset);
        Commit(next);
    }
    private void Commit(Payload next) { Persist(_envelope!, next, _key!); _payload = next; }
    private void Persist(Envelope envelope, Payload payload, byte[] key)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(payload, NoteStore.JsonOptions);
        try
        {
            if (plain.Length > Limit) throw new InvalidDataException("保险箱超过 64 MB，请拆分或清理历史版本。");
            var next = Clone(envelope); next.Content = Seal(plain, key, Aad(next, "content"));
            WorkspaceFiles.WriteAtomic(_root, FilePath, JsonSerializer.SerializeToUtf8Bytes(next, NoteStore.JsonOptions), keepBackup: Exists);
            envelope.Content = next.Content;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void Export(string destination)
    {
        if (!Exists) throw new InvalidOperationException("请先创建保险箱。");
        if (Path.GetFullPath(destination).Equals(FilePath, StringComparison.OrdinalIgnoreCase)) throw new IOException("请选择其他导出路径。");
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.Copy(FilePath, temporary); File.Move(temporary, destination, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    // Import only into an empty destination. Verify the password and all content before writing.
    public void Import(string source, string password, bool recovery = false)
    {
        if (Path.GetExtension(source).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(source);
            var entries = archive.Entries.Where(e => e.FullName == FileName).ToArray();
            if (entries.Length != 1 || entries[0].Length > 96 * 1024 * 1024) throw new InvalidDataException("备份缺少唯一有效的保险箱，或保险箱过大。");
            using var stream = entries[0].Open();
            var bytes = new byte[checked((int)entries[0].Length)]; stream.ReadExactly(bytes);
            ImportBytes(bytes, password, recovery);
        }
        else ImportBytes(ReadBounded(source), password, recovery);
    }
    private void ImportBytes(byte[] bytes, string password, bool recovery)
    {
        if (Exists || IsUnlocked) throw new InvalidOperationException("此设备已有保险箱；请先导出保管，不支持覆盖或合并。");
        var envelope = JsonSerializer.Deserialize<Envelope>(bytes, NoteStore.JsonOptions) ?? throw new InvalidDataException("保险箱格式无效。");
        ValidateEnvelope(envelope);
        var derived = recovery ? Convert.FromHexString(password.Replace("-", "").Replace(" ", "")) : Derive(password, envelope.Salt);
        byte[]? key = null; byte[]? plain = null;
        try
        {
            key = Unseal(recovery ? envelope.RecoveryKey : envelope.PasswordKey, derived, Aad(envelope, recovery ? "recovery" : "password"));
            plain = Unseal(envelope.Content, key, Aad(envelope, "content"));
            Validate(JsonSerializer.Deserialize<Payload>(plain, NoteStore.JsonOptions) ?? throw new InvalidDataException("保险箱为空。"));
            WorkspaceFiles.WriteAtomic(_root, FilePath, bytes);
        }
        catch (CryptographicException ex) { throw new CryptographicException("主密码或恢复密钥不正确，或保险箱文件已损坏。", ex); }
        finally
        {
            CryptographicOperations.ZeroMemory(derived);
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            if (plain is not null) CryptographicOperations.ZeroMemory(plain);
        }
    }
    public void Lock()
    {
        if (_key is not null) CryptographicOperations.ZeroMemory(_key);
        if (_payload is not null) foreach (var bytes in _payload.Assets.Values) CryptographicOperations.ZeroMemory(bytes);
        _key = null; _payload = null; _envelope = null;
    }
    public void Dispose() => Lock();
    private Payload Require() => _payload ?? throw new InvalidOperationException("保险箱已锁定。");
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, NoteStore.JsonOptions), NoteStore.JsonOptions)!;
    private static void ValidatePassword(string password)
    {
        if (password.Length is < 12 or > 1024) throw new ArgumentException("主密码至少 12 个字符，最长 1024 个字符。建议使用独立的长口令。");
    }
    private static byte[] Derive(string password, byte[] salt)
    {
        if (password.Length > 1024) throw new ArgumentException("密码过长。");
        var bytes = Encoding.UTF8.GetBytes(password);
        try
        {
            using var argon = new Argon2id(bytes) { Salt = salt, MemorySize = 65536, Iterations = 3, DegreeOfParallelism = 1 };
            return argon.GetBytes(32);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    // Salt is authenticated by the password wrap; exclude it here so recovery and content
    // stay valid when rewrapping the data key with a new master password.
    private static byte[] Aad(Envelope e, string purpose) => Encoding.UTF8.GetBytes($"StickyVault|1|{e.Id:D}|Argon2id-64MiB-3-1|AES256GCM|{purpose}");
    private static Box Seal(byte[] plain, byte[] key, byte[] aad)
    {
        var box = new Box { Nonce = RandomNumberGenerator.GetBytes(12), Tag = new byte[16], Ciphertext = new byte[plain.Length] };
        using var aes = new AesGcm(key, 16); aes.Encrypt(box.Nonce, plain, box.Ciphertext, box.Tag, aad); return box;
    }
    private static byte[] Unseal(Box box, byte[] key, byte[] aad)
    {
        var plain = new byte[box.Ciphertext.Length];
        try { using var aes = new AesGcm(key, 16); aes.Decrypt(box.Nonce, box.Ciphertext, box.Tag, plain, aad); return plain; }
        catch { CryptographicOperations.ZeroMemory(plain); throw; }
    }
    private static byte[] ReadBounded(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > 96 * 1024 * 1024) throw new InvalidDataException("保险箱文件过大。");
        var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes); return bytes;
    }
    private static Envelope ReadEnvelope(string path)
    {
        var envelope = JsonSerializer.Deserialize<Envelope>(ReadBounded(path), NoteStore.JsonOptions) ?? throw new InvalidDataException("保险箱格式无效。");
        ValidateEnvelope(envelope); return envelope;
    }
    private static void ValidateEnvelope(Envelope e)
    {
        if (e.Format != "StickyVault" || e.Version != 1 || e.Kdf != "Argon2id-64MiB-3-1" || e.Id == Guid.Empty || e.Salt is not { Length: 16 })
            throw new InvalidDataException("不支持的保险箱格式。");
        foreach (var box in new[] { e.PasswordKey, e.RecoveryKey, e.Content })
            if (box is null || box.Nonce is not { Length: 12 } || box.Tag is not { Length: 16 } || box.Ciphertext is null || box.Ciphertext.Length > Limit)
                throw new InvalidDataException("保险箱密文格式无效。");
        if (e.PasswordKey.Ciphertext.Length != 32 || e.RecoveryKey.Ciphertext.Length != 32) throw new InvalidDataException("保险箱密钥格式无效。");
    }
    private static void Validate(Payload payload)
    {
        if (payload.Notes is null || payload.Assets is null || payload.History is null || payload.SourceBackups is null || payload.Notes.Select(n => n.Id).Distinct().Count() != payload.Notes.Count)
            throw new InvalidDataException("保险箱内容无效。");
        foreach (var (id, bytes) in payload.Assets)
            if (bytes is null || Convert.ToHexStringLower(SHA256.HashData(bytes)) + ".png" != id) throw new InvalidDataException("保险箱图片校验失败。");
        foreach (var note in payload.Notes.Concat(payload.History.Values.SelectMany(v => v)))
        {
            _ = NoteStore.ParseNote(JsonSerializer.SerializeToUtf8Bytes(note, NoteStore.JsonOptions), note.Id);
            if (ImageIds(note.Blocks).Any(id => !payload.Assets.ContainsKey(id))) throw new InvalidDataException("保险箱缺少图片。");
        }
    }
    public static IEnumerable<string> ImageIds(IEnumerable<NoteBlock> blocks)
    {
        foreach (var block in blocks)
        {
            foreach (var inline in block.Inlines) if (inline.Kind == "image" && inline.AssetId is { } id) yield return id;
            foreach (var id in ImageIds(block.Children)) yield return id;
        }
    }
    public sealed class Box
    {
        public byte[] Nonce { get; set; } = [];
        public byte[] Tag { get; set; } = [];
        public byte[] Ciphertext { get; set; } = [];
    }
    public sealed class Envelope
    {
        public string Format { get; set; } = "StickyVault";
        public int Version { get; set; } = 1;
        public string Kdf { get; set; } = "Argon2id-64MiB-3-1";
        public Guid Id { get; set; }
        public byte[] Salt { get; set; } = [];
        public Box PasswordKey { get; set; } = new();
        public Box RecoveryKey { get; set; } = new();
        public Box Content { get; set; } = new();
    }
    public sealed class Payload
    {
        public List<Note> Notes { get; set; } = [];
        public Dictionary<string, byte[]> Assets { get; set; } = [];
        public Dictionary<Guid, List<Note>> History { get; set; } = [];
        public Dictionary<Guid, Dictionary<string, byte[]>> SourceBackups { get; set; } = [];
    }
}
