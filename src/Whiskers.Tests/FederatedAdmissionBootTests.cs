using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Whiskers.Models;
using Whiskers.Services.Auth;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>
/// R1-27 (contract 0003, decision 1 a; TA-23, A-12): the real app in Production with Google and OIDC configured and no
/// Admin yet admits no federated user â€” not at the sign-in gate, and a federated cookie minted with the app's own data
/// protection (with or without a binding claim, or of the kind issued before R1-27) reaches neither a <c>/mcp</c> tool call
/// nor the container hub. A local account the operator created keeps its fail-open path until setup.
/// </summary>
[Collection("WebAppBoot")] // serialized: the boot needs the process-wide WHISKERS_DATA_DIR env var
public class FederatedAdmissionBootTests
{
    private const string LocalEmail = "operator-made@corp.test";
    private const string Password = "CorrectHorse12!!";

    [Fact]
    public async Task An_unconfigured_instance_admits_no_federated_user_to_mcp_or_the_hub()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"sw-r127-boot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        var previous = Environment.GetEnvironmentVariable("WHISKERS_DATA_DIR");
        Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", dataDir);
        var config = new List<KeyValuePair<string, string?>>
        {
            new("Auth:Disabled", "false"),
            new("GoogleAuth:ClientId", "google-client"), new("GoogleAuth:ClientSecret", "test-only"),
            new("Oidc:Enabled", "true"), new("Oidc:Authority", "https://id.example.com"),
            new("Oidc:ClientId", "whiskers"), new("Oidc:ClientSecret", "test-only"),
            new("WHISKERS_DISABLE_LOCAL_DOCKER", "true"),
        };
        try
        {
            using var settings = BootSettings.Apply(config);
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Production");
                b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(config));
            });
            using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/healthz")).StatusCode);
            var roles = factory.Services.GetRequiredService<IRoleService>();
            Assert.DoesNotContain(roles.GetRoleData().Roles, r => r.Role == AppRole.Admin);   // setup not finished

            // The gate: a verified Google user is refused, nothing is recorded for them.
            var admission = factory.Services.GetRequiredService<IFederatedAdmission>();
            var decision = await admission.AdmitAsync(OidcIdentityBindingTests.Google("g-anyone", "anyone@gmail.test"));
            Assert.Equal(AuthAuditReasons.SetupIncomplete, decision.Reason);
            using (var scope = factory.Services.CreateScope())
            {
                var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
                var ticket = OidcIdentityBindingTests.Ticket(http, "Google", new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "g-anyone"), new Claim(ClaimTypes.Email, "anyone@gmail.test"),
                    new Claim(AuthConstants.EmailVerifiedClaim, "true"),
                ], "Google"));
                await FederatedSignIn.WhitelistGateAsync(ticket, "");
                Assert.True(ticket.Result!.Handled);
                Assert.Equal("/login?error=unauthorized", http.Response.Headers.Location.ToString());
            }
            Assert.Empty(factory.Services.GetRequiredService<IIdentityBindingStore>().Snapshot().Pending);

            // The operator's local account (no role): the fail-open path before setup is unchanged.
            using (var scope = factory.Services.CreateScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
                var created = await users.CreateAsync(new AppUser { UserName = LocalEmail, Email = LocalEmail }, Password);
                Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Code)));
            }
            using var local = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            await SignInLocallyAsync(local);
            Assert.Equal(HttpStatusCode.OK, (await NegotiateAsync(local, cookie: null)).StatusCode);

            // Positive control for the minting below: a local cookie minted the same way reaches the hub.
            AppUser row;
            using (var scope = factory.Services.CreateScope())
                row = (await scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().FindByEmailAsync(LocalEmail))!;
            var minted = Mint(factory.Services, new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Email, LocalEmail), new Claim(ClaimTypes.Name, LocalEmail),
                new Claim(ClaimTypes.NameIdentifier, row.Id), new Claim(AuthConstants.SecurityStampClaim, row.SecurityStamp!),
            ], AuthConstants.LocalPasswordScheme));
            Assert.Equal(HttpStatusCode.OK, (await NegotiateAsync(anonymous, minted)).StatusCode);

            // Federated cookies with the app's data protection: with a binding claim, without, and pre-R1-27.
            foreach (var identity in new[]
                     {
                         new ClaimsIdentity([new Claim(ClaimTypes.Email, "anyone@gmail.test"),
                             new Claim(AuthConstants.BindingIdClaim, "b_" + new string('a', 32))], AuthConstants.FederatedScheme),
                         new ClaimsIdentity([new Claim(ClaimTypes.Email, "anyone@gmail.test")], AuthConstants.FederatedScheme),
                         new ClaimsIdentity([new Claim(ClaimTypes.Email, "anyone@gmail.test")], "Google"),
                         new ClaimsIdentity([new Claim(ClaimTypes.Email, LocalEmail)], "oidc"),
                     })
            {
                var cookie = Mint(factory.Services, identity);
                var negotiate = await NegotiateAsync(anonymous, cookie);
                Assert.NotEqual(HttpStatusCode.OK, negotiate.StatusCode);
                var mcp = await McpToolCallAsync(anonymous, cookie);
                Assert.True(mcp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Redirect or HttpStatusCode.Forbidden,
                    $"{identity.AuthenticationType}: /mcp answered {(int)mcp.StatusCode}");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", previous);
            TestSqlite.ClearPools(dataDir);
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>The app's own cookie for <paramref name="identity"/>, as the cookie handler would write it.</summary>
    private static string Mint(IServiceProvider services, ClaimsIdentity identity)
    {
        var options = services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), new AuthenticationProperties
        {
            IssuedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1),
        }, CookieAuthenticationDefaults.AuthenticationScheme);
        return $"{options.Cookie.Name ?? ".AspNetCore.Cookies"}={options.TicketDataFormat.Protect(ticket)}";
    }

    private static Task<HttpResponseMessage> NegotiateAsync(HttpClient client, string? cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/hubs/containers/negotiate?negotiateVersion=1");
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        return client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> McpToolCallAsync(HttpClient client, string cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"list_containers","arguments":{}}}""",
                Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Add("Cookie", cookie);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
    }

    private static async Task SignInLocallyAsync(HttpClient client)
    {
        var html = await (await client.GetAsync("/login")).Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token), "the sign-in page carries no antiforgery token");
        var response = await client.PostAsync("/login-local", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = LocalEmail, ["password"] = Password, ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.DoesNotContain("error", response.Headers.Location!.OriginalString);
    }
}
