using System.Security.Cryptography;
using Whiskers.Services.Backup;

namespace Whiskers.Tests;

/// <summary>Framed AES-256-GCM stream cipher for encrypted self-backups: round-trip fidelity plus rejection of
/// a wrong key, tampering, header damage and truncation (a dropped final frame).</summary>
public class BackupArchiveCipherTests
{
    private static async Task<byte[]> Encrypt(byte[] plain, string pass)
    {
        using var src = new MemoryStream(plain);
        using var dst = new MemoryStream();
        await BackupArchiveCipher.EncryptAsync(src, dst, pass);
        return dst.ToArray();
    }

    private static async Task<byte[]> Decrypt(byte[] cipher, string pass)
    {
        using var src = new MemoryStream(cipher);
        using var dst = new MemoryStream();
        await BackupArchiveCipher.DecryptAsync(src, dst, pass);
        return dst.ToArray();
    }

    [Theory]
    [InlineData(0)]                 // empty → single empty final frame
    [InlineData(1)]
    [InlineData(5000)]
    [InlineData(1 << 20)]           // exactly one frame
    [InlineData((1 << 20) + 7)]     // one frame + a partial second
    [InlineData(3 * (1 << 20))]     // exact multiple → trailing empty final frame
    public async Task Round_trip_recovers_the_plaintext(int size)
    {
        var plain = RandomNumberGenerator.GetBytes(size);
        var cipher = await Encrypt(plain, "correct horse battery staple");
        Assert.True(BackupArchiveCipher.StartsWithMagic(cipher));
        Assert.Equal(plain, await Decrypt(cipher, "correct horse battery staple"));
    }

    [Fact]
    public async Task Wrong_key_fails_cleanly()
    {
        var cipher = await Encrypt(RandomNumberGenerator.GetBytes(5000), "key-A");
        await Assert.ThrowsAnyAsync<CryptographicException>(() => Decrypt(cipher, "key-B"));
    }

    [Fact]
    public async Task Tampered_ciphertext_is_rejected()
    {
        var cipher = await Encrypt(RandomNumberGenerator.GetBytes(5000), "k");
        cipher[^1] ^= 0xFF;   // flip a byte of the final frame's tag
        await Assert.ThrowsAnyAsync<CryptographicException>(() => Decrypt(cipher, "k"));
    }

    [Fact]
    public async Task Header_tamper_is_rejected()
    {
        var cipher = await Encrypt(RandomNumberGenerator.GetBytes(100), "k");
        cipher[0] ^= 0xFF;   // break the magic
        await Assert.ThrowsAsync<InvalidDataException>(() => Decrypt(cipher, "k"));
    }

    [Fact]
    public async Task Truncated_stream_is_rejected()
    {
        // Multiple frames so a half-cut definitely drops the authenticated final frame.
        var cipher = await Encrypt(RandomNumberGenerator.GetBytes(3 * (1 << 20)), "k");
        var truncated = cipher[..(cipher.Length / 2)];
        await Assert.ThrowsAnyAsync<Exception>(() => Decrypt(truncated, "k"));
    }
}
