using Microsoft.Extensions.Configuration;
using Whiskers.Configuration;

namespace Whiskers.Tests;

public class DataPathOptionsTests
{
    [Fact]
    public void Defaults_to_app_data_when_unset()
    {
        var paths = new DataPathOptions();

        Assert.Equal("/app/data", paths.RootDir);
        Assert.Equal("/app/data/metrics.db", paths.DbPath);
        Assert.Equal("Data Source=/app/data/metrics.db", paths.DbConnectionString);
        Assert.Equal("/app/data/keys", paths.KeysDir);
        Assert.Equal("/app/data/servers.json", paths.ServersJson);
        Assert.Equal("/app/data/vault.json", paths.VaultJson);
        Assert.Equal("/app/data/ssh-keys", paths.SshKeysDir);
        Assert.Equal("/app/data/mtls", paths.MtlsDir);
        Assert.Equal("/app/data/backups", paths.BackupsDir);
        Assert.Equal("/app/data/netbird/config.json", paths.NetbirdConfigPath);
    }

    [Fact]
    public void Override_reroots_every_derived_path()
    {
        var paths = new DataPathOptions("/var/lib/whiskers");

        Assert.Equal("/var/lib/whiskers", paths.RootDir);
        Assert.Equal("/var/lib/whiskers/metrics.db", paths.DbPath);
        Assert.Equal("/var/lib/whiskers/api-keys.json", paths.ApiKeysJson);
        Assert.Equal("/var/lib/whiskers/agent-chat", paths.AgentChatDir);
        Assert.Equal("/var/lib/whiskers/tailscale", paths.TailscaleStateDir);
    }

    [Fact]
    public void Derived_paths_always_use_forward_slash()
    {
        // These strings end up inside POSIX shell commands on the Linux container/remote hosts, so the
        // separator must be '/' regardless of the host OS running this code.
        var paths = new DataPathOptions("/srv/data");
        Assert.DoesNotContain('\\', paths.NetbirdConfigPath);
        Assert.DoesNotContain('\\', paths.BackupsDir);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_override_falls_back_to_default(string? blank)
        => Assert.Equal("/app/data", new DataPathOptions(blank).RootDir);

    [Theory]
    [InlineData("/data/", "/data")]
    [InlineData("/data\\", "/data")]
    [InlineData("/nested/root/", "/nested/root")]
    public void Trailing_separators_are_trimmed(string input, string expected)
        => Assert.Equal(expected, new DataPathOptions(input).RootDir);

    [Fact]
    public void FromConfiguration_honors_env_var_key()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [DataPathOptions.EnvVarName] = "/mnt/data" })
            .Build();

        Assert.Equal("/mnt/data", DataPathOptions.FromConfiguration(config).RootDir);
    }

    [Fact]
    public void FromConfiguration_honors_config_key()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [DataPathOptions.ConfigKey] = "/mnt/cfg" })
            .Build();

        Assert.Equal("/mnt/cfg", DataPathOptions.FromConfiguration(config).RootDir);
    }

    [Fact]
    public void FromConfiguration_env_var_wins_over_config_key()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DataPathOptions.EnvVarName] = "/mnt/env",
                [DataPathOptions.ConfigKey] = "/mnt/cfg",
            })
            .Build();

        Assert.Equal("/mnt/env", DataPathOptions.FromConfiguration(config).RootDir);
    }

    [Fact]
    public void FromConfiguration_falls_back_to_default_when_neither_set()
    {
        var config = new ConfigurationBuilder().Build();

        Assert.Equal("/app/data", DataPathOptions.FromConfiguration(config).RootDir);
    }
}
