using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Whiskers.Services.Backup;

/// <summary>
/// Streaming authenticated encryption for self-backup archives (framed AES-256-GCM). The key is derived
/// from the <c>VAULT_KEY</c> passphrase with PBKDF2 (600k, SHA-256) over a per-archive random salt that is
/// stored in the plaintext header — the key itself is NEVER written to the archive (CLAUDE.md guardrail:
/// no key next to the ciphertext). The per-archive salt also keeps this key cryptographically distinct from
/// the vault master key even though both derive from the same passphrase.
///
/// On-disk layout:
///   MAGIC(8) ‖ Version(4 BE) ‖ Salt(16) ‖ NoncePrefix(8) ‖ Frame*
///   Frame = Flag(1: 0=more, 1=final) ‖ CipherLen(4 BE) ‖ Ciphertext(CipherLen) ‖ Tag(16)
///   Nonce_i = NoncePrefix(8) ‖ i(4 BE)      AAD_i = Version(4 BE) ‖ i(4 BE) ‖ Flag(1)
///
/// The authenticated per-frame Flag plus the running frame index make tampering, reordering AND truncation
/// detectable: dropping the final frame ends the stream without ever decrypting a Flag=1 frame, so the
/// reader throws instead of silently returning a short plaintext.
/// </summary>
public static class BackupArchiveCipher
{
    /// <summary>Magic bytes identifying a Whiskers encrypted backup ("WHBKENC1").</summary>
    public static readonly byte[] Magic = "WHBKENC1"u8.ToArray();

    private const int Version = 1;
    private const int SaltSize = 16;
    private const int NoncePrefixSize = 8;
    private const int NonceSize = 12;           // 8-byte per-archive prefix + 4-byte big-endian frame counter
    private const int TagSize = 16;
    private const int FrameSize = 1 << 20;      // 1 MiB plaintext frames
    private const int Pbkdf2Iterations = 600_000;

    /// <summary>Header length that <see cref="StartsWithMagic"/> needs to sniff (just the magic).</summary>
    public const int MagicLength = 8;

    /// <summary>True if the given bytes begin with the encryption magic.</summary>
    public static bool StartsWithMagic(ReadOnlySpan<byte> header)
        => header.Length >= MagicLength && header[..MagicLength].SequenceEqual(Magic);

    /// <summary>Derives the 32-byte archive key from the passphrase and the per-archive salt (PBKDF2-SHA256).</summary>
    public static byte[] DeriveKey(string passphrase, byte[] salt)
        => Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);

    /// <summary>Encrypts <paramref name="source"/> into <paramref name="destination"/>. Neither stream is disposed.</summary>
    public static async Task EncryptAsync(Stream source, Stream destination, string passphrase, CancellationToken ct = default)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var noncePrefix = RandomNumberGenerator.GetBytes(NoncePrefixSize);
        var key = DeriveKey(passphrase, salt);

        // Header
        await destination.WriteAsync(Magic, ct);
        await destination.WriteAsync(BeInt(Version), ct);
        await destination.WriteAsync(salt, ct);
        await destination.WriteAsync(noncePrefix, ct);

        using var gcm = new AesGcm(key, TagSize);
        var plain = new byte[FrameSize];
        var nonce = new byte[NonceSize];
        noncePrefix.CopyTo(nonce, 0);
        var tag = new byte[TagSize];
        uint index = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int n = await source.ReadAtLeastAsync(plain, FrameSize, throwOnEndOfStream: false, ct);
            byte flag = (byte)(n < FrameSize ? 1 : 0);   // a short read (incl. 0) is the final frame

            var cipher = new byte[n];
            BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(NoncePrefixSize), index);
            gcm.Encrypt(nonce, plain.AsSpan(0, n), cipher, tag, Aad(index, flag));

            var frameHeader = new byte[5];
            frameHeader[0] = flag;
            BinaryPrimitives.WriteInt32BigEndian(frameHeader.AsSpan(1), n);
            await destination.WriteAsync(frameHeader, ct);
            if (n > 0) await destination.WriteAsync(cipher, ct);
            await destination.WriteAsync(tag, ct);

            if (flag == 1) break;
            index++;
        }
    }

    /// <summary>Decrypts <paramref name="source"/> into <paramref name="destination"/>. Throws
    /// <see cref="CryptographicException"/> on a wrong key/tamper and <see cref="EndOfStreamException"/> on a
    /// truncated stream. Neither stream is disposed.</summary>
    public static async Task DecryptAsync(Stream source, Stream destination, string passphrase, CancellationToken ct = default)
    {
        var header = new byte[MagicLength + 4 + SaltSize + NoncePrefixSize];
        await source.ReadExactlyAsync(header, ct);
        if (!StartsWithMagic(header))
            throw new InvalidDataException("Not a Whiskers encrypted backup (bad magic).");
        int version = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(MagicLength));
        if (version != Version)
            throw new InvalidDataException($"Unsupported backup encryption version {version}.");
        var salt = header.AsSpan(MagicLength + 4, SaltSize).ToArray();
        var noncePrefix = header.AsSpan(MagicLength + 4 + SaltSize, NoncePrefixSize).ToArray();
        var key = DeriveKey(passphrase, salt);

        using var gcm = new AesGcm(key, TagSize);
        var nonce = new byte[NonceSize];
        noncePrefix.CopyTo(nonce, 0);
        var tag = new byte[TagSize];
        var frameHeader = new byte[5];
        uint index = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await source.ReadExactlyAsync(frameHeader, ct);   // EndOfStream here == truncated (missing final frame)
            byte flag = frameHeader[0];
            if (flag > 1) throw new CryptographicException("Corrupt encrypted backup (bad frame flag).");
            int n = BinaryPrimitives.ReadInt32BigEndian(frameHeader.AsSpan(1));
            if (n < 0 || n > FrameSize) throw new CryptographicException("Corrupt encrypted backup (bad frame length).");

            var cipher = new byte[n];
            if (n > 0) await source.ReadExactlyAsync(cipher, ct);
            await source.ReadExactlyAsync(tag, ct);

            var plain = new byte[n];
            BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(NoncePrefixSize), index);
            gcm.Decrypt(nonce, cipher, tag, plain, Aad(index, flag));   // throws on tamper/wrong key
            if (n > 0) await destination.WriteAsync(plain, ct);

            if (flag == 1) break;
            index++;
        }
    }

    // AAD binds the format version, the frame index and the final-flag into every tag, so a reordered,
    // downgraded or flag-flipped frame fails authentication.
    private static byte[] Aad(uint index, byte flag)
    {
        var aad = new byte[9];
        BinaryPrimitives.WriteInt32BigEndian(aad, Version);
        BinaryPrimitives.WriteUInt32BigEndian(aad.AsSpan(4), index);
        aad[8] = flag;
        return aad;
    }

    private static byte[] BeInt(int value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, value);
        return b;
    }
}
