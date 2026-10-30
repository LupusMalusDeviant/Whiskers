using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Models;
using Whiskers.Services.AuditLog;
using Whiskers.Services.Auth;
using Whiskers.Services.Backup;
using Whiskers.Startup;

namespace Whiskers.Tests;

/// <summary>R1-22 over the real pipeline: sign-ins, refused sign-ins and sign-outs are audited without changing what
/// the login does, and a self-backup download is audited before its first byte — or not handed out at all.</summary>
[Collection("WebAppBoot")] // serialized: the boot needs the process-wide WHISKERS_DATA_DIR env var
public sealed class AuthAuditBootTests
{
    private const string Email = "admin@local.test";
    private const string Password = "CorrectHorse12!!";

    [Fact]
    public async Task Sign_ins_refusals_and_sign_outs_are_audited_without_secrets() =>
        await BootAsync(async (services, client) =>
        {
            var token = await AntiforgeryTokenAsync(client);
            await LoginAsync(client, token, "nobody@local.test", "typed-into-the-wrong-field");
            await LoginAsync(client, token, Email, "wrong-password-1");
            var ok = await LoginAsync(client, token, Email, Password);
            Assert.Equal("/", ok.Headers.Location!.OriginalString);
            await client.GetAsync("/logout");

            // Written in the background (a sign-in never waits for the audit): wait for all four.
            var rows = await RowsAsync(services, "auth.", expected: 4);
            var unknown = Assert.Single(rows, r => r.Action == AuthAuditLog.LoginFailedAction && r.Actor == "anonymous");
            Assert.Contains($"reason={AuthAuditReasons.UnknownAccount}", unknown.Details);
            Assert.False(unknown.Success);
            var wrong = Assert.Single(rows, r => r.Action == AuthAuditLog.LoginFailedAction && r.Actor == Email);
            Assert.Contains($"reason={AuthAuditReasons.WrongPassword}", wrong.Details);
            Assert.Equal("local", wrong.TargetId);
            var signedIn = Assert.Single(rows, r => r.Action == AuthAuditLog.LoginAction);
            Assert.Equal(Email, signedIn.Actor);
            Assert.True(signedIn.Success);
            Assert.Single(rows, r => r.Action == AuthAuditLog.LogoutAction && r.Actor == Email);

            // Neither a password nor an unknown account name (it may be a password typed into the wrong field).
            foreach (var row in rows)
            {
                var text = $"{row.Actor}|{row.TargetId}|{row.TargetName}|{row.Details}";
                Assert.DoesNotContain("wrong-password-1", text);
                Assert.DoesNotContain(Password, text);
                Assert.DoesNotContain("typed-into-the-wrong-field", text);
                Assert.DoesNotContain("nobody@local.test", text);
            }
        });

    [Fact]
    public async Task A_backup_download_is_audited_before_it_is_handed_out() =>
        await BootAsync(async (services, client) =>
        {
            var token = await AntiforgeryTokenAsync(client);
            await LoginAsync(client, token, Email, Password);
            var backup = await services.GetRequiredService<IBackupService>().CreateBackupAsync("test");

            var response = await client.GetAsync($"/backups/self/{backup.Id}/download");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var row = Assert.Single(await RowsAsync(services, WhiskersPipelineExtensions.BackupDownloadAction, expected: 1));
            Assert.Equal(Email, row.Actor);
            Assert.Equal(backup.Id, row.TargetId);
            Assert.Equal(backup.FileName, row.TargetName);
            Assert.Contains($"{backup.SizeBytes} bytes", row.Details);
            Assert.True(row.Success);
        });

    [Fact]
    public async Task Without_its_audit_record_a_backup_is_not_handed_out_while_sign_in_still_works() =>
        await BootAsync(async (services, client) =>
        {
            var token = await AntiforgeryTokenAsync(client);
            // The audit store is down: the sign-in still succeeds (its record never blocks it) ...
            var login = await LoginAsync(client, token, Email, Password);
            Assert.Equal("/", login.Headers.Location!.OriginalString);
            var backup = await services.GetRequiredService<IBackupService>().CreateBackupAsync("test");

            // ... but the archive is not handed out without its record.
            var response = await client.GetAsync($"/backups/self/{backup.Id}/download");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.DoesNotContain(response.Content.Headers, h => h.Key == "Content-Disposition");
        }, audit: AuditMode.Down);

    [Fact]
    public async Task A_locked_account_is_recorded_as_locked_out() =>
        await BootAsync(async (services, client) =>
        {
            var token = await AntiforgeryTokenAsync(client);
            for (var i = 0; i < 5; i++) await LoginAsync(client, token, Email, "wrong-" + i);
            await LoginAsync(client, token, Email, Password);

            var rows = await RowsAsync(services, AuthAuditLog.LoginFailedAction, expected: 6);
            Assert.Equal(5, rows.Count(r => r.Details!.Contains($"reason={AuthAuditReasons.WrongPassword}")));
            Assert.Single(rows, r => r.Details!.Contains($"reason={AuthAuditReasons.LockedOut}"));
        });

    [Fact]
    public async Task A_stalled_audit_store_delays_no_sign_in_or_sign_out() =>
        await BootAsync(async (services, client) =>
        {
            var token = await AntiforgeryTokenAsync(client);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var refused = await LoginAsync(client, token, Email, "wrong");
            // The background writer is now stuck inside the store's write: every answer below comes regardless.
            await ((StalledAudit)services.GetRequiredService<IAuditLogService>()).Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var login = await LoginAsync(client, token, Email, Password);
            var logout = await client.GetAsync("/logout");
            clock.Stop();

            Assert.Contains("error=invalid", refused.Headers.Location!.OriginalString);
            Assert.Equal("/", login.Headers.Location!.OriginalString);
            Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
            // The audit write never completes; the answers came anyway, well before any database timeout.
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
        }, audit: AuditMode.Stalled);

    [Fact]
    public async Task A_refused_download_by_a_signed_in_non_admin_is_recorded() =>
        await BootAsync(async (services, client) =>
        {
            const string viewer = "viewer@local.test";
            using (var scope = services.CreateScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Whiskers.Services.Persistence.AppUser>>();
                Assert.True((await users.CreateAsync(new Whiskers.Services.Persistence.AppUser { UserName = viewer, Email = viewer }, Password)).Succeeded);
            }
            await services.GetRequiredService<IRoleService>().SetRoleAsync(viewer, AppRole.Viewer);
            var backup = await services.GetRequiredService<IBackupService>().CreateBackupAsync("test");
            var token = await AntiforgeryTokenAsync(client);
            Assert.Equal("/", (await LoginAsync(client, token, viewer, Password)).Headers.Location!.OriginalString);

            var response = await client.GetAsync($"/backups/self/{backup.Id}/download");
            // Route text that names no archive (and could carry anything) is never recorded.
            var forged = await client.GetAsync("/backups/self/password=hunter2/download");

            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.NotEqual(HttpStatusCode.OK, forged.StatusCode);
            var rows = await RowsAsync(services, WhiskersPipelineExtensions.BackupDownloadAction, expected: 2);
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.Equal(viewer, r.Actor));
            Assert.All(rows, r => Assert.False(r.Success));
            Assert.Single(rows, r => r.TargetId == backup.Id && r.TargetName == backup.FileName);
            Assert.Single(rows, r => r.TargetId == "(unknown archive)");
            Assert.DoesNotContain(rows, r => $"{r.TargetId}{r.TargetName}{r.Details}".Contains("hunter2"));
        });

    [Fact]
    public async Task An_audit_export_is_recorded_first_and_carries_an_intact_trail() =>
        await BootAsync(async (services, client) =>
        {
            var token = await AntiforgeryTokenAsync(client);
            await LoginAsync(client, token, Email, Password);

            var response = await client.GetAsync("/audit/export");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(json.RootElement.GetProperty("verification").GetProperty("Intact").GetBoolean());
            // The export's own record was written before the snapshot, so it is part of what was exported.
            Assert.Contains(json.RootElement.GetProperty("rows").EnumerateArray(),
                r => r.GetProperty("Action").GetString() == AuditExport.Action && r.GetProperty("Actor").GetString() == Email);
            var row = Assert.Single(await RowsAsync(services, AuditExport.Action, expected: 1));
            Assert.True(row.Success);
        });

    [Fact]
    public async Task Without_its_audit_record_no_audit_export_is_handed_out() =>
        await BootAsync(async (_, client) =>
        {
            var token = await AntiforgeryTokenAsync(client);
            await LoginAsync(client, token, Email, Password);

            var response = await client.GetAsync("/audit/export");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.DoesNotContain(response.Content.Headers, h => h.Key == "Content-Disposition");
        }, audit: AuditMode.Down);

    [Fact]
    public async Task A_non_admin_cannot_export_the_audit_trail_and_the_refusal_is_recorded() =>
        await BootAsync(async (services, client) =>
        {
            const string viewer = "operator@local.test";
            using (var scope = services.CreateScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Whiskers.Services.Persistence.AppUser>>();
                Assert.True((await users.CreateAsync(new Whiskers.Services.Persistence.AppUser { UserName = viewer, Email = viewer }, Password)).Succeeded);
            }
            await services.GetRequiredService<IRoleService>().SetRoleAsync(viewer, AppRole.Operator);
            var token = await AntiforgeryTokenAsync(client);
            Assert.Equal("/", (await LoginAsync(client, token, viewer, Password)).Headers.Location!.OriginalString);

            var response = await client.GetAsync("/audit/export");

            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain(response.Content.Headers, h => h.Key == "Content-Disposition");
            var row = Assert.Single(await RowsAsync(services, AuditExport.Action, expected: 1));
            Assert.Equal(viewer, row.Actor);
            Assert.False(row.Success);
        });

    /// <summary>The audit rows whose action starts with <paramref name="prefix"/>, once at least
    /// <paramref name="expected"/> are there (sign-in rows are written in the background) or after 10 seconds.</summary>
    private static async Task<List<AuditLogEntity>> RowsAsync(IServiceProvider services, string prefix, int expected = 0)
    {
        var audit = services.GetRequiredService<IAuditLogService>();
        var until = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var rows = await audit.GetRecentAsync(500, actionFilter: prefix);
            if (rows.Count >= expected || DateTime.UtcNow > until) return rows;
            await Task.Delay(50);
        }
    }

    private static async Task<string> AntiforgeryTokenAsync(HttpClient client)
    {
        var html = await (await client.GetAsync("/login")).Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token));
        return token;
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string token, string email, string password) =>
        client.PostAsync("/login-local", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = email, ["password"] = password, ["__RequestVerificationToken"] = token,
        }));

    public enum AuditMode { Working, Down, Stalled }

    /// <summary>An audit store whose writes never complete, as a locked table or a stalled connection does.</summary>
    private sealed class StalledAudit : IAuditLogService
    {
        /// <summary>Completes once a write has started (and hangs).</summary>
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task LogAsync(string actor, string actorType, string action, string targetType, string targetId,
            string targetName, string? details = null, string? serverId = null, bool success = true) =>
            Hang();

        public Task<bool> TryLogAsync(string actor, string actorType, string action, string targetType, string targetId,
            string targetName, string? details = null, string? serverId = null, bool success = true) =>
            Hang();

        public Task<bool> TryLogAtAsync(DateTime occurredUtc, string actor, string actorType, string action,
            string targetType, string targetId, string targetName, string? details = null, string? serverId = null,
            bool success = true) =>
            Hang();

        private async Task<bool> Hang()
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite);
            return false;
        }

        public Task<List<AuditLogEntity>> GetRecentAsync(int count = 100, int offset = 0, string? actionFilter = null,
            string? targetTypeFilter = null) => Task.FromResult(new List<AuditLogEntity>());

        public Task<int> GetTotalCountAsync(string? actionFilter = null, string? targetTypeFilter = null) => Task.FromResult(0);
    }

    /// <summary>An audit store that loses every row, as a locked database or a full disk does.</summary>
    private sealed class DownAudit : IAuditLogService
    {
        public Task LogAsync(string actor, string actorType, string action, string targetType, string targetId,
            string targetName, string? details = null, string? serverId = null, bool success = true) => Task.CompletedTask;

        public Task<bool> TryLogAsync(string actor, string actorType, string action, string targetType, string targetId,
            string targetName, string? details = null, string? serverId = null, bool success = true) => Task.FromResult(false);

        public Task<List<AuditLogEntity>> GetRecentAsync(int count = 100, int offset = 0, string? actionFilter = null,
            string? targetTypeFilter = null) => Task.FromResult(new List<AuditLogEntity>());

        public Task<int> GetTotalCountAsync(string? actionFilter = null, string? targetTypeFilter = null) => Task.FromResult(0);
    }

    private static async Task BootAsync(Func<IServiceProvider, HttpClient, Task> body, AuditMode audit = AuditMode.Working)
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"sw-authaudit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        var pwFile = Path.Combine(dataDir, "admin-pw.txt");
        await File.WriteAllTextAsync(pwFile, Password);
        var prev = Environment.GetEnvironmentVariable("WHISKERS_DATA_DIR");
        Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", dataDir);

        var config = new List<KeyValuePair<string, string?>>
        {
            new("Auth:Disabled", "false"),
            new("WHISKERS_ADMIN_EMAIL", Email),
            new("WHISKERS_ADMIN_PASSWORD_FILE", pwFile),
        };
        config.AddRange(BootSettings.OnlyCore());

        try
        {
            using var bootSettings = BootSettings.Apply(config);   // read eagerly by Program
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Development");
                b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(config));
                if (audit == AuditMode.Down) b.ConfigureTestServices(s => s.AddSingleton<IAuditLogService, DownAudit>());
                if (audit == AuditMode.Stalled) b.ConfigureTestServices(s => s.AddSingleton<IAuditLogService, StalledAudit>());
            });
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            BootSettings.AssertBoot(factory.Services, config);
            await body(factory.Services, client);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", prev);
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
