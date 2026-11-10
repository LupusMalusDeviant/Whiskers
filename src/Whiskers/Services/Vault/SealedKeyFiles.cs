using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Whiskers.Configuration;

namespace Whiskers.Services.Vault;

/// <summary>
/// Contract 0015 Part A (INV-52): private keys Whiskers keeps in its data directory — the SSH keys in <c>ssh-keys/</c>
/// and the mTLS client keys in <c>mtls/</c> — are stored sealed by the vault. A sealed file keeps its name and place;
/// its content is one line <c>f1:&lt;base64&gt;</c> (<see cref="ProtectedFields.Prefix"/>), made by
/// <see cref="IVaultService.ProtectField"/> and bound to a purpose that names the file's place
/// (<see cref="SealedKeyPurposes"/>), so a file moved to another server or slot does not open there.
/// </summary>
public interface ISealedKeyFiles
{
    /// <summary>Why a key cannot be sealed now (the vault is not configured or locked), or null when it can.</summary>
    string? RefusalReason { get; }

    /// <summary>Seals <paramref name="pem"/> for <paramref name="purpose"/> and writes it atomically (tmp + rename, 0600).
    /// Refuses (<see cref="SealedKeyException"/>, nothing written) when the vault is not usable (D4).</summary>
    void Write(string path, string purpose, ReadOnlySpan<char> pem);

    /// <summary>The key's PEM in memory. A sealed file is opened with its purpose; a plaintext PEM inside the data
    /// directory is read and reported (until the start migration seals it); a plaintext PEM outside the data directory
    /// is an operator-managed key (D6). Throws <see cref="SealedKeyException"/> with a reason for anything else.</summary>
    SealedKeyMaterial Read(string path, string purpose);
}

/// <summary>Where a key in memory came from.</summary>
public enum KeySource
{
    /// <summary>A sealed file, opened by the vault.</summary>
    Sealed,

    /// <summary>A plaintext key inside the data directory, from before contract 0015 (sealed by the next start
    /// migration while the vault is usable).</summary>
    PlaintextInDataDirectory,

    /// <summary>A plaintext key outside the data directory: the operator manages it, Whiskers never moves or seals it (D6).</summary>
    OperatorManaged,
}

/// <summary>A key that cannot be sealed, read or opened. The message names the reason and never any part of a key.</summary>
public sealed class SealedKeyException(string message) : InvalidOperationException(message);

/// <summary>Plaintext key in a pinned buffer, zeroed on <see cref="Dispose"/>.</summary>
public sealed class SealedKeyMaterial : IDisposable
{
    private readonly char[] _buffer;
    private bool _disposed;

    internal SealedKeyMaterial(ReadOnlySpan<char> pem, KeySource source)
    {
        // Pinned: the GC never copies the key to another place in memory that nobody zeroes.
        _buffer = GC.AllocateArray<char>(pem.Length, pinned: true);
        pem.CopyTo(_buffer);
        Source = source;
    }

    public ReadOnlySpan<char> Pem
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _buffer;
        }
    }

    public KeySource Source { get; }

    /// <summary>Tests only: whether the buffer holds nothing but zeros.</summary>
    internal bool IsZeroed => _buffer.All(c => c == '\0');

    public void Dispose()
    {
        if (_disposed) return;
        CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(_buffer.AsSpan()));
        _disposed = true;
    }
}

/// <summary>The purposes a sealed key file is bound to (contract 0015, "Sealed key files").</summary>
public static class SealedKeyPurposes
{
    /// <summary>An SSH key: <c>ssh-key:{serverId}/{fileName}</c>.</summary>
    public static string SshKey(string serverId, string fileName) => $"ssh-key:{serverId}/{fileName}";

    /// <summary>An mTLS client key (per host or shared): <c>mtls-client-key:{path relative to MtlsDir, '/' separated}</c>.
    /// A path outside the mTLS directory is named by its full path (an operator-managed key is never sealed).</summary>
    public static string MtlsClientKey(DataPathOptions paths, string keyPath)
    {
        var mtls = Path.GetFullPath(paths.MtlsDir);
        var full = Path.GetFullPath(keyPath);
        var inside = full.StartsWith(mtls + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        var name = inside ? Path.GetRelativePath(mtls, full) : full;
        return "mtls-client-key:" + name.Replace('\\', '/');
    }
}

/// <inheritdoc />
public sealed class SealedKeyFiles(IVaultService vault, DataPathOptions paths) : ISealedKeyFiles
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Said when a sealed key cannot be opened because the vault is locked (D10): the cause, not a symptom.</summary>
    public const string VaultLockedReason = "client keys sealed, vault locked: VAULT_KEY does not open this vault — set the right key and restart";

    /// <summary>Said when a sealed key cannot be opened because no VAULT_KEY is configured.</summary>
    public const string VaultOffReason = "the key is sealed by the vault, but no VAULT_KEY is set — set VAULT_KEY_FILE and restart";

    public string? RefusalReason => vault.KeyState switch
    {
        VaultKeyState.Verified => null,
        VaultKeyState.Wrong => "The vault is locked (VAULT_KEY does not open it), so a key cannot be stored sealed. Set the right VAULT_KEY and restart.",
        _ => "Keys are stored sealed by the vault: set VAULT_KEY_FILE (or VAULT_KEY) and restart before storing a key.",
    };

    public void Write(string path, string purpose, ReadOnlySpan<char> pem)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        if (RefusalReason is { } refused) throw new SealedKeyException(refused);
        var plain = new string(pem);
        var sealedText = vault.ProtectField(purpose, plain)
                         ?? throw new SealedKeyException("The vault is not usable; nothing was written.");
        // Read back before anything is written: a value that would not open again never replaces a key.
        if (!vault.TryUnprotectField(purpose, sealedText, out var back) || back != plain)
            throw new SealedKeyException("A sealed key did not read back; nothing was written.");
        WriteAtomic(path, sealedText + "\n");
    }

    public SealedKeyMaterial Read(string path, string purpose)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string text;
        try
        {
            text = File.ReadAllText(path, Utf8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            throw new SealedKeyException($"The key file {Path.GetFileName(path)} cannot be read ({ex.GetType().Name}).");
        }

        var trimmed = text.Trim();
        if (ProtectedFields.IsProtected(trimmed))
        {
            if (vault.KeyState == VaultKeyState.Wrong) throw new SealedKeyException(VaultLockedReason);
            if (!vault.IsEnabled) throw new SealedKeyException(VaultOffReason);
            if (!vault.TryUnprotectField(purpose, trimmed, out var plain))
                throw new SealedKeyException($"The sealed key file {Path.GetFileName(path)} does not open here: it was changed, "
                                             + "moved from another server or slot, or sealed with another vault key.");
            return new SealedKeyMaterial(plain, KeySource.Sealed);
        }
        return new SealedKeyMaterial(text, IsInDataDirectory(path) ? KeySource.PlaintextInDataDirectory : KeySource.OperatorManaged);
    }

    /// <summary>Whether <paramref name="path"/> lies inside the data directory.</summary>
    public bool IsInDataDirectory(string path) => IsInside(paths.RootDir, path);

    internal static bool IsInside(string root, string path)
    {
        var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return Path.GetFullPath(path).StartsWith(dir + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>Whether the file's content is a sealed key (<c>f1:</c>).</summary>
    public static bool IsSealedFile(string path)
    {
        try
        {
            using var reader = new StreamReader(path, Utf8);
            var buffer = new char[ProtectedFields.Prefix.Length + 8];
            var n = reader.ReadBlock(buffer, 0, buffer.Length);
            return ProtectedFields.IsProtected(new string(buffer, 0, n).TrimStart());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>Writes <paramref name="content"/> to <c>{path}.tmp</c>, created owner-only (0600, Unix), and renames it
    /// over <paramref name="path"/>: a crash leaves the old file or the new one, never half of one.</summary>
    internal static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.Delete(tmp);   // a leftover of a crash: the create mode applies only to a file this call creates
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(tmp, options))
        {
            stream.Write(Utf8.GetBytes(content));
            stream.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }
}
