using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Whiskers.Modules;
using Whiskers.Services.Auth;

namespace Whiskers.Tests;

/// <summary>Audit TA-03 / TA-11 over the real pipeline: account lockout and the per-client rate limit on
/// /login-local, and rejection of a cookie that carries a synthetic (Auth:Disabled / agent) identity.</summary>
[Collection("WebAppBoot")] // serialized: the boot needs the process-wide WHISKERS_DATA_DIR env var
public sealed class LoginHardeningTests
{
    private const string Email = "admin@local.test";
    private const string Password = "CorrectHorse12!!";

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_and_the_client_is_then_rate_limited() =>
        await BootAsync(async (_, client) =>
        {
            var token = await AntiforgeryTokenAsync(client);
            for (var i = 0; i < 5; i++)
                Assert.Contains("error=invalid", (await LoginAsync(client, token, "wrong-password-" + i)).Headers.Location!.OriginalString);

            // Locked: even the right password is refused, with the same generic answer (no oracle).
            var locked = await LoginAsync(client, token, Password);
            Assert.Equal(HttpStatusCode.Redirect, locked.StatusCode);
            Assert.Contains("error=invalid", locked.Headers.Location!.OriginalString);
            Assert.DoesNotContain(locked.Headers, h => h.Key == "Set-Cookie" && h.Value.Any(v => v.Contains(".AspNetCore.Cookies=")));

            // 6 attempts so far; the limit is 10 per minute and client — the 11th is throttled.
            for (var i = 0; i < 4; i++) await LoginAsync(client, token, "x");
            Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginAsync(client, token, Password)).StatusCode);
        });

    [Theory]
    [InlineData(AuthConstants.AuthDisabledScheme)]
    [InlineData(AuthConstants.AgentSyntheticScheme)]
    public async Task A_cookie_carrying_a_synthetic_identity_is_rejected(string scheme) =>
        await BootAsync(async (factory, client) =>
        {
            // What an attacker holding the DataProtection key ring could mint: a valid cookie for the synthetic
            // Admin identity, which skips the whitelist. It must not be accepted when auth is enabled.
            var cookieOptions = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
                .Get(CookieAuthenticationDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "local"),
                new Claim(ClaimTypes.Email, "local@serverwatch.local"),
            }, scheme));
            var ticket = new AuthenticationTicket(principal, new AuthenticationProperties(), CookieAuthenticationDefaults.AuthenticationScheme);
            var cookie = cookieOptions.TicketDataFormat.Protect(ticket);

            var request = new HttpRequestMessage(HttpMethod.Get, "/settings");
            request.Headers.Add("Cookie", $".AspNetCore.Cookies={cookie}");
            request.Headers.Accept.ParseAdd("text/html");
            var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("/login", response.Headers.Location!.OriginalString);
        });

    private static async Task<string> AntiforgeryTokenAsync(HttpClient client)
    {
        var html = await (await client.GetAsync("/login")).Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token));
        return token;
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string token, string password) =>
        client.PostAsync("/login-local", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = Email, ["password"] = password, ["__RequestVerificationToken"] = token,
        }));

    private static async Task BootAsync(Func<WebApplicationFactory<Program>, HttpClient, Task> body)
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"sw-login-{Guid.NewGuid():N}");
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
            });
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            BootSettings.AssertBoot(factory.Services, config);
            await body(factory, client);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", prev);
            TestSqlite.ClearPools(dataDir);
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
