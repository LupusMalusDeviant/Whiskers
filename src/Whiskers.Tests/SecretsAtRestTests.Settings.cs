using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models.Agent;
using Whiskers.Services.Agent;
using Whiskers.Models;
using Whiskers.Services.Mutations;
using Whiskers.Services.Vault;

namespace Whiskers.Tests;

// R1-30 (INV-52): the notification channels' credentials (SMTP password, chat and bot tokens, webhook URLs and secrets,
// the ntfy topic and token) and both AI clients' API keys are kept in the vault. After a save or the start-up migration
// no file of the data directory — app-settings.json, agent-settings.json, a self-backup — holds them in plaintext, and
// the options still read them.
public partial class SecretsAtRestTests
{
    private static readonly AgentPrincipal AgentAdmin = new(AgentPrincipalKind.WebUser, "admin", McpPermissionLevels.Admin, null, UserEmail: "a@x");

    private AgentSettingsStore AgentSettingsFile() => new(dataPaths: _paths);

    /// <summary>One fresh value per vault-backed setting, built at runtime (never a token-shaped literal).</summary>
    private static Dictionary<(string Section, string Property), string> FreshSettingValues()
        => VaultBackedSettings.Fields
            .SelectMany(f => f.Value.Select(p => (f.Key, p)))
            .ToDictionary(k => k, k => $"{k.Key}-{k.p}-" + Guid.NewGuid().ToString("N"));

    private static Dictionary<string, JsonObject> PlainSections() => VaultBackedSettings.AppSettingsSections
        .ToDictionary(s => s, _ => new JsonObject { ["Enabled"] = true }, StringComparer.OrdinalIgnoreCase);

    private async Task<SecretSaveResult> SaveAllAsync(IUiMutationRunner runner, IVaultService vault,
        Func<(string Section, string Property), SecretInput> input)
    {
        var app = VaultBackedSettings.AppSettingsSections
            .SelectMany(s => VaultBackedSettings.Fields[s].Select(p => input((s, p))))
            .ToList();
        var result = await VaultBackedSettingsSave.SaveAppSettingsAsync(runner, vault, AppSettings(), "notifications",
            PlainSections(), app, "needs vault");
        await VaultBackedSettingsSave.SaveAgentAsync(runner, vault, AgentSettingsFile(), new { section = "agent" },
            new AgentSettings { Enabled = true, Provider = "openai" }, AgentAdmin, input((VaultBackedSettings.AgentSection, "ApiKey")),
            "needs vault");
        return result;
    }

    /// <summary>The options as the app binds them: both settings files, then the vault-backed values from the vault.</summary>
    private IConfiguration SettingsConfiguration()
    {
        var builder = new ConfigurationBuilder();
        if (File.Exists(_paths.AppSettingsJson)) builder.AddJsonFile(_paths.AppSettingsJson);
        if (File.Exists(_paths.AgentSettingsJson)) builder.AddJsonFile(_paths.AgentSettingsJson);
        return builder.Build();
    }

    private static T Bound<T>(IConfiguration configuration, IVaultService vault, string section) where T : class, new()
    {
        var options = configuration.GetSection(section).Get<T>() ?? new T();
        new VaultSecretsPostConfigure<T>(configuration, vault, NullLogger<VaultSecretsPostConfigure<T>>.Instance, section)
            .PostConfigure(null, options);
        return options;
    }

    /// <summary>Each vault-backed setting as the options read it.</summary>
    private static Dictionary<(string, string), string> Materialized(IConfiguration c, IVaultService vault) => new()
    {
        [("Mattermost", "WebhookUrl")] = Bound<MattermostSettings>(c, vault, "Mattermost").WebhookUrl,
        [("Matrix", "AccessToken")] = Bound<MatrixSettings>(c, vault, "Matrix").AccessToken,
        [("Telegram", "BotToken")] = Bound<TelegramSettings>(c, vault, "Telegram").BotToken,
        [("Ntfy", "Topic")] = Bound<NtfySettings>(c, vault, "Ntfy").Topic,
        [("Ntfy", "Token")] = Bound<NtfySettings>(c, vault, "Ntfy").Token,
        [("Discord", "WebhookUrl")] = Bound<DiscordSettings>(c, vault, "Discord").WebhookUrl,
        [("Slack", "WebhookUrl")] = Bound<SlackSettings>(c, vault, "Slack").WebhookUrl,
        [("Email", "Password")] = Bound<EmailSettings>(c, vault, "Email").Password,
        [("WebhookNotification", "Url")] = Bound<WebhookNotificationSettings>(c, vault, "WebhookNotification").Url,
        [("WebhookNotification", "Secret")] = Bound<WebhookNotificationSettings>(c, vault, "WebhookNotification").Secret,
        [("AiChat", "ApiKey")] = Bound<AiChatSettings>(c, vault, "AiChat").ApiKey,
        [("Agent", "ApiKey")] = Bound<AgentSettings>(c, vault, "Agent").ApiKey,
    };

    [Fact]
    public void Every_vault_backed_setting_is_covered_here()
    {
        // A new vault-backed setting must be added to Materialized (and so to every test below).
        var covered = Materialized(new ConfigurationBuilder().Build(), new UnconfirmedVault()).Keys
            .Select(k => $"{k.Item1}:{k.Item2}").Order();
        var registered = VaultBackedSettings.Fields.SelectMany(f => f.Value.Select(p => $"{f.Key}:{p}")).Order();
        Assert.Equal(registered, covered);
    }

    [Fact]
    public async Task Notification_and_ai_saves_keep_no_plaintext_in_any_file_or_backup_and_the_options_read_them()
    {
        var vault = await Vault();
        var values = FreshSettingValues();

        await SaveAllAsync(new SettingsRunner(), vault, k => new SecretInput(k.Section, k.Property, values[k]));

        Assert.Equal(values, Materialized(SettingsConfiguration(), vault));
        await CreatePlaintextBackupAsync(vault);
        AssertNoPlaintext(values.Values.ToArray());
    }

    [Fact]
    public async Task Empty_fields_keep_the_stored_keys_and_clearing_removes_one()
    {
        var vault = await Vault();
        var values = FreshSettingValues();
        await SaveAllAsync(new SettingsRunner(), vault, k => new SecretInput(k.Section, k.Property, values[k]));
        var appBefore = File.ReadAllText(_paths.AppSettingsJson);
        var keysBefore = vault.ListSecrets().Select(e => e.Key).Order().ToList();

        // Every field empty: nothing changes but the plain settings, no value is revealed or rewritten.
        await SaveAllAsync(new SettingsRunner(), vault, k => new SecretInput(k.Section, k.Property, ""));
        Assert.Equal(keysBefore, vault.ListSecrets().Select(e => e.Key).Order().ToList());
        Assert.Equal(values, Materialized(SettingsConfiguration(), vault));
        Assert.Equal(appBefore, File.ReadAllText(_paths.AppSettingsJson));

        // Clearing the SMTP password: its key is removed, the others stay.
        var smtp = (await AppSettings().ReadSectionAsync("Email"))!["PasswordRef"]!.GetValue<string>();
        await VaultBackedSettingsSave.SaveAppSettingsAsync(new SettingsRunner(), vault, AppSettings(), "channels",
            new Dictionary<string, JsonObject> { ["Email"] = new() { ["Enabled"] = true } },
            [new SecretInput("Email", "Password", null, Clear: true)], "needs vault");
        Assert.Null(vault.GetSecret(smtp));
        Assert.Equal("", Materialized(SettingsConfiguration(), vault)[("Email", "Password")]);
        Assert.Equal(values[("Telegram", "BotToken")], Materialized(SettingsConfiguration(), vault)[("Telegram", "BotToken")]);
    }

    [Fact]
    public async Task A_refused_settings_write_removes_the_new_keys_and_keeps_the_previous_credentials()
    {
        var vault = await Vault();
        var values = FreshSettingValues();
        await SaveAllAsync(new SettingsRunner(), vault, k => new SecretInput(k.Section, k.Property, values[k]));
        var keysBefore = vault.ListSecrets().Select(e => e.Key).Order().ToList();

        var refusing = new SettingsRunner { Refuse = ["config.save", "config.agent"] };
        var rotated = FreshSettingValues();
        await Assert.ThrowsAsync<MutationRefusedException>(() => SaveAllAsync(refusing, vault, k => new SecretInput(k.Section, k.Property, rotated[k])));
        await Assert.ThrowsAsync<MutationRefusedException>(() => VaultBackedSettingsSave.SaveAgentAsync(refusing, vault, AgentSettingsFile(),
            new { section = "agent" }, new AgentSettings(), AgentAdmin, new SecretInput("Agent", "ApiKey", rotated[("Agent", "ApiKey")]), "needs vault"));

        // Live settings and vault agree with the previous save: no new key, no mixed state.
        Assert.Equal(keysBefore, vault.ListSecrets().Select(e => e.Key).Order().ToList());
        Assert.Equal(values, Materialized(SettingsConfiguration(), vault));
    }

    [Fact]
    public async Task Without_a_usable_vault_a_credential_save_is_refused_before_anything_is_written()
    {
        var vault = await Vault(key: null);
        var values = FreshSettingValues();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SaveAllAsync(new SettingsRunner(), vault, k => new SecretInput(k.Section, k.Property, values[k])));

        Assert.Equal("needs vault", ex.Message);
        Assert.False(File.Exists(_paths.AppSettingsJson));
        Assert.False(File.Exists(_paths.AgentSettingsJson));
        // A save that stores no credential still works without a vault.
        await VaultBackedSettingsSave.SaveAppSettingsAsync(new SettingsRunner(), vault, AppSettings(), "channels",
            new Dictionary<string, JsonObject> { ["Slack"] = new() { ["Enabled"] = false } },
            [new SecretInput("Slack", "WebhookUrl", "")], "needs vault");
        Assert.True(File.Exists(_paths.AppSettingsJson));
    }

    private void WriteLegacySettings(Dictionary<(string Section, string Property), string> values)
    {
        var app = new JsonObject();
        foreach (var section in VaultBackedSettings.AppSettingsSections)
        {
            var obj = new JsonObject { ["Enabled"] = true };
            foreach (var p in VaultBackedSettings.Fields[section]) obj[p] = values[(section, p)];
            app[section] = obj;
        }
        File.WriteAllText(_paths.AppSettingsJson, app.ToJsonString());
        File.WriteAllText(_paths.AgentSettingsJson, new JsonObject
        {
            ["Agent"] = new JsonObject { ["Enabled"] = true, ["Provider"] = "openai", ["ApiKey"] = values[("Agent", "ApiKey")] },
        }.ToJsonString());
    }

    [Fact]
    public async Task Migration_moves_legacy_notification_and_ai_plaintext_into_the_vault()
    {
        var values = FreshSettingValues();
        WriteLegacySettings(values);
        var vault = await Vault();
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertNoPlaintext(values[("Email", "Password")]));
        Assert.Equal(values, Materialized(SettingsConfiguration(), vault));   // legacy plaintext still works before

        await new CredentialVaultMigration(vault, await Servers(), AppSettings(), NullLogger<CredentialVaultMigration>.Instance,
            agentSettings: AgentSettingsFile()).InitializeAsync();

        var app = await AppSettings().ReadSectionAsync("Email");
        Assert.Equal(VaultBackedSettings.MigratedKey("Email", "Password"), app!["PasswordRef"]!.GetValue<string>());
        Assert.Equal(VaultBackedSettings.MigratedKey("Agent", "ApiKey"),
            (await AgentSettingsFile().ReadSectionAsync())!["ApiKeyRef"]!.GetValue<string>());
        var reopened = await Vault();
        Assert.Equal(values, Materialized(SettingsConfiguration(), reopened));
        await CreatePlaintextBackupAsync(reopened);
        AssertNoPlaintext(values.Values.ToArray());
    }

    [Fact]
    public async Task Without_a_usable_vault_the_migration_keeps_the_settings_usable_and_a_save_must_move_them()
    {
        var values = FreshSettingValues();
        WriteLegacySettings(values);
        var before = File.ReadAllBytes(_paths.AppSettingsJson);
        var vault = await Vault(key: null);

        await new CredentialVaultMigration(vault, await Servers(), AppSettings(), NullLogger<CredentialVaultMigration>.Instance,
            agentSettings: AgentSettingsFile()).InitializeAsync();

        Assert.Equal(before, File.ReadAllBytes(_paths.AppSettingsJson));
        Assert.Equal(values, Materialized(SettingsConfiguration(), vault));
        // A save with empty fields would have to move the plaintext: refused, nothing written.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SaveAllAsync(new SettingsRunner(), vault, k => new SecretInput(k.Section, k.Property, "")));
        Assert.Equal(before, File.ReadAllBytes(_paths.AppSettingsJson));
    }

    [Fact]
    public async Task A_save_with_empty_fields_moves_legacy_plaintext_into_the_vault()
    {
        var values = FreshSettingValues();
        WriteLegacySettings(values);
        var vault = await Vault();

        await SaveAllAsync(new SettingsRunner(), vault, k => new SecretInput(k.Section, k.Property, ""));

        Assert.Equal(values, Materialized(SettingsConfiguration(), vault));
        AssertNoPlaintext(values.Values.ToArray());
    }

    [Fact]
    public async Task A_locked_vault_leaves_a_vault_backed_setting_unset_never_plaintext_or_stale()
    {
        var vault = await Vault();
        var values = FreshSettingValues();
        await SaveAllAsync(new SettingsRunner(), vault, k => new SecretInput(k.Section, k.Property, values[k]));

        var locked = await Vault("another-passphrase-entirely");
        Assert.Equal(VaultKeyState.Wrong, locked.KeyState);
        Assert.All(Materialized(SettingsConfiguration(), locked).Values, v => Assert.Equal("", v));
        Assert.All(Materialized(SettingsConfiguration(), await Vault(key: null)).Values, v => Assert.Equal("", v));
    }

    [Fact]
    public async Task Options_bound_before_the_vault_started_read_the_value_after_the_migration_and_redact_it()
    {
        var token = "BOTTOKEN-" + Guid.NewGuid().ToString("N");
        var key = VaultBackedSettings.MigratedKey("Telegram", "BotToken");
        await (await Vault()).SetSecretAsync(key, token);

        // The module's own registrations, against a vault that has not started yet — as the app binds early.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["VAULT_KEY"] = VaultKey, ["Telegram:Enabled"] = "true", ["Telegram:BotTokenRef"] = key,
        }).Build();
        var vault = new VaultService(config, NullLogger<VaultService>.Instance, dataPaths: _paths);
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<IVaultService>(vault);
        new Whiskers.Modules.Notifications.NotificationsModule().ConfigureServices(services, config);
        using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<TelegramSettings>>();
        Assert.Equal("", monitor.CurrentValue.BotToken);

        await vault.InitializeAsync();
        foreach (var cache in provider.GetServices<IVaultBackedOptionsCache>()) cache.Invalidate();

        Assert.Equal(token, monitor.CurrentValue.BotToken);
        // Read from the vault before the redaction registered it: the token never reaches a log line.
        Assert.DoesNotContain(token, Whiskers.Utils.KnownSecrets.Replace($"POST https://api.telegram.org/bot{token}/sendMessage"));
    }

    [Fact]
    public void A_plaintext_value_next_to_a_reference_still_counts_as_plaintext()
    {
        var section = new JsonObject { ["Password"] = "hand-edited", ["PasswordRef"] = "settings:email:password:x" };
        Assert.Null(VaultBackedSettings.PlaintextValue(section, "Password"));
        Assert.True(VaultBackedSettings.HasPlaintext(section, "Password"));
        Assert.False(VaultBackedSettings.HasPlaintext(new JsonObject { ["PasswordRef"] = "k" }, "Password"));
    }

    [Fact]
    public void A_moved_legacy_value_applies_only_while_the_section_still_holds_it()
    {
        var legacy = "legacy-" + Guid.NewGuid().ToString("N");
        var move = SecretFieldChange.Move(VaultBackedSettings.NewKey("Telegram", "BotToken"), legacy);
        var stored = new JsonObject { ["BotToken"] = legacy };

        var next = new JsonObject();
        move.ApplyTo(next, stored, "BotToken");
        Assert.Null(next["BotToken"]);
        Assert.NotNull(next["BotTokenRef"]);

        Assert.Throws<SecretFieldChange.SecretChangedException>(() => move.ApplyTo(new JsonObject(), new JsonObject(), "BotToken"));
        Assert.Throws<SecretFieldChange.SecretChangedException>(() => move.ApplyTo(new JsonObject(),
            new JsonObject { ["BotTokenRef"] = VaultBackedSettings.NewKey("Telegram", "BotToken") }, "BotToken"));
    }

    [Fact]
    public async Task The_stores_refuse_a_plaintext_notification_or_ai_credential()
    {
        foreach (var section in VaultBackedSettings.AppSettingsSections)
            foreach (var property in VaultBackedSettings.Fields[section])
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    AppSettings().SaveSectionAsync(section, new JsonObject { [property] = "x-" + Guid.NewGuid().ToString("N") }));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AgentSettingsFile().SaveAsync(new AgentSettings { ApiKey = "x-" + Guid.NewGuid().ToString("N") }, AgentAdmin));
        Assert.False(File.Exists(_paths.AppSettingsJson));
        Assert.False(File.Exists(_paths.AgentSettingsJson));
    }

    /// <summary>Runs every mutation, refusing the listed capabilities.</summary>
    private sealed class SettingsRunner : IUiMutationRunner
    {
        public HashSet<string> Refuse { get; init; } = [];

        public async Task<T> RunAsync<T>(string capabilityId, object arguments, Func<CancellationToken, Task<T>> action,
            CancellationToken ct = default)
        {
            if (Refuse.Contains(capabilityId)) throw new MutationRefusedException("refused", MutationStatus.Denied, "op");
            return await action(ct);
        }

        public async Task RunAsync(string capabilityId, object arguments, Func<CancellationToken, Task> action,
            CancellationToken ct = default)
        {
            if (Refuse.Contains(capabilityId)) throw new MutationRefusedException("refused", MutationStatus.Denied, "op");
            await action(ct);
        }

        public Task RefuseAsync(string capabilityId, object arguments, string reason, CancellationToken ct = default) => Task.CompletedTask;
    }
}
