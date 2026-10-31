using System.Text.Json.Nodes;
using Whiskers.Services.Mutations;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Vault;

/// <summary>What an editor asks for one vault-backed setting: a new value (<paramref name="Entered"/>), removing it
/// (<paramref name="Clear"/>), or — neither — keeping what is stored.</summary>
public sealed record SecretInput(string Section, string Property, string? Entered, bool Clear = false);

/// <summary>What a save did with the previous credentials.</summary>
/// <param name="UnremovedSecrets">Previous vault entries that could not be removed because the vault is not usable;
/// they stay encrypted and unused.</param>
public sealed record SecretSaveResult(int UnremovedSecrets);

/// <summary>
/// The save of settings sections with vault-backed credentials (R1-30), shared by the editors: each new value — one
/// entered now, or a legacy plaintext value that moves with the save — is written to the vault under a NEW key as a
/// gateway decision of its own (<c>vault.secret_set</c>), then the sections are rebuilt under the store's lock naming
/// those keys (<c>config.save</c> / <c>config.agent</c>), and only then are the previous keys removed
/// (<c>vault.secret_delete</c>). A failed or refused settings write removes the new keys again, so the live settings
/// and the vault never disagree. Without a usable vault a save that would have to store a value is refused before
/// anything is written. Values are never arguments of a decision.
/// </summary>
public static class VaultBackedSettingsSave
{
    /// <summary>Saves sections of app-settings.json: <paramref name="plainSections"/> carry everything but the vault-backed
    /// properties, <paramref name="secrets"/> say what happens to those.</summary>
    /// <param name="needsVault">The message when a value would have to be stored and the vault is not usable.</param>
    public static async Task<SecretSaveResult> SaveAppSettingsAsync(IUiMutationRunner mutations, IVaultService vault,
        IAppSettingsStore store, string scope, IReadOnlyDictionary<string, JsonObject> plainSections,
        IReadOnlyList<SecretInput> secrets, string needsVault)
    {
        var stored = new Dictionary<string, JsonObject?>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in plainSections.Keys) stored[section] = await store.ReadSectionAsync(section);

        var changes = await StageAsync(mutations, vault, secrets, s => stored.GetValueOrDefault(s), needsVault);
        IReadOnlyDictionary<string, JsonObject?> previous;
        try
        {
            var builds = plainSections.ToDictionary(
                s => s.Key,
                s => (Func<JsonObject?, JsonObject>)(inLock => Build(s.Value, s.Key, inLock, changes)),
                StringComparer.OrdinalIgnoreCase);
            previous = await mutations.RunAsync("config.save", new { section = scope }, _ => store.UpdateSectionsAsync(builds));
        }
        catch
        {
            await RemoveAsync(mutations, vault, changes.Values.Select(c => c.NewRef));
            throw;
        }
        return await RemoveReplacedAsync(mutations, vault, changes, s => previous.GetValueOrDefault(s));
    }

    /// <summary>Saves the agent settings (agent-settings.json) with <paramref name="apiKey"/> for its API key.</summary>
    public static async Task<SecretSaveResult> SaveAgentAsync(IUiMutationRunner mutations, IVaultService vault,
        Whiskers.Services.Agent.IAgentSettingsStore store, object arguments, Whiskers.Configuration.AgentSettings settings,
        Whiskers.Models.Agent.AgentPrincipal editor, SecretInput apiKey, string needsVault)
    {
        var stored = await store.ReadSectionAsync();
        var changes = await StageAsync(mutations, vault, [apiKey], _ => stored, needsVault);
        var change = changes.GetValueOrDefault((apiKey.Section, apiKey.Property)) ?? SecretFieldChange.Keep;
        JsonObject? previous;
        try
        {
            previous = await mutations.RunAsync("config.agent", arguments, _ => store.SaveAsync(settings, editor, change));
        }
        catch
        {
            await RemoveAsync(mutations, vault, changes.Values.Select(c => c.NewRef));
            throw;
        }
        return await RemoveReplacedAsync(mutations, vault, changes, _ => previous);
    }

    /// <summary>Decides each setting's change and writes the new values to the vault — after checking, before any write,
    /// that a value to be stored has a usable vault.</summary>
    private static async Task<Dictionary<(string, string), SecretFieldChange>> StageAsync(IUiMutationRunner mutations,
        IVaultService vault, IReadOnlyList<SecretInput> secrets, Func<string, JsonObject?> stored, string needsVault)
    {
        var plan = new List<(SecretInput Input, string Value, bool Moved)>();
        var changes = new Dictionary<(string, string), SecretFieldChange>();
        foreach (var input in secrets)
        {
            if (input.Clear) changes[(input.Section, input.Property)] = SecretFieldChange.Clear;
            else if (!string.IsNullOrWhiteSpace(input.Entered))
                plan.Add((input, VaultBackedSettings.IsVerbatim(input.Section, input.Property) ? input.Entered : input.Entered.Trim(), false));
            else if (VaultBackedSettings.PlaintextValue(stored(input.Section), input.Property) is { } legacy) plan.Add((input, legacy, true));
        }
        if (plan.Count > 0 && !vault.IsEnabled) throw new InvalidOperationException(needsVault);

        try
        {
            foreach (var (input, value, moved) in plan)
            {
                // A vault write of its own (R1-11): decided and audited by name, the value never in the arguments.
                var key = VaultBackedSettings.NewKey(input.Section, input.Property);
                await mutations.RunAsync("vault.secret_set", new { section = "vault", key }, _ => vault.SetSecretAsync(key, value));
                changes[(input.Section, input.Property)] = moved ? SecretFieldChange.Move(key, value) : SecretFieldChange.Set(key);
            }
        }
        catch
        {
            await RemoveAsync(mutations, vault, changes.Values.Select(c => c.NewRef));
            throw;
        }
        return changes;
    }

    private static JsonObject Build(JsonObject plain, string section, JsonObject? inLock,
        IReadOnlyDictionary<(string, string), SecretFieldChange> changes)
    {
        var next = (JsonObject)plain.DeepClone();
        foreach (var property in VaultBackedSettings.Fields.GetValueOrDefault(section) ?? [])
            changes.GetValueOrDefault((section, property), SecretFieldChange.Keep)!.ApplyTo(next, inLock, property);
        return next;
    }

    /// <summary>After the settings were written: removes the keys they named before and no longer name.</summary>
    private static async Task<SecretSaveResult> RemoveReplacedAsync(IUiMutationRunner mutations, IVaultService vault,
        IReadOnlyDictionary<(string Section, string Property), SecretFieldChange> changes, Func<string, JsonObject?> previous)
    {
        var replaced = changes
            .Where(c => c.Value.NewRef is not null || c.Value.Clears)
            .Select(c => (Old: VaultBackedSettings.StoredRef(previous(c.Key.Section), c.Key.Property), New: c.Value.NewRef))
            .Where(c => c.Old is not null && c.Old != c.New)
            .Select(c => c.Old!)
            .ToList();
        if (replaced.Count == 0) return new SecretSaveResult(0);
        if (!vault.IsEnabled) return new SecretSaveResult(replaced.Count);
        return new SecretSaveResult(await RemoveAsync(mutations, vault, replaced));
    }

    /// <summary>Removes vault entries no setting names — tidying up, so a failure only leaves an unused secret. Returns
    /// how many could not be removed.</summary>
    private static async Task<int> RemoveAsync(IUiMutationRunner mutations, IVaultService vault, IEnumerable<string?> keys)
    {
        var all = keys.OfType<string>().ToList();
        if (!vault.IsEnabled) return all.Count;
        var failed = 0;
        foreach (var key in all)
        {
            try
            {
                await mutations.RunAsync("vault.secret_delete", new { section = "vault", key }, _ => vault.DeleteSecretAsync(key));
            }
            catch { failed++; /* an unused secret at worst; no setting names it */ }
        }
        return failed;
    }
}
