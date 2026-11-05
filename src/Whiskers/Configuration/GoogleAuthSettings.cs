namespace Whiskers.Configuration;

public class GoogleAuthSettings
{
    public const string SectionName = "GoogleAuth";
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public List<string> AllowedEmails { get; set; } = new();

    /// <summary>R1-27 (contract 0003, decision 3 b): whether Google's <c>email_verified=true</c> binds a new subject to a
    /// configured address at its first sign-in. On by default.</summary>
    public bool TrustEmailVerified { get; set; } = true;
}
