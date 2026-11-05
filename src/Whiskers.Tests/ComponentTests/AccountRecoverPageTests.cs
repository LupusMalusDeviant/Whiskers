using System.Diagnostics;
using System.Reflection;
using Bunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Whiskers.Components.Pages;
using Whiskers.Models;
using Whiskers.Services.Auth;

namespace Whiskers.Tests.ComponentTests;

/// <summary>R1-49 (review round 2, NIT 5): every answer of the recovery page takes at least
/// <see cref="AccountRecover.MinimumDuration"/> — a success as much as a refusal — and that is a floor, not a wait added
/// on top of a slow check. Runs alone (<see cref="WallClockTimingCollection"/>): it tells 1.5 s from 2.5 s by real time.</summary>
[Collection(WallClockTimingCollection.Name)]
public class AccountRecoverPageTests : BunitContext
{
    private IRenderedComponent<AccountRecover> RenderWith(ILocalAccountService accounts)
    {
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddMudServices();
        Services.AddSingleton<IKeyInterceptorService>(ServersKubeconfigSaveTests.NullProxy<IKeyInterceptorService>.Create());
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton(accounts);
        JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = Render<AccountRecover>();
        foreach (var (field, value) in new[] { ("_email", "ops@local.test"), ("_code", "ABCD"), ("_new", "Long-Password-12"), ("_confirm", "Long-Password-12") })
            typeof(AccountRecover).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cut.Instance, value);
        return cut;
    }

    private static async Task<TimeSpan> SubmitAsync(IRenderedComponent<AccountRecover> cut)
    {
        var watch = Stopwatch.StartNew();
        await cut.InvokeAsync(() => (Task)typeof(AccountRecover).GetMethod("Redeem", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(cut.Instance, [])!);
        return watch.Elapsed;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Every_answer_takes_at_least_the_minimum_duration(bool success)
    {
        var cut = RenderWith(new Answering(success ? LocalAccountResult.Ok() : LocalAccountResult.Refused(LocalAccountErrors.InvalidCode), TimeSpan.Zero));

        var elapsed = await SubmitAsync(cut);

        Assert.True(elapsed >= AccountRecover.MinimumDuration - TimeSpan.FromMilliseconds(50), $"answered after {elapsed}");
    }

    [Fact]
    public async Task A_slow_check_is_not_slowed_down_further()
    {
        var slow = TimeSpan.FromSeconds(1.5);
        var cut = RenderWith(new Answering(LocalAccountResult.Refused(LocalAccountErrors.InvalidCode), slow));

        var elapsed = await SubmitAsync(cut);

        // A floor: about the check's own time, not the check plus the minimum (2.5 s).
        Assert.True(elapsed < slow + AccountRecover.MinimumDuration - TimeSpan.FromMilliseconds(100), $"answered after {elapsed}");
    }

    private sealed class Answering(LocalAccountResult result, TimeSpan delay) : ILocalAccountService
    {
        public async Task<LocalAccountResult> RedeemRecoveryCodeAsync(string email, string code, string newPassword, CancellationToken ct = default)
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
            return result;
        }

        public Task<IReadOnlyList<LocalAccountInfo>> ListAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LocalAccountInfo?> GetOwnAccountAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LocalAccountResult> ChangeOwnPasswordAsync(string currentPassword, string newPassword, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LocalAccountResult> CreateAsync(string email, AppRole role, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LocalAccountResult> ResetPasswordAsync(string email, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LocalAccountResult> DeleteAsync(string email, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
