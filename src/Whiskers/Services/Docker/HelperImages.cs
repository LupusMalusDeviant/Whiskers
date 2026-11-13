namespace Whiskers.Services.Docker;

/// <summary>
/// The images Whiskers starts as its own helpers on managed hosts. Each runs with the host's root (the host-shell helper
/// is privileged and enters PID 1; the volume helpers mount volumes read-write), so each is pinned by digest: a moved
/// tag, a compromised registry or a different image tagged the same way on the host never runs in their place
/// (Fable review H5). Re-pin with <c>docker buildx imagetools inspect alpine:3.22</c> (the index digest).
/// </summary>
public static class HelperImages
{
    public const string AlpineRepository = "alpine";
    public const string AlpineDigest = "sha256:14358309a308569c32bdc37e2e0e9694be33a9d99e68afb0f5ff33cc1f695dce";

    /// <summary>The reference a container is created from: tag for the reader, digest for Docker.</summary>
    public const string Alpine = AlpineRepository + ":3.22@" + AlpineDigest;
}
