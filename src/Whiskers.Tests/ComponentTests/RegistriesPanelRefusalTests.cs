using System.Reflection;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Whiskers.Components.Shared;
using Whiskers.Models;
using Whiskers.Services.Auth;
using Whiskers.Services.Mutations;
using Whiskers.Services.Registries;
using Whiskers.Services.Vault;

namespace Whiskers.Tests.ComponentTests;

/// <summary>R1-22: a registry host carrying user info is refused as a Denied event that never contains the host, and
/// nothing is saved.</summary>
public class RegistriesPanelRefusalTests : BunitContext, IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sw-r122-reg-" + Guid.NewGuid().ToString("N"));
    private readonly RegistryConfigService _registries;
    private readonly Recording _mutations = new();

    public RegistriesPanelRefusalTests()
    {
        Directory.CreateDirectory(_dir);
        _registries = new RegistryConfigService(new ProtectedResourceResolutionTests.NoVault(), Path.Combine(_dir, "registries.json"));
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddMudServices();
        Services.AddSingleton<IKeyInterceptorService>(ServersKubeconfigSaveTests.NullProxy<IKeyInterceptorService>.Create());
        Services.AddSingleton<IRegistryConfigService>(_registries);
        Services.AddSingleton<IVaultService>(new ProtectedResourceResolutionTests.NoVault());
        Services.AddSingleton<ICurrentUserService>(ServersKubeconfigSaveTests.NullProxy<ICurrentUserService>.Create());
        Services.AddSingleton<IUiMutationRunner>(_mutations);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task A_host_with_user_info_is_one_denied_event_without_the_host()
    {
        var panel = Render<RegistriesPanel>();
        typeof(RegistriesPanel).GetField("_form", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(panel.Instance, new RegistryConfig { Name = "ci", Host = "https://ci:owl42@registry.example" });

        var save = typeof(RegistriesPanel).GetMethod("Save", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await panel.InvokeAsync(() => (Task)save.Invoke(panel.Instance, null)!);

        var refusal = Assert.Single(_mutations.Refused);
        Assert.Equal("registry.save", refusal.Capability);
        Assert.DoesNotContain("owl42", refusal.Arguments + refusal.Reason);
        Assert.DoesNotContain("registry.example", refusal.Arguments);
        Assert.Empty(_mutations.Ran);
        Assert.Empty(await _registries.GetRegistriesAsync());
    }

    private sealed class Recording : IUiMutationRunner
    {
        public List<(string Capability, string Arguments, string Reason)> Refused { get; } = [];
        public List<string> Ran { get; } = [];

        public Task<T> RunAsync<T>(string capabilityId, object arguments, Func<CancellationToken, Task<T>> action,
            CancellationToken ct = default)
        {
            Ran.Add(capabilityId);
            return action(ct);
        }

        public Task RunAsync(string capabilityId, object arguments, Func<CancellationToken, Task> action,
            CancellationToken ct = default)
        {
            Ran.Add(capabilityId);
            return action(ct);
        }

        public Task RefuseAsync(string capabilityId, object arguments, string reason, CancellationToken ct = default)
        {
            Refused.Add((capabilityId, JsonSerializer.Serialize(arguments), reason));
            return Task.CompletedTask;
        }
    }
}
