namespace Whiskers.Models;

/// <summary>How one container's Docker log driver is configured, and where its log file lives
/// (Plan-0007 WP3).</summary>
/// <param name="Driver">The log driver, e.g. <c>json-file</c>. Only the file-based drivers can grow without
/// bound on the host disk; the others ship their lines elsewhere and are somebody else's problem.</param>
/// <param name="MaxSize">The configured <c>max-size</c>, or null when none is set — the case that made two
/// containers grow to 822 MB in a fortnight.</param>
/// <param name="MaxFile">The configured <c>max-file</c>, or null.</param>
/// <param name="LogPath">The file on the host, when Docker reports one. Reading its size needs host access,
/// which is why the size is a separate, optional step.</param>
public sealed record ContainerLogConfiguration(string Driver, string? MaxSize, string? MaxFile, string? LogPath)
{
    /// <summary>A rotation limit is anything that stops the file growing forever. <c>max-size</c> is the one
    /// that matters: <c>max-file</c> alone caps the number of files, not the size of each. Review 1.0.0: <c>-1</c> is
    /// json-file's own value for "unlimited" and is no limit.</summary>
    public bool RotationConfigured => !string.IsNullOrWhiteSpace(MaxSize) && MaxSize.Trim() != "-1";

    /// <summary>Whether this driver writes a file on the host that can fill the disk. Drivers that forward
    /// elsewhere (syslog, journald, a remote collector) are outside what this inventory can or should judge.</summary>
    public bool WritesToHostDisk => Driver is "json-file" or "local";
}
