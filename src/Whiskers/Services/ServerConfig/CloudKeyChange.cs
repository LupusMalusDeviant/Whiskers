namespace Whiskers.Services.ServerConfig;

/// <summary>
/// How a server save changes its cloud API key (R1-29). The store decides it under its write lock, like the kubeconfig
/// reference (R1-11): <see cref="Keep"/> keeps whatever the stored server has at that moment — never the value an
/// older copy carries — so neither a stale page nor onboarding can point a server back to an old key or to plaintext.
/// </summary>
public sealed record CloudKeyChange
{
    private CloudKeyChange(string? newRef, bool clear)
    {
        NewRef = newRef;
        Clears = clear;
    }

    /// <summary>The vault key that holds the new API key, or null.</summary>
    public string? NewRef { get; }

    /// <summary>Whether the key is removed (the provider was cleared).</summary>
    public bool Clears { get; }

    /// <summary>Keep the stored key.</summary>
    public static CloudKeyChange Keep { get; } = new(null, false);

    /// <summary>Remove the key.</summary>
    public static CloudKeyChange Clear { get; } = new(null, true);

    /// <summary>Use the key stored in the vault under <paramref name="vaultRef"/>.</summary>
    public static CloudKeyChange Set(string vaultRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultRef);
        return new(vaultRef, false);
    }

    /// <summary>Applies the change to <paramref name="server"/>, against <paramref name="stored"/> (the record as it is
    /// stored now). The plaintext field is only ever kept or removed, never set.</summary>
    public void ApplyTo(Models.ServerConfig server, Models.ServerConfig? stored)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (NewRef is not null)
        {
            server.CloudKeyRef = NewRef;
            server.CloudApiKey = null;
        }
        else if (Clears)
        {
            server.CloudKeyRef = null;
            server.CloudApiKey = null;
        }
        else
        {
            server.CloudKeyRef = stored?.CloudKeyRef;
            server.CloudApiKey = stored?.CloudApiKey;
        }
    }
}
