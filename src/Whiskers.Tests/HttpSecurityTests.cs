using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Utils;

namespace Whiskers.Tests;

/// <summary>R1-50 (A-13–A-16) over the real pipeline: the response headers every answer carries, the session cookie's
/// flags, sign-out only by a POST with the antiforgery token, MCP's 401 for API clients, local-only redirects, and
/// proxy trust — a client that is not a trusted proxy cannot move the login rate limit with X-Forwarded-For, a configured
/// proxy can forward its clients' addresses, and an unreadable trusted-proxy list refuses the start.</summary>
[Collection("WebAppBoot")] // serialized: the boot needs the process-wide WHISKERS_DATA_DIR env var
public sealed class HttpSecurityTests
{
    private const string Email = "admin@local.test";
    private const string Password = "CorrectHorse12!!";

    /// <summary>The test server's connection has no address; this request header sets it (as a real socket would).</summary>
    private const string RemoteHeader = "X-Test-Remote-Address";

    [Fact]
    public async Task Every_answer_forbids_framing_and_sniffing_and_keeps_the_referrer_on_the_site() =>
        await BootAsync(async (_, client) =>
        {
            foreach (var path in new[] { "/login", "/healthz", "/mcp", "/settings", "/does-not-exist" })
            {
                var response = await client.GetAsync(path);
                Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
                Assert.Equal("DENY", Header(response, "X-Frame-Options"));
                Assert.Equal("frame-ancestors 'none'", Header(response, "Content-Security-Policy"));
                Assert.Equal("same-origin", Header(response, "Referrer-Policy"));
            }
        });

    [Theory]
    [InlineData("http://localhost", false)]
    [InlineData("https://localhost", true)]
    public async Task The_session_cookie_is_http_only_lax_and_secure_over_https(string baseAddress, bool secure) =>
        await BootAsync(async (factory, _) =>
        {
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false, BaseAddress = new Uri(baseAddress),
            });
            var token = await AntiforgeryTokenAsync(client, "/login");

            var response = await LoginAsync(client, token, Email, Password);

            var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), v => v.StartsWith(".AspNetCore.Cookies=", StringComparison.Ordinal));
            Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(secure, cookie.Contains("secure", StringComparison.OrdinalIgnoreCase));
        });

    [Fact]
    public async Task Only_a_post_with_the_antiforgery_token_signs_out() =>
        await BootAsync(async (_, client) =>
        {
            await LoginAsync(client, await AntiforgeryTokenAsync(client, "/login"), Email, Password);
            Assert.True(await SignedInAsync(client));

            // A GET — a link or an image another site embeds — shows a form and ends nothing.
            var page = await client.GetAsync("/logout");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.True(await SignedInAsync(client));

            // A POST without the token (a cross-site form) is refused and ends nothing.
            var forged = await client.PostAsync("/logout", new FormUrlEncodedContent(new Dictionary<string, string>()));
            Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
            Assert.True(await SignedInAsync(client));

            // The form's own POST ends the session.
            var token = Regex.Match(await page.Content.ReadAsStringAsync(),
                "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
            var signedOut = await client.PostAsync("/logout", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token),
            }));
            Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode);
            Assert.EndsWith("/login", signedOut.Headers.Location!.OriginalString);
            Assert.False(await SignedInAsync(client));
        });

    [Fact]
    public async Task An_anonymous_mcp_call_gets_401_with_a_bearer_challenge_not_a_sign_in_redirect() =>
        await BootAsync(async (_, client) =>
        {
            var response = await client.PostAsync("/mcp", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Null(response.Headers.Location);
            Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
        });

    [Theory]
    [InlineData("/servers", "/servers")]
    [InlineData("//evil.example/x", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("https://evil.example/", "/")]
    [InlineData("javascript:alert(1)", "/")]
    public async Task The_language_switch_follows_only_a_local_path(string redirect, string expected) =>
        await BootAsync(async (_, client) =>
        {
            var response = await client.GetAsync("/set-culture?culture=de&redirect=" + Uri.EscapeDataString(redirect));

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal(expected, response.Headers.Location!.OriginalString);
        });

    [Fact]
    public async Task A_client_that_is_not_a_trusted_proxy_cannot_move_the_login_rate_limit_with_x_forwarded_for() =>
        await BootAsync(async (factory, client) =>
        {
            var token = await AntiforgeryTokenAsync(client, "/login");

            // Ten attempts per minute and client. A spoofing client claims a new address on every attempt.
            HttpResponseMessage? last = null;
            for (var i = 0; i < 11; i++)
                last = await LoginAsync(client, token, $"nobody{i}@local.test", "wrong", from: "198.51.100.20", forwardedFor: $"203.0.113.{i + 1}");

            Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
            // The ignored headers are named for the operator (a proxy to configure, or a spoofer).
            Assert.Equal("198.51.100.20", factory.Services.GetRequiredService<ForwardedHeadersObserver>().LastUntrustedPeer?.Peer);
        });

    [Fact]
    public async Task A_configured_proxy_forwards_its_clients_addresses() =>
        await BootAsync(async (factory, client) =>
        {
            var token = await AntiforgeryTokenAsync(client, "/login");

            // Twelve clients behind the configured proxy: each has its own limit, none is throttled.
            for (var i = 0; i < 12; i++)
            {
                var response = await LoginAsync(client, token, $"nobody{i}@local.test", "wrong", from: "10.0.0.5", forwardedFor: $"203.0.113.{i + 1}");
                Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
            }
            Assert.Null(factory.Services.GetRequiredService<ForwardedHeadersObserver>().LastUntrustedPeer);
        }, new KeyValuePair<string, string?>("ForwardedHeaders:TrustedNetworks:0", "10.0.0.0/8"));

    [Fact]
    public async Task An_unreadable_trusted_proxy_list_refuses_the_start()
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            BootAsync((_, _) => Task.CompletedTask, new KeyValuePair<string, string?>("ForwardedHeaders:TrustedNetworks:0", "proxy.local")));

        var inner = ex;
        while (inner is not InvalidOperationException && inner.InnerException is not null) inner = inner.InnerException;
        Assert.Contains("proxy.local", inner.Message);
    }

    // ------------------------------------------------------------------------------------------------ helpers

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values)
        : response.Content.Headers.TryGetValues(name, out var content) ? string.Join(",", content) : null;

    private static async Task<bool> SignedInAsync(HttpClient client)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/settings");
        request.Headers.Accept.ParseAdd("text/html");
        var response = await client.SendAsync(request);
        return response.StatusCode == HttpStatusCode.OK;
    }

    private static async Task<string> AntiforgeryTokenAsync(HttpClient client, string path)
    {
        var html = await (await client.GetAsync(path)).Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token));
        return token;
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string token, string email, string password,
        string? from = null, string? forwardedFor = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/login-local")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["email"] = email, ["password"] = password, ["__RequestVerificationToken"] = token,
            }),
        };
        if (from is not null) request.Headers.Add(RemoteHeader, from);
        if (forwardedFor is not null) request.Headers.Add("X-Forwarded-For", forwardedFor);
        return client.SendAsync(request);
    }

    private static async Task BootAsync(Func<WebApplicationFactory<Program>, HttpClient, Task> body,
        params KeyValuePair<string, string?>[] extra)
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"sw-http-{Guid.NewGuid():N}");
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
        config.AddRange(extra);
        config.AddRange(BootSettings.OnlyCore());

        try
        {
            using var bootSettings = BootSettings.Apply(config);   // read eagerly by Program
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Development");
                b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(config));
                b.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(new RemoteAddressFromHeader()));
            });
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            await body(factory, client);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", prev);
            TestSqlite.ClearPools(dataDir);
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private sealed class RemoteAddressFromHeader : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue(RemoteHeader, out var value)
                    && IPAddress.TryParse(value.ToString(), out var address))
                    context.Connection.RemoteIpAddress = address;
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
