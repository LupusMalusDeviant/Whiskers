using Whiskers.Configuration;
using Whiskers.Services.Certificates;

namespace Whiskers.Services.Vault;

/// <summary>
/// Contract 0015 Part A, start migration (INV-52) [mTLS layer for the client keys, approved Q1]: every private key Whiskers
/// keeps in plaintext in its data directory — SSH keys in <c>ssh-keys/{server}/</c>, the shared <c>mtls/client.key</c>
/// and every per-host <c>mtls/hosts/{id}/{slot}/client.key</c> — is sealed in place. Per file: seal, write
/// <c>*.sealing</c>, read it back and open it, compare with the original in memory, then rename it over the original — all
/// or nothing per file; an interrupted run leaves the original and finishes at the next start. Without a usable vault
/// nothing changes and the files are counted for the readiness check (D5). Leftover <c>.tmp</c>/<c>.sealing</c> files
/// of a key are removed. Keys outside the data directory are operator-managed and never touched (D6).
/// </summary>
public sealed class KeyMaterialMigration(IVaultService vault, ISealedKeyFiles keys, DataPathOptions paths,
    ILogger<KeyMaterialMigration> logger) : IInitializable
{
    internal const string SealingSuffix = ".sealing";

    /// <summary>Right after the credential migration (55).</summary>
    public int Order => 56;

    /// <summary>Tests only: called after a file's <c>.sealing</c> copy is written, before it replaces the original.</summary>
    internal Action<string>? AfterSealingWritten { get; init; }

    public Task InitializeAsync(CancellationToken ct = default)
    {
        Run(ct);
        return Task.CompletedTask;
    }

    /// <summary>Seals what can be sealed; returns how many files were sealed and how many stay in plaintext.</summary>
    public (int Sealed, int Plaintext) Run(CancellationToken ct = default)
    {
        RemoveLeftovers(paths);
        var plaintext = Candidates(paths).Where(c => !SealedKeyFiles.IsSealedFile(c.Path)).ToList();
        if (plaintext.Count == 0) return (0, 0);
        if (!vault.IsEnabled)
        {
            logger.LogWarning("{Count} private key file(s) in the data directory stay in plaintext: the vault is {State}. "
                              + "Set the right VAULT_KEY and restart to seal them.",
                plaintext.Count, vault.KeyState == VaultKeyState.Wrong ? "locked" : "disabled");
            return (0, plaintext.Count);
        }

        var done = 0;
        foreach (var (path, purpose) in plaintext)
        {
            ct.ThrowIfCancellationRequested();
            var sealing = path + SealingSuffix;
            try
            {
                using var original = keys.Read(path, purpose);
                if (original.Source != KeySource.PlaintextInDataDirectory) continue;
                keys.Write(sealing, purpose, original.Pem);
                using (var back = keys.Read(sealing, purpose))
                {
                    if (back.Source != KeySource.Sealed || !back.Pem.SequenceEqual(original.Pem))
                        throw new SealedKeyException("The sealed key did not read back as the original.");
                }
                AfterSealingWritten?.Invoke(path);
                File.Move(sealing, path, overwrite: true);
                done++;
            }
            catch (Exception ex) when (ex is SealedKeyException or IOException or UnauthorizedAccessException)
            {
                // The original stays as it is (still usable, still counted); the next start tries again.
                TryDelete(sealing);
                logger.LogError("The key file {File} could not be sealed and stays as it is: {Reason}",
                    Path.GetRelativePath(paths.RootDir, path), ex is SealedKeyException ? ex.Message : ex.GetType().Name);
            }
        }
        if (done > 0) logger.LogInformation("Sealed {Count} private key file(s) stored in plaintext before", done);
        return (done, plaintext.Count - done);
    }

    /// <summary>Every key file Whiskers keeps in its data directory, with the purpose of its place.</summary>
    public static IReadOnlyList<(string Path, string Purpose)> Candidates(DataPathOptions paths)
    {
        var found = new List<(string, string)>();
        if (Directory.Exists(paths.SshKeysDir))
            foreach (var serverDir in Directory.EnumerateDirectories(paths.SshKeysDir).Order(StringComparer.Ordinal))
                foreach (var file in Directory.EnumerateFiles(serverDir).Order(StringComparer.Ordinal))
                {
                    if (IsLeftover(file)) continue;
                    found.Add((file, SealedKeyPurposes.SshKey(Path.GetFileName(serverDir), Path.GetFileName(file))));
                }
        foreach (var key in MtlsKeyFiles(paths))
            found.Add((key, SealedKeyPurposes.MtlsClientKey(paths, key)));
        return found;
    }

    /// <summary>How many key files in the data directory are not sealed (for the readiness check).</summary>
    public static (int Ssh, int Mtls) CountPlaintext(DataPathOptions paths)
    {
        var ssh = 0;
        var mtls = 0;
        foreach (var (path, purpose) in Candidates(paths))
        {
            if (SealedKeyFiles.IsSealedFile(path)) continue;
            if (purpose.StartsWith("ssh-key:", StringComparison.Ordinal)) ssh++;
            else mtls++;
        }
        return (ssh, mtls);
    }

    private static IEnumerable<string> MtlsKeyFiles(DataPathOptions paths)
    {
        var shared = Path.Combine(paths.MtlsDir, MtlsClientCredentialStore.KeyFile);
        if (File.Exists(shared)) yield return shared;
        var hosts = Path.Combine(paths.MtlsDir, MtlsClientCredentialStore.HostsDirName);
        if (!Directory.Exists(hosts)) yield break;
        foreach (var host in Directory.EnumerateDirectories(hosts).Order(StringComparer.Ordinal))
            foreach (var slot in Directory.EnumerateDirectories(host).Order(StringComparer.Ordinal))
            {
                var key = Path.Combine(slot, MtlsClientCredentialStore.KeyFile);
                if (File.Exists(key)) yield return key;
            }
    }

    private static bool IsLeftover(string file) =>
        file.EndsWith(".tmp", StringComparison.Ordinal) || file.EndsWith(SealingSuffix, StringComparison.Ordinal)
        || file.EndsWith(VaultKeyRotation.RotatingSuffix, StringComparison.Ordinal);

    /// <summary>Removes the <c>.tmp</c>/<c>.sealing</c> leftovers of key files (a crash between write and rename; an older
    /// build's <c>client.key.tmp</c> could hold a key in plaintext).</summary>
    internal static void RemoveLeftovers(DataPathOptions paths)
    {
        if (Directory.Exists(paths.SshKeysDir))
            foreach (var serverDir in Directory.EnumerateDirectories(paths.SshKeysDir))
                foreach (var file in Directory.EnumerateFiles(serverDir).Where(IsLeftover))
                    TryDelete(file);
        var keyNames = new[] { MtlsClientCredentialStore.KeyFile + ".tmp", MtlsClientCredentialStore.KeyFile + SealingSuffix,
            MtlsClientCredentialStore.KeyFile + SealingSuffix + ".tmp" };
        foreach (var name in keyNames) TryDelete(Path.Combine(paths.MtlsDir, name));
        var hosts = Path.Combine(paths.MtlsDir, MtlsClientCredentialStore.HostsDirName);
        if (!Directory.Exists(hosts)) return;
        foreach (var host in Directory.EnumerateDirectories(hosts))
            foreach (var slot in Directory.EnumerateDirectories(host))
                foreach (var name in keyNames)
                    TryDelete(Path.Combine(slot, name));
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
