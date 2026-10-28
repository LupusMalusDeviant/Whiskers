namespace Whiskers.Tests;

/// <summary>The boot-test helper changes process-wide environment variables, so it must leave none of them changed
/// after it is disposed — or after it fails half-way.</summary>
[Collection("WebAppBoot")]
public sealed class BootSettingsTests
{
    private static string Key() => $"BootSettingsTest:{Guid.NewGuid():N}";

    private static string Env(string key) => key.Replace(":", "__", StringComparison.Ordinal);

    [Fact]
    public void Values_are_set_for_the_boot_and_the_earlier_ones_come_back()
    {
        var existing = Key();
        var fresh = Key();
        Environment.SetEnvironmentVariable(Env(existing), "before");
        try
        {
            using (BootSettings.Apply([new(existing, "during"), new(fresh, "new")]))
            {
                Assert.Equal("during", Environment.GetEnvironmentVariable(Env(existing)));
                Assert.Equal("new", Environment.GetEnvironmentVariable(Env(fresh)));
            }

            Assert.Equal("before", Environment.GetEnvironmentVariable(Env(existing)));
            Assert.Null(Environment.GetEnvironmentVariable(Env(fresh)));
        }
        finally
        {
            Environment.SetEnvironmentVariable(Env(existing), null);
        }
    }

    [Fact]
    public void A_key_given_twice_ends_at_its_value_from_before()
    {
        var key = Key();
        Environment.SetEnvironmentVariable(Env(key), "before");
        try
        {
            using (BootSettings.Apply([new(key, "first"), new(key, "second")]))
                Assert.Equal("second", Environment.GetEnvironmentVariable(Env(key)));

            Assert.Equal("before", Environment.GetEnvironmentVariable(Env(key)));
        }
        finally
        {
            Environment.SetEnvironmentVariable(Env(key), null);
        }
    }

    [Fact]
    public void A_setting_that_cannot_be_applied_undoes_the_ones_before_it()
    {
        var first = Key();

        // An environment variable name cannot contain '='.
        Assert.ThrowsAny<ArgumentException>(() => BootSettings.Apply([new(first, "set"), new("bad=name", "x")]));

        Assert.Null(Environment.GetEnvironmentVariable(Env(first)));
    }

    [Fact]
    public void Settings_that_fail_to_enumerate_change_nothing()
    {
        var first = Key();
        IEnumerable<KeyValuePair<string, string?>> Broken()
        {
            yield return new(first, "set");
            throw new InvalidOperationException("broken settings");
        }

        Assert.Throws<InvalidOperationException>(() => BootSettings.Apply(Broken()));

        Assert.Null(Environment.GetEnvironmentVariable(Env(first)));
    }
}
