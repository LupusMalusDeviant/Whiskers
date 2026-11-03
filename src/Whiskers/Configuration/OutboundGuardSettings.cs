namespace Whiskers.Configuration;

/// <summary>R1-54 (INV-60): the exceptions to the outbound guard of the notification channels, kept in
/// <c>app-settings.json</c> next to the channels' own settings. Edited by an Admin on the Settings page only — every
/// change is a gated, audited <c>config.save</c>. The denied ranges themselves are fixed in code
/// (<c>OutboundAddressPolicy</c>) and are not configured here.</summary>
public class OutboundGuardSettings
{
    public const string SectionName = "OutboundGuard";

    public List<OutboundExceptionEntry> Exceptions { get; set; } = [];
}

/// <summary>One exception: an IP literal plus port (<see cref="Addresses"/> empty), or a name plus port plus the
/// exact address(es) it may resolve to. Never a range or a wildcard. A connection is let through only when the
/// address it actually connects to is the entry's.</summary>
public class OutboundExceptionEntry
{
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public List<string> Addresses { get; set; } = [];
}
