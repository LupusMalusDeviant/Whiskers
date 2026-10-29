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

            var rows = await RowsAsync(services, "auth.");
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
            var row = Assert.Single(await RowsAsync(services, WhiskersPipelineExtensions.BackupDownloadAction));
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
        }, auditDown: true);

    private static async Task<List<AuditLogEntity>> RowsAsync(IServiceProvider services, string prefix) =>
        await services.GetRequiredService<IAuditLogService>().GetRecentAsync(500, actionFilter: prefix);

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

    private static async Task BootAsync(Func<IServiceProvider, HttpClient, Task> body, bool auditDown = false)
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
                if (auditDown) b.ConfigureTestServices(s => s.AddSingleton<IAuditLogService, DownAudit>());
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
