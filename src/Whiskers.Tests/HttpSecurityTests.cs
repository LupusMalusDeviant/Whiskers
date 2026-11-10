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
            // The attribute, not the (encrypted) value: "; secure".
            Assert.Equal(secure, cookie.Split(';').Skip(1).Any(a => a.Trim().Equals("secure", StringComparison.OrdinalIgnoreCase)));
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

    // CodeQL #86 (approved 2026-11-10): the language cookie is HttpOnly and Lax always, Secure over https.
    [Theory]
    [InlineData("http://localhost", false)]
    [InlineData("https://localhost", true)]
    public async Task The_language_cookie_is_http_only_lax_and_secure_over_https(string baseAddress, bool secure) =>
        await BootAsync(async (factory, _) =>
        {
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false, BaseAddress = new Uri(baseAddress),
            });

            var response = await client.GetAsync("/set-culture?culture=de&redirect=%2F");

            var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"),
                v => v.StartsWith(".AspNetCore.Culture=", StringComparison.Ordinal));
            Assert.True(HasAttribute(cookie, "httponly"));
            Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(secure, HasAttribute(cookie, "secure"));
        });

    // CodeQL #60/#61 (approved 2026-11-10): with RequireHttpsMetadata=false the OIDC nonce and correlation cookies follow
    // the request's scheme — Secure over https, without over plain http (the LAN case keeps working).
    [Theory]
    [InlineData("http://localhost", false)]
    [InlineData("https://localhost", true)]
    public async Task Oidc_nonce_and_correlation_cookies_follow_the_scheme_without_https_metadata(string baseAddress, bool secure) =>
        await BootAsync(async (factory, _) =>
        {
            // A fixed IdP configuration: the challenge needs no discovery document (no network).
            await using var oidc = factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
                services.PostConfigure<Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions>("oidc", o =>
                {
                    o.Configuration = new Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration
                    {
                        AuthorizationEndpoint = "http://idp.local.test/authorize",
                        Issuer = "http://idp.local.test",
                    };
                    o.ConfigurationManager = new Microsoft.IdentityModel.Protocols.StaticConfigurationManager<
                        Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration>(o.Configuration);
                })));
            using var client = oidc.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false, BaseAddress = new Uri(baseAddress),
            });

            var response = await client.GetAsync("/login-oidc");

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            var cookies = response.Headers.GetValues("Set-Cookie").ToList();
            var correlation = Assert.Single(cookies, v => v.StartsWith(".AspNetCore.Correlation.", StringComparison.Ordinal));
            var nonce = Assert.Single(cookies, v => v.StartsWith(".AspNetCore.OpenIdConnect.Nonce.", StringComparison.Ordinal));
            Assert.Equal(secure, HasAttribute(correlation, "secure"));
            Assert.Equal(secure, HasAttribute(nonce, "secure"));
        },
        new KeyValuePair<string, string?>("Oidc:Enabled", "true"),
        new KeyValuePair<string, string?>("Oidc:Authority", "http://idp.local.test"),
        new KeyValuePair<string, string?>("Oidc:ClientId", "whiskers"),
        new KeyValuePair<string, string?>("Oidc:ClientSecret", "test-client-secret"),
        new KeyValuePair<string, string?>("Oidc:RequireHttpsMetadata", "false"));

    /// <summary>Whether a Set-Cookie value carries the attribute (not the value): "; secure".</summary>
    private static bool HasAttribute(string cookie, string attribute) =>
        cookie.Split(';').Skip(1).Any(a => a.Trim().Equals(attribute, StringComparison.OrdinalIgnoreCase));

    [Theory]
    [InlineData("192.168.1.50")]   // a LAN client: trusted as a proxy before R1-50
    [InlineData("::ffff:10.0.0.5")]   // the same over an IPv4-mapped IPv6 socket
    [InlineData("100.100.1.2")]      // a tailnet client
    public async Task A_client_that_is_not_a_trusted_proxy_cannot_move_the_login_rate_limit_with_x_forwarded_for(string peer) =>
        await BootAsync(async (factory, client) =>
        {
            var token = await AntiforgeryTokenAsync(client, "/login");

            // Ten attempts per minute and client. A spoofing client claims a new address on every attempt.
            HttpResponseMessage? last = null;
            for (var i = 0; i < 11; i++)
                last = await LoginAsync(client, token, $"nobody{i}@local.test", "wrong", from: peer, forwardedFor: $"203.0.113.{i + 1}");

            Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
            // The ignored headers are named for the operator (a proxy to configure, or a spoofer).
            var seen = Assert.Single(factory.Services.GetRequiredService<ForwardedHeadersObserver>().RecentUntrustedPeers);
            Assert.Equal(IPAddress.Parse(peer).ToString(), seen.Peer);
        });

    [Theory]
    // An unlisted proxy (or a LAN client) whose own address is in the gate's private ranges: refused, not let through
    // with the proxy's address standing in for a public client.
    [InlineData("172.18.0.5", "203.0.113.9", null, HttpStatusCode.Forbidden)]
    [InlineData("192.168.1.50", "127.0.0.1", null, HttpStatusCode.Forbidden)]
    // Without forwarded headers a LAN client is served, as before.
    [InlineData("172.18.0.5", null, null, HttpStatusCode.OK)]
    // A listed proxy: its clients' addresses decide — a public one is refused, a LAN one is served.
    [InlineData("10.0.0.5", "203.0.113.9", "10.0.0.0/8", HttpStatusCode.Forbidden)]
    [InlineData("10.0.0.5", "192.168.1.7", "10.0.0.0/8", HttpStatusCode.OK)]
    // The default proxy (loopback, also IPv4-mapped) is honoured: its public client is refused, its LAN client served.
    [InlineData("127.0.0.1", "203.0.113.9", null, HttpStatusCode.Forbidden)]
    [InlineData("::ffff:127.0.0.1", "192.168.1.7", null, HttpStatusCode.OK)]
    public async Task Auth_disabled_serves_only_the_trusted_network_also_behind_a_proxy(
        string peer, string? forwardedFor, string? trusted, HttpStatusCode expected)
    {
        var extra = new List<KeyValuePair<string, string?>> { new("Auth:Disabled", "true") };
        if (trusted is not null) extra.Add(new("ForwardedHeaders:TrustedNetworks:0", trusted));
        await BootAsync(async (_, client) =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
            request.Headers.Add(RemoteHeader, peer);
            if (forwardedFor is not null) request.Headers.Add("X-Forwarded-For", forwardedFor);

            var response = await client.SendAsync(request);

            Assert.Equal(expected, response.StatusCode);
            if (expected == HttpStatusCode.Forbidden)
            {
                // Which refusal: headers ignored (an untrusted peer) or the resolved client outside the allowed networks.
                var body = await response.Content.ReadAsStringAsync();
                var forwardedRefusal = trusted is null && !ForwardedHeadersConfig.IsTrusted(
                    ForwardedHeadersConfig.ParseTrustedNetworks(null), IPAddress.Parse(peer));
                Assert.Contains(forwardedRefusal ? "ForwardedHeaders:TrustedNetworks" : "Auth:DisabledAllowedNetworks", body);
                Assert.DoesNotContain(forwardedRefusal ? "Auth:DisabledAllowedNetworks" : "ForwardedHeaders:TrustedNetworks", body);
            }
        }, extra.ToArray());
    }

    [Fact]
    public async Task The_layout_sign_out_button_posts_a_token_that_signs_out() =>
        await BootAsync(async (_, client) =>
        {
            await LoginAsync(client, await AntiforgeryTokenAsync(client, "/login"), Email, Password);

            // The button every page shows: the layout's form, its token rendered with the page.
            var html = await (await client.GetAsync("/settings")).Content.ReadAsStringAsync();
            var form = Regex.Match(html, "<form method=\"post\" action=\"logout\".*?</form>", RegexOptions.Singleline).Value;
            Assert.Contains("type=\"submit\"", form);
            var token = Regex.Match(form, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
            Assert.False(string.IsNullOrEmpty(token));

            var signedOut = await client.PostAsync("/logout", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token),
            }));

            Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode);
            Assert.EndsWith("/login", signedOut.Headers.Location!.OriginalString);
            Assert.False(await SignedInAsync(client));
        });

    [Fact]
    public async Task A_sign_out_after_the_session_ended_goes_to_the_sign_in_page_not_a_bare_400() =>
        await BootAsync(async (_, client) =>
        {
            // A tab left open: the form's token belongs to a session that is gone (here: no session at all).
            var stale = await client.PostAsync("/logout", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = "stale",
            }));

            Assert.Equal(HttpStatusCode.Redirect, stale.StatusCode);
            Assert.EndsWith("/login", stale.Headers.Location!.OriginalString);
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
            Assert.Empty(factory.Services.GetRequiredService<ForwardedHeadersObserver>().RecentUntrustedPeers);
        }, new KeyValuePair<string, string?>("ForwardedHeaders:TrustedNetworks:0", "10.0.0.0/8"));

    [Fact]
    public async Task The_frameworks_trust_all_forwarded_headers_switch_refuses_the_start()
    {
        // ASPNETCORE_FORWARDEDHEADERS_ENABLED=true would add a step ahead of the pipeline that trusts every peer.
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => BootAsync((_, client) => client.GetAsync("/healthz"),
            new KeyValuePair<string, string?>("ASPNETCORE_FORWARDEDHEADERS_ENABLED", "true")));

        var inner = ex;
        while (inner is not InvalidOperationException && inner.InnerException is not null) inner = inner.InnerException;
        Assert.Contains("ASPNETCORE_FORWARDEDHEADERS_ENABLED", inner.Message);
    }

    [Fact]
    public async Task A_request_without_a_peer_address_is_not_marked_as_untrusted() =>
        await BootAsync(async (factory, client) =>
        {
            // A local transport (Unix socket; here the in-memory test server) has no peer address: the forwarded-headers
            // middleware applies the headers, so the request is neither marked nor reported.
            var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
            request.Headers.Add("X-Forwarded-For", "203.0.113.9");

            var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Empty(factory.Services.GetRequiredService<ForwardedHeadersObserver>().RecentUntrustedPeers);
        });

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
        config.AddRange(BootSettings.OnlyCore());
        // A setting in `extra` replaces the default of the same name (Auth:Disabled for the gate tests).
        config = config.Where(c => !extra.Any(e => e.Key == c.Key)).Concat(extra).ToList();

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
