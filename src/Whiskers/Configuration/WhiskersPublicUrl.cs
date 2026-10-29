namespace Whiskers.Configuration;

/// <summary>
/// The address people open Whiskers at, as the admin configured it (<c>Whiskers:PublicUrl</c>, environment variable
/// <c>Whiskers__PublicUrl</c>; R1-15). Only this configured value builds absolute links for channels outside the app
/// (e-mail, chat) — never the host of an incoming request, which a caller controls. Unset or invalid: no absolute
/// links, the notification names the page instead.
/// </summary>
public sealed class WhiskersPublicUrl
{
    public const string ConfigKey = "Whiskers:PublicUrl";

    /// <summary>The base address, ending in <c>/</c>; <c>null</c> when none (valid) is configured.</summary>
    public Uri? Base { get; }

    public WhiskersPublicUrl(Uri? @base) => Base = @base;

    public static WhiskersPublicUrl None { get; } = new(null);

    public static WhiskersPublicUrl From(IConfiguration configuration) => new(Parse(configuration[ConfigKey]));

    /// <summary>An absolute http(s) address without credentials, query or fragment; anything else is ignored.</summary>
    public static Uri? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http")
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return null;
        var text = uri.GetLeftPart(UriPartial.Path);
        return new Uri(text.EndsWith('/') ? text : text + "/");
    }

    /// <summary>The absolute address of an in-app link (relative, as <c>NotificationFormatter.LinkFor</c> gives it).</summary>
    public string? Link(string relative) => Base is null ? null : new Uri(Base, relative).AbsoluteUri;
}
