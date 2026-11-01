using System.Security.Cryptography;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;

namespace Whiskers.Services.Vault;

/// <summary>
/// R1-31 (INV-52): the data-protection key ring (sign-in cookies, antiforgery tokens) is kept in its directory with each
/// key's secret encrypted by a key derived from the vault's — through the documented extension points of ASP.NET Core
/// Data Protection (<see cref="IXmlEncryptor"/>, <see cref="IXmlDecryptor"/>, <see cref="IXmlRepository"/>); the
/// authentication setup itself is unchanged. With the vault usable a new key is written encrypted, and a key file from
/// before is encrypted in place at start (<see cref="VaultKeyRingRepository.ProtectExistingKeys"/>), so nobody is
/// signed out by the upgrade. Without VAULT_KEY the keys are written as before (the readiness check says so). With a
/// VAULT_KEY that does not open the vault, the encrypted keys cannot be read: a new key is created and kept in memory
/// only — sign-in works, nothing is written in plaintext, and sessions end at the next restart.
/// </summary>
public static class VaultKeyRing
{
    /// <summary>The Data Protection XML namespace (its <c>encryptedSecret</c> and <c>requiresEncryption</c> names).</summary>
    internal static readonly XNamespace DataProtectionNs = "http://schemas.asp.net/2015/03/dataProtection";
    internal static readonly XName EncryptedSecret = DataProtectionNs + "encryptedSecret";
    internal static readonly XName RequiresEncryption = DataProtectionNs + "requiresEncryption";
    internal const string DecryptorType = "decryptorType";

    internal static readonly XName VaultProtected = "vaultProtected";
    internal static readonly XName MemoryOnly = "memoryOnly";
    internal static readonly XName Unprotected = "unprotected";

    /// <summary>Keeps the key ring in <paramref name="directory"/>, encrypted with the vault (replaces
    /// <c>PersistKeysToFileSystem</c>).</summary>
    public static IServiceCollection AddVaultProtectedKeyRing(this IServiceCollection services, string directory)
    {
        services.AddSingleton(sp => new VaultKeyRingRepository(directory, sp.GetRequiredService<IVaultService>(),
            sp.GetService<ILoggerFactory>()?.CreateLogger<VaultKeyRingRepository>()
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<VaultKeyRingRepository>.Instance));
        services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(sp => new ConfigureOptions<KeyManagementOptions>(o =>
        {
            o.XmlRepository = sp.GetRequiredService<VaultKeyRingRepository>();
            o.XmlEncryptor = new VaultXmlEncryptor(sp.GetRequiredService<IVaultService>(), sp.GetService<IConfiguration>());
        }));
        return services;
    }

    /// <summary>The placeholder Data Protection reads back: our decryptor named, our encrypted element inside.</summary>
    internal static XElement Placeholder(Type decryptor, XElement encrypted)
        => new(EncryptedSecret, new XAttribute(DecryptorType, decryptor.AssemblyQualifiedName!), encrypted);
}

/// <summary>Encrypts a key's secret with the vault. When the vault is not usable it encrypts nothing: the element is
/// marked for the repository — kept in memory only when a VAULT_KEY is configured, written as before when none is.</summary>
public sealed class VaultXmlEncryptor(IVaultService vault, IConfiguration? configuration) : IXmlEncryptor
{
    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        ArgumentNullException.ThrowIfNull(plaintextElement);
        if (vault.ProtectField(ProtectedFields.DataProtectionKey, plaintextElement.ToString(SaveOptions.DisableFormatting)) is { } stored)
            return new EncryptedXmlInfo(new XElement(VaultKeyRing.VaultProtected, stored), typeof(VaultXmlDecryptor));
        var configured = vault.KeyState != VaultKeyState.NotConfigured
                         || (configuration is not null && SecretSource.VaultKey(configuration) is not null);
        return new EncryptedXmlInfo(
            new XElement(configured ? VaultKeyRing.MemoryOnly : VaultKeyRing.Unprotected, new XElement(plaintextElement)),
            typeof(PassThroughXmlDecryptor));
    }
}

/// <summary>Opens a key's secret encrypted by <see cref="VaultXmlEncryptor"/>. Created by Data Protection's activator,
/// which hands it the service provider.</summary>
public sealed class VaultXmlDecryptor(IServiceProvider services) : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement)
    {
        ArgumentNullException.ThrowIfNull(encryptedElement);
        var vault = services.GetRequiredService<IVaultService>();
        if (encryptedElement.Name != VaultKeyRing.VaultProtected
            || !vault.TryUnprotectField(ProtectedFields.DataProtectionKey, encryptedElement.Value, out var plain))
            throw new CryptographicException("The data-protection key cannot be opened: the vault is not usable, or the key is another.");
        return XElement.Parse(plain);
    }
}

/// <summary>Hands back a key kept in memory (never written; see <see cref="VaultXmlEncryptor"/>).</summary>
public sealed class PassThroughXmlDecryptor : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement)
    {
        ArgumentNullException.ThrowIfNull(encryptedElement);
        return new XElement(encryptedElement.Elements().Single());
    }
}

/// <summary>
/// The key ring's files (<c>key-*.xml</c> in the keys directory), in Data Protection's own format. A key the encryptor
/// could not encrypt is kept in memory only when a VAULT_KEY is configured, and written in plaintext as before R1-31
/// when none is.
/// </summary>
public sealed class VaultKeyRingRepository(string directory, IVaultService vault, ILogger<VaultKeyRingRepository> logger) : IXmlRepository
{
    private readonly object _gate = new();
    private readonly List<XElement> _memory = [];
    private bool _warned;

    public string Directory => directory;

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        lock (_gate)
        {
            var elements = new List<XElement>();
            if (System.IO.Directory.Exists(directory))
                foreach (var file in System.IO.Directory.EnumerateFiles(directory, "*.xml"))
                    elements.Add(XElement.Load(file));
            elements.AddRange(_memory.Select(e => new XElement(e)));
            return elements;
        }
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);
        lock (_gate)
        {
            var element2 = new XElement(element);
            var marked = element2.Descendants(VaultKeyRing.EncryptedSecret)
                .Where(p => (string?)p.Attribute(VaultKeyRing.DecryptorType) == typeof(PassThroughXmlDecryptor).AssemblyQualifiedName)
                .ToList();
            if (marked.Any(p => p.Elements().Single().Name == VaultKeyRing.MemoryOnly))
            {
                _memory.Add(element2);
                if (!_warned)
                {
                    _warned = true;
                    logger.LogWarning("The vault is not usable: a new data-protection key is kept in memory only — sign-ins end at the next restart");
                }
                return;
            }
            // No VAULT_KEY: written as before R1-31, the plaintext key in place of the placeholder.
            foreach (var placeholder in marked)
                placeholder.ReplaceWith(new XElement(placeholder.Elements().Single().Elements().Single()));
            Write(Path.Combine(directory, FileName(friendlyName)), element2);
        }
    }

    /// <summary>
    /// Encrypts, in place, the secret of every key written in plaintext before (or while no VAULT_KEY was set): each
    /// file is replaced only once its encrypted form reads back. Returns how many files were encrypted. Run at start,
    /// before the key ring is first read.
    /// </summary>
    public int ProtectExistingKeys()
    {
        if (!vault.IsEnabled || !System.IO.Directory.Exists(directory)) return 0;
        var count = 0;
        lock (_gate)
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(directory, "*.xml").ToList())
            {
                var root = XElement.Load(file);
                var plain = PlaintextSecrets(root);
                if (plain.Count == 0) continue;
                foreach (var secret in plain)
                {
                    var text = secret.ToString(SaveOptions.DisableFormatting);
                    var stored = vault.ProtectField(ProtectedFields.DataProtectionKey, text)
                        ?? throw new InvalidOperationException("The vault is not usable.");
                    if (!vault.TryUnprotectField(ProtectedFields.DataProtectionKey, stored, out var back) || back != text)
                        throw new InvalidOperationException("An encrypted data-protection key did not read back; nothing was changed.");
                    secret.ReplaceWith(VaultKeyRing.Placeholder(typeof(VaultXmlDecryptor), new XElement(VaultKeyRing.VaultProtected, stored)));
                }
                Write(file, root);
                count++;
            }
        }
        if (count > 0) logger.LogInformation("Encrypted {Count} data-protection key file(s) stored in plaintext before", count);
        return count;
    }

    /// <summary>How many key files hold a secret in plaintext (for the readiness check).</summary>
    public int CountPlaintextKeys()
    {
        if (!System.IO.Directory.Exists(directory)) return 0;
        lock (_gate)
            return System.IO.Directory.EnumerateFiles(directory, "*.xml").Count(f => PlaintextSecrets(XElement.Load(f)).Count > 0);
    }

    // Elements Data Protection marks as secret that are not inside an encrypted placeholder.
    private static List<XElement> PlaintextSecrets(XElement root)
        => root.DescendantsAndSelf()
            .Where(e => (string?)e.Attribute(VaultKeyRing.RequiresEncryption) == "true"
                        && !e.Ancestors(VaultKeyRing.EncryptedSecret).Any())
            .ToList();

    private static string FileName(string friendlyName)
    {
        var safe = !string.IsNullOrEmpty(friendlyName)
                   && friendlyName.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
        return (safe ? friendlyName : Guid.NewGuid().ToString()) + ".xml";
    }

    // A temporary file beside it, then a replace: a crash leaves the old file or the new one, never half of one.
    private static void Write(string path, XElement element)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
        {
            element.Save(stream);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }
}
