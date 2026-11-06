using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Whiskers.Services.Onboarding;

namespace Whiskers.Services.Certificates;

/// <summary>
/// The client identity Whiskers presents to one managed host (R1-44, contract 0007 D1/D2): the common name
/// <c>whiskers-client-{slug}-g{generation}</c>, which that host's ghostunnel admits with <c>--allow-cn</c>.
///
/// <para>The slug comes from the server's <b>id</b>, not its display name (contract amendment B3): a display name is
/// lossy under slugging and changes on a rename, so two hosts could end up sharing a name. It is fixed at the first issue
/// and kept in the store's <c>identity</c> file; afterwards the common name is only ever read from there, never derived
/// again. The generation is part of the name, so a rotation (generation + 1, then narrowing the host to the new name)
/// makes every older certificate useless at that host without a CRL (D2).</para>
/// </summary>
public sealed partial record MtlsClientIdentity
{
    /// <summary>The prefix every per-host client common name carries.</summary>
    public const string Prefix = "whiskers-client-";

    /// <summary>A DNS label is at most 63 characters, and ghostunnel's flag and step-ca's file name take it as one.</summary>
    public const int MaxCommonNameLength = 63;

    public string ServerId { get; }
    public string Slug { get; }
    public int Generation { get; }

    /// <summary>"whiskers-client-{slug}": what the store keeps; the generation is appended (contract amendment 3, m-5).</summary>
    public string Stem => Prefix + Slug;

    /// <summary>"whiskers-client-{slug}-g{generation}".</summary>
    public string CommonName { get; }

    public MtlsClientIdentity(string serverId, string slug, int generation)
    {
        if (string.IsNullOrWhiteSpace(serverId)) throw new ArgumentException("A server id is required.", nameof(serverId));
        if (generation < 1) throw new ArgumentOutOfRangeException(nameof(generation), "Generations start at 1.");
        var cn = $"{Prefix}{slug}-g{generation}";
        if (!IsValidCommonName(cn))
            throw new ArgumentException(
                $"'{cn}' is not a usable client name (one lowercase DNS label of at most {MaxCommonNameLength} characters).", nameof(slug));
        ServerId = serverId;
        Slug = slug;
        Generation = generation;
        CommonName = cn;
    }

    /// <summary>The longest slug that keeps the name within 63 characters up to generation 9999.</summary>
    public const int MaxSlugLength = MaxCommonNameLength - 16 - 2 - 4;

    /// <summary>The identity a server gets at its first issue (no other server holds a slug yet). Afterwards read it
    /// from the store.</summary>
    public static MtlsClientIdentity For(Models.ServerConfig server, int generation) => For(server, generation, _ => false);

    /// <summary>The identity a server gets at its first issue: <see cref="SlugFor"/> of its id.</summary>
    public static MtlsClientIdentity For(Models.ServerConfig server, int generation, Func<string, bool> slugTaken)
    {
        ArgumentNullException.ThrowIfNull(server);
        return new MtlsClientIdentity(server.Id, SlugFor(server.Id, slugTaken), generation);
    }

    /// <summary>
    /// The deterministic slug of a server id (contract amendment 2, MI-10): <c>Slug(id)</c>; when that is longer than
    /// <see cref="MaxSlugLength"/> or another server already holds it, <c>Slug(id)</c> truncated, a hyphen and the first
    /// six hex characters of SHA-256(id). Throws when even that is taken.
    /// </summary>
    public static string SlugFor(string serverId, Func<string, bool> slugTaken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        var plain = OnboardingCommands.Slug(serverId);
        if (plain.Length <= MaxSlugLength && !slugTaken(plain)) return plain;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serverId)))[..6].ToLowerInvariant();
        var head = plain[..Math.Min(plain.Length, MaxSlugLength - 7)].TrimEnd('-');
        var hashed = head.Length == 0 ? hash : $"{head}-{hash}";
        if (slugTaken(hashed))
            throw new InvalidOperationException($"The client name of server '{serverId}' collides with another server's even with its hash suffix.");
        return hashed;
    }

    /// <summary>The identity of <paramref name="generation"/> under a stored stem (<c>whiskers-client-{slug}</c>).</summary>
    public static MtlsClientIdentity FromStem(string serverId, string stem, int generation) =>
        stem is not null && stem.StartsWith(Prefix, StringComparison.Ordinal)
            ? new MtlsClientIdentity(serverId, stem[Prefix.Length..], generation)
            : throw new ArgumentException($"'{stem}' is not a client name stem.", nameof(stem));

    /// <summary>The next generation of this identity (same slug, generation + 1).</summary>
    public MtlsClientIdentity Next() => new(ServerId, Slug, Generation + 1);

    /// <summary>True for a value that may appear in <c>--allow-cn=</c> on a host: one lowercase DNS label (the same
    /// character set <see cref="MtlsIdentity"/> allows), at most 63 characters. Every name that reaches a shell line on a
    /// host passes through this check.</summary>
    public static bool IsValidCommonName(string? value) =>
        value is { Length: > 0 and <= MaxCommonNameLength } && LabelRegex().IsMatch(value);

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex LabelRegex();
}
