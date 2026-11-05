using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticAssets;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Whiskers.Models;
using Whiskers.Services.Auth;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>
/// R1-47 (TA-25, A-18, INV-17): the HTTP authorization matrix of the real application. The app is booted in the
/// Production environment with real authentication; every endpoint its endpoint data sources publish — minimal APIs,
/// Razor component pages, SignalR hubs, the Blazor framework endpoints, MCP, health probes and static assets — is
/// requested as an anonymous caller and as a signed-in Viewer, Operator and Admin. For each request the test records
/// whether the endpoint's handler actually ran (the endpoint middleware's "executing endpoint" event, tagged with a
/// per-request probe id), not only which status came back: a denied request must never reach the handler, an allowed
/// one must reach it.
///
/// <para>Every endpoint must be classified in <see cref="Expected"/>. An endpoint added to the app without a
/// classification fails <see cref="Every_endpoint_is_classified_and_every_classification_exists"/> until someone decides
/// who may call it; a classification whose endpoint is gone fails it too. Static assets are classified by rule (the
/// endpoint carries a <see cref="StaticAssetDescriptor"/>), nothing else is.</para>
///
/// <para>The matrix is the HTTP layer. Pages are reachable for every signed-in role on purpose: their role checks run
/// inside the handlers (INV-17) and are pinned by <c>UiMutationGatewayCoverageTests</c> and the gateway tests.</para>
/// </summary>
[Collection("WebAppBoot")]
public sealed class EndpointAuthMatrixTests(EndpointAuthMatrixTests.App app) : IClassFixture<EndpointAuthMatrixTests.App>
{
    /// <summary>Who may reach an endpoint's handler.</summary>
    public enum Access
    {
        /// <summary>Anyone, signed in or not.</summary>
        Public,
        /// <summary>Anyone at the HTTP layer; the handler itself checks a token, signature or form (documented per row).</summary>
        PublicHandlerGated,
        /// <summary>Any signed-in user; anonymous callers are challenged before the handler.</summary>
        SignedIn,
        /// <summary>Admins only; anonymous callers are challenged and Viewers/Operators refused before the handler.</summary>
        Admin,
        /// <summary>Nobody: the route is closed in this state (the setup wizard once an Admin exists) and redirects away.</summary>
        Closed,
    }

    /// <summary>How an anonymous caller is turned away from a protected endpoint.</summary>
    public enum Challenge
    {
        /// <summary>A redirect to the sign-in page (browser endpoints).</summary>
        LoginRedirect,
        /// <summary>401 without a redirect (hubs: API clients).</summary>
        Unauthorized,
    }

    /// <param name="Access">Who may reach the handler.</param>
    /// <param name="Challenge">How an anonymous caller is turned away.</param>
    /// <param name="Why">What the endpoint is, and why it has this access.</param>
    /// <param name="KnownServerError">A recorded 5xx answer of an allowed call, with the package that owns it. The
    /// handler must still run; the row fails as soon as the answer is no longer a 5xx, so the note cannot go stale.</param>
    /// <remarks>There is no opt-out of the handler proof: every allowed call must reach its handler.</remarks>
    public sealed record Rule(Access Access, Challenge Challenge = Challenge.LoginRedirect, string Why = "",
        string? KnownServerError = null);

    /// <summary>The classification of every non-static endpoint, keyed by "METHODS pattern" (<see cref="KeyOf"/>).</summary>
    internal static readonly IReadOnlyDictionary<string, Rule> Expected = new Dictionary<string, Rule>(StringComparer.Ordinal)
    {
        // Probes and framework plumbing.
        ["* /healthz"] = new(Access.Public, Why: "liveness probe, status word only"),
        ["* /readyz"] = new(Access.Public, Why: "readiness probe, status word only"),
        ["* /_blazor"] = new(Access.Public, Why: "Blazor circuit; carries no content, pages authorize what they render"),
        ["* /_blazor/negotiate"] = new(Access.Public, Why: "Blazor circuit negotiation"),
        ["* /_blazor/disconnect/"] = new(Access.Public, Why: "Blazor circuit disconnect"),
        ["* /_blazor/initializers/"] = new(Access.Public, Why: "Blazor JS initializers"),
        ["GET /_framework/opaque-redirect"] = new(Access.SignedIn, Why: "enhanced-navigation redirect helper; behind the fallback policy"),
        // MapStaticAssets serves wwwroot files missing from a *build* manifest (dotnet run, this test host) through a file
        // fallback whose route only matches a file that exists; the published image uses the publish manifest. Behind the
        // fallback policy. The fixture arranges a real file only this fallback can serve (App.FallbackProbeFile), so its
        // handler run is proven like every other endpoint's.
        ["GET,HEAD {**path:file}"] = new(Access.SignedIn, Why: "static-asset fallback of the build manifest"),

        // Sign-in and session.
        ["GET /set-culture"] = new(Access.Public, Why: "language cookie, local redirect only"),
        // R1-50: a provider that is not configured (as in this host) answers 404 from the handler, no longer a 500.
        ["GET /login-google"] = new(Access.Public, Why: "starts the Google challenge; 404 when Google is not configured"),
        ["GET /login-oidc"] = new(Access.Public, Why: "starts the OIDC challenge; 404 when OIDC is not configured"),
        ["POST /login-local"] = new(Access.PublicHandlerGated, Why: "antiforgery form + password check + rate limit"),
        // R1-50 (A-16): a GET only shows the sign-out form; the POST ends the caller's own cookie, with the antiforgery token.
        ["GET /logout"] = new(Access.Public, Why: "sign-out form (ends nothing)"),
        ["POST /logout"] = new(Access.PublicHandlerGated, Why: "ends the caller's own cookie; antiforgery token checked by the handler"),

        // Pages that must work before sign-in.
        ["GET,POST /login"] = new(Access.Public, Why: "sign-in page"),
        ["GET,POST /setup"] = new(Access.Closed, Why: "first-run wizard: a dead route (redirect to /) once an Admin exists"),
        ["GET,POST /Error"] = new(Access.Public, Why: "error page"),
        ["GET,POST /account/recover"] = new(Access.PublicHandlerGated, Why: "one-time recovery code (R1-49)"),

        // Token-gated machine endpoints.
        ["POST /api/webhooks/{webhookId}"] = new(Access.PublicHandlerGated, Why: "HMAC signature + freshness + delivery id (INV-42)"),
        ["GET /metrics"] = new(Access.PublicHandlerGated, Why: "scrape token; disabled (404) without one"),

        // API clients.
        ["* /hubs/containers"] = new(Access.SignedIn, Challenge.Unauthorized, "container stats hub"),
        ["* /hubs/containers/negotiate"] = new(Access.SignedIn, Challenge.Unauthorized, "container stats hub negotiation"),
        // MCP: an API key (Bearer) or a signed-in web user; per-tool levels inside (McpPermissionCheck). Without either an
        // MCP client gets 401 with a Bearer challenge, not a sign-in redirect (R1-50).
        ["POST /mcp/"] = new(Access.SignedIn, Challenge.Unauthorized, "MCP calls"),
        ["GET /mcp/"] = new(Access.SignedIn, Challenge.Unauthorized, "MCP stream"),
        ["DELETE /mcp/"] = new(Access.SignedIn, Challenge.Unauthorized, "MCP session end"),

        // Admin-only downloads.
        ["GET /backups/self/{id}/download"] = new(Access.Admin, Why: "self-backup archive (secret-bearing)"),
        ["GET /audit/export"] = new(Access.Admin, Why: "whole audit trail"),

        // Pages behind the global [Authorize]; their role checks run inside the handlers.
        ["GET,POST /"] = new(Access.SignedIn),
        ["GET,POST /account"] = new(Access.SignedIn),
        ["GET,POST /agent"] = new(Access.SignedIn),
        ["GET,POST /agent-history"] = new(Access.SignedIn),
        ["GET,POST /ai-operations"] = new(Access.SignedIn),
        ["GET,POST /ai-triggers"] = new(Access.SignedIn),
        ["GET,POST /approvals"] = new(Access.SignedIn),
        ["GET,POST /apps"] = new(Access.SignedIn),
        ["GET,POST /audit-log"] = new(Access.SignedIn),
        ["GET,POST /backups"] = new(Access.SignedIn),
        ["GET,POST /cloud"] = new(Access.SignedIn),
        ["GET,POST /compose"] = new(Access.SignedIn),
        ["GET,POST /container/{ContainerId}"] = new(Access.SignedIn),
        ["GET,POST /container/{ServerId}/{ContainerId}"] = new(Access.SignedIn),
        ["GET,POST /cves"] = new(Access.SignedIn),
        ["GET,POST /deploy"] = new(Access.SignedIn),
        ["GET,POST /diff"] = new(Access.SignedIn),
        ["GET,POST /firewall/{ServerId}"] = new(Access.SignedIn),
        ["GET,POST /git-deploy"] = new(Access.SignedIn),
        ["GET,POST /graph"] = new(Access.SignedIn),
        ["GET,POST /guardrails"] = new(Access.SignedIn),
        ["GET,POST /health"] = new(Access.SignedIn),
        ["GET,POST /hello-world"] = new(Access.SignedIn),
        ["GET,POST /help"] = new(Access.SignedIn),
        ["GET,POST /logs"] = new(Access.SignedIn),
        ["GET,POST /networks"] = new(Access.SignedIn),
        ["GET,POST /nginx/{ServerId}"] = new(Access.SignedIn),
        ["GET,POST /notifications"] = new(Access.SignedIn),
        ["GET,POST /self-status"] = new(Access.SignedIn),
        ["GET,POST /server-terminal/{ServerId}"] = new(Access.SignedIn),
        ["GET,POST /servers"] = new(Access.SignedIn),
        ["GET,POST /services/{ServerId}"] = new(Access.SignedIn),
        ["GET,POST /settings"] = new(Access.SignedIn),
        ["GET,POST /ssl/{ServerId}"] = new(Access.SignedIn),
        ["GET,POST /tasks"] = new(Access.SignedIn),
        ["GET,POST /terminal/{ServerId}/{ContainerId}"] = new(Access.SignedIn),
        ["GET,POST /webhooks"] = new(Access.SignedIn),
    };

    /// <summary>Endpoints requested last for each caller: they could end its session. (The matrix posts no antiforgery
    /// token, so <c>POST /logout</c> refuses and ends nothing; it stays last regardless.)</summary>
    private static readonly HashSet<string> SessionEnding = new(StringComparer.Ordinal) { "POST /logout" };

    // ------------------------------------------------------------------------------------------------ the tests

    [Fact]
    public void Every_endpoint_is_classified_and_every_classification_exists()
    {
        var problems = Classify(app.Endpoints, Expected, out var classified);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        // The enumeration really saw the surface: pages, hubs, MCP, minimal APIs and static assets.
        Assert.Contains(classified, c => c.Key == "GET,POST /settings");
        Assert.Contains(classified, c => c.Key == "POST /mcp/");
        Assert.Contains(classified, c => c.Key == "* /hubs/containers");
        Assert.Contains(classified, c => c.Static);
    }

    [Fact]
    public void An_unclassified_endpoint_or_a_stale_classification_is_caught()
    {
        var real = app.Endpoints;
        var unknown = new RouteEndpoint(_ => Task.CompletedTask,
            Microsoft.AspNetCore.Routing.Patterns.RoutePatternFactory.Parse("/new-admin-endpoint"), 0,
            new EndpointMetadataCollection(new HttpMethodMetadata(["POST"])), "new endpoint");
        Assert.Contains(Classify([.. real, unknown], Expected, out _), p => p.Contains("POST /new-admin-endpoint"));

        var stale = new Dictionary<string, Rule>(Expected, StringComparer.Ordinal) { ["GET /removed"] = new(Access.Admin) };
        Assert.Contains(Classify(real, stale, out _), p => p.Contains("GET /removed"));

        // A static-looking path without the static-asset descriptor is not waved through as a static asset.
        var fakeAsset = new RouteEndpoint(_ => Task.CompletedTask,
            Microsoft.AspNetCore.Routing.Patterns.RoutePatternFactory.Parse("leak.css"), 0,
            new EndpointMetadataCollection(new HttpMethodMetadata(["GET", "HEAD"])), null);
        Assert.Contains(Classify([.. real, fakeAsset], Expected, out _), p => p.Contains("leak.css"));
    }

    [Fact]
    public async Task An_anonymous_static_fallback_fails_the_matrix()
    {
        // The real fallback is probed with a file that exists only for it (the main matrix requires its handler to run).
        Assert.Empty(Classify(app.Endpoints, Expected, out var real));
        var fallbackKey = $"GET,HEAD {App.StaticFallbackPattern}";
        Assert.Single(real, c => c.Key == fallbackKey);

        // The same app with that one endpoint opened to anonymous callers: the matrix must fail on it.
        var mutated = new App(anonymousStaticFallback: true);
        await mutated.InitializeAsync();
        try
        {
            Assert.Empty(Classify(mutated.Endpoints, Expected, out var classified));
            var fallback = Assert.Single(classified, c => c.Key == fallbackKey);
            var anonymous = mutated.Callers.Single(c => c.Role is null);
            var violations = new List<string>();
            foreach (var method in fallback.ProbeMethods)
                violations.AddRange(Judge(null, fallback, method, await mutated.ProbeAsync(anonymous, fallback, method)));
            Assert.Contains(violations, v => v.Contains("must be refused before the handler, but the handler ran", StringComparison.Ordinal));
        }
        finally
        {
            await mutated.DisposeAsync();
        }
    }

    [Fact]
    public void Two_endpoints_on_one_route_and_method_are_classified_and_probed_apart()
    {
        static RouteEndpoint Twin(string name, string host) => new(_ => Task.CompletedTask,
            Microsoft.AspNetCore.Routing.Patterns.RoutePatternFactory.Parse("/twin"), 0,
            new EndpointMetadataCollection(new HttpMethodMetadata(["GET"]), new HostAttribute(host)), name);
        var forAdmins = Twin("twin for admins", "admin.example");
        var forAll = Twin("twin for everyone", "public.example");
        var surface = new List<Endpoint>([.. app.Endpoints, forAdmins, forAll]);

        // Classifying one leaves the other unclassified: neither inherits the other's rule.
        var oneRule = new Dictionary<string, Rule>(Expected, StringComparer.Ordinal) { [DistinctKeyOf(forAdmins)] = new(Access.Admin) };
        var problems = Classify(surface, oneRule, out var classified);
        Assert.Contains(problems, p => p.Contains(DistinctKeyOf(forAll), StringComparison.Ordinal));
        Assert.DoesNotContain(problems, p => p.Contains(DistinctKeyOf(forAdmins), StringComparison.Ordinal));
        var unit = Assert.Single(classified, c => c.Key == DistinctKeyOf(forAdmins));
        Assert.Same(forAdmins, Assert.Single(unit.Endpoints));

        // The shared route alone classifies neither of them.
        var shared = new Dictionary<string, Rule>(Expected, StringComparer.Ordinal) { ["GET /twin"] = new(Access.Public) };
        problems = Classify(surface, shared, out _);
        Assert.Contains(problems, p => p.Contains(DistinctKeyOf(forAdmins), StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains(DistinctKeyOf(forAll), StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("'GET /twin' names no endpoint", StringComparison.Ordinal));

        // Both classified: two units, each probed and judged on its own.
        var both = new Dictionary<string, Rule>(oneRule, StringComparer.Ordinal) { [DistinctKeyOf(forAll)] = new(Access.Public) };
        Assert.Empty(Classify(surface, both, out classified));
        Assert.Equal(2, classified.Count(c => c.Pattern == "/twin"));

        // Two that cannot be told apart at all are reported, never merged.
        Assert.Contains(Classify([.. app.Endpoints, Twin("twin", "a.example"), Twin("twin", "b.example")], Expected, out _),
            p => p.Contains("cannot tell them apart", StringComparison.Ordinal));

        // A handler run is attributed to the endpoint that ran, not to its route and method — also when the routing
        // middleware ran its own object of that endpoint (a data source that builds new objects for each reader).
        var recorder = new HandlerRecorder();
        recorder.Record("probe", forAll);
        Assert.False(recorder.Ran("probe", [forAdmins]));
        Assert.True(recorder.Ran("probe", [forAll]));
        Assert.True(recorder.Ran("probe", [Twin("twin for everyone", "public.example")]));
        Assert.False(recorder.Ran("another probe", [forAll]));
        Assert.NotEqual(HandlerRecorder.IdentityOf(forAdmins), HandlerRecorder.IdentityOf(forAll));
    }

    [Fact]
    public async Task Every_endpoint_answers_each_role_as_classified_and_runs_its_handler_only_when_allowed()
    {
        Assert.Empty(Classify(app.Endpoints, Expected, out var classified));
        var violations = new List<string>();
        var requests = 0;

        foreach (var caller in app.Callers)
        {
            foreach (var endpoint in classified.OrderBy(c => SessionEnding.Contains(c.Key) ? 1 : 0).ThenBy(c => c.Key, StringComparer.Ordinal))
            foreach (var method in endpoint.ProbeMethods)
            {
                requests++;
                var result = await app.ProbeAsync(caller, endpoint, method);
                violations.AddRange(Judge(caller.Role, endpoint, method, result));
            }
        }

        Assert.True(requests > 4 * 150, $"only {requests} requests were made: the matrix did not see the whole surface");
        Assert.True(violations.Count == 0, $"{violations.Count} violation(s):\n" + string.Join("\n", violations));
    }

    // ------------------------------------------------------------------------------------------------ the rules

    /// <summary>One classified unit of the surface: a single endpoint, or the content-encoding variants of one static
    /// asset (<see cref="Endpoints"/>, proven equivalent: same route, same method, same asset). A probe of the unit counts
    /// as "handler ran" only when one of exactly these endpoint objects ran.</summary>
    internal sealed record Classified(string Key, string Pattern, IReadOnlyList<string> ProbeMethods, Rule Rule, bool Static,
        IReadOnlyList<RouteEndpoint> Endpoints);

    internal static string KeyOf(RouteEndpoint endpoint)
    {
        var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
        var verb = methods is null || methods.Count == 0 ? "*" : string.Join(",", methods.Order(StringComparer.Ordinal));
        return $"{verb} {endpoint.RoutePattern.RawText}";
    }

    /// <summary>The key of an endpoint that shares its route and method with another non-static endpoint (told apart by
    /// host or other selection metadata): the shared key plus its display name, so each is classified on its own.</summary>
    internal static string DistinctKeyOf(RouteEndpoint endpoint) => $"{KeyOf(endpoint)} [{endpoint.DisplayName}]";

    /// <summary>Matches every endpoint to its rule. Returns one line per unclassified endpoint and per stale rule.</summary>
    /// <remarks>Endpoints are merged only when they are proven equivalent: static-asset endpoints (each carries a
    /// <see cref="StaticAssetDescriptor"/>) of one route, method and asset — one per content encoding. Any other endpoint
    /// that shares a route and method with another is classified and probed on its own (<see cref="DistinctKeyOf"/>).</remarks>
    internal static List<string> Classify(IReadOnlyList<Endpoint> endpoints, IReadOnlyDictionary<string, Rule> expected,
        out List<Classified> classified)
    {
        var problems = new List<string>();
        classified = [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var routes = new List<RouteEndpoint>();
        foreach (var endpoint in endpoints)
        {
            if (endpoint is RouteEndpoint route) routes.Add(route);
            else problems.Add($"'{endpoint.DisplayName}' is not a route endpoint; the matrix cannot request it");
        }

        foreach (var group in routes.GroupBy(KeyOf, StringComparer.Ordinal))
        {
            var statics = group.Where(r => r.Metadata.GetMetadata<StaticAssetDescriptor>() is not null).ToList();
            var others = group.Where(r => r.Metadata.GetMetadata<StaticAssetDescriptor>() is null).ToList();
            var units = new List<(string Key, IReadOnlyList<RouteEndpoint> Endpoints, bool Static)>();
            if (statics.Count > 0)
            {
                // Variants of one asset: at most one plain endpoint, every other one selected by Content-Encoding alone.
                var descriptors = statics.Select(s => s.Metadata.GetMetadata<StaticAssetDescriptor>()!).ToList();
                var plain = descriptors.Count(d => d.Selectors.Count == 0);
                var encodings = descriptors.Where(d => d.Selectors.Count > 0)
                    .Select(d => d.Selectors.All(s => s.Name == "Content-Encoding") ? string.Join(",", d.Selectors.Select(s => s.Value)) : null)
                    .ToList();
                if (plain <= 1 && encodings.All(e => e is not null) && encodings.Distinct(StringComparer.Ordinal).Count() == encodings.Count)
                    units.Add((group.Key, statics, true));
                else problems.Add($"'{group.Key}': {statics.Count} static-asset endpoints that are not the content-encoding variants " +
                                  "of one asset; the matrix cannot classify them as one");
            }
            if (others.Count == 1 && statics.Count == 0) units.Add((group.Key, others, false));
            else units.AddRange(others.Select(o => (DistinctKeyOf(o), (IReadOnlyList<RouteEndpoint>)[o], false)));

            foreach (var (key, members, isStatic) in units)
            {
                if (!seen.Add(key))
                {
                    problems.Add($"'{key}': more than one endpoint with this route, method and display name; the matrix cannot " +
                                 "tell them apart — give one a distinct display name or route");
                    continue;
                }
                var route = members[0];
                var methods = route.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
                IReadOnlyList<string> probe = methods is { Count: > 0 }
                    ? methods.Order(StringComparer.Ordinal).ToList()
                    : [route.RoutePattern.RawText!.EndsWith("/negotiate", StringComparison.Ordinal)
                       || route.RoutePattern.RawText.EndsWith("/disconnect/", StringComparison.Ordinal) ? "POST" : "GET"];
                if (isStatic && !expected.ContainsKey(key))
                {
                    classified.Add(new(key, route.RoutePattern.RawText!, probe, new Rule(Access.Public, Why: "static asset"), true, members));
                    continue;
                }
                if (!expected.TryGetValue(key, out var rule))
                {
                    problems.Add($"unclassified endpoint '{key}' ({route.DisplayName}; {route.RequestDelegate?.Method.DeclaringType?.FullName}): " +
                                 "add it to EndpointAuthMatrixTests.Expected with the access it must have");
                    continue;
                }
                classified.Add(new(key, route.RoutePattern.RawText!, probe, rule, isStatic, members));
            }
        }
        foreach (var key in expected.Keys.Where(k => !seen.Contains(k)))
            problems.Add($"classification '{key}' names no endpoint of the app: remove it");
        return problems;
    }

    private static IEnumerable<string> Judge(AppRole? role, Classified endpoint, string method, ProbeResult result)
    {
        var who = role?.ToString() ?? "anonymous";
        var what = $"{who} {method} {endpoint.Key}";
        var allowed = endpoint.Rule.Access switch
        {
            Access.Public or Access.PublicHandlerGated => true,
            Access.SignedIn => role is not null,
            Access.Admin => role == AppRole.Admin,
            _ => false,
        };

        if (endpoint.Rule.Access == Access.Closed)
        {
            if (result.Invoked) yield return $"{what}: a closed route, but its handler ran (HTTP {(int)result.Status})";
            if (result.Status != HttpStatusCode.Redirect || result.Location.Contains(endpoint.Pattern, StringComparison.Ordinal))
                yield return $"{what}: a closed route must redirect away, got HTTP {(int)result.Status} '{result.Location}'";
            yield break;
        }

        if (allowed)
        {
            if (!result.Invoked)
                yield return $"{what}: allowed, but its handler did not run (HTTP {(int)result.Status}) {result.Error}";
            var serverError = (int)result.Status >= 500;
            if (endpoint.Rule.KnownServerError is { } known)
            {
                if (!serverError)
                    yield return $"{what}: answers HTTP {(int)result.Status} now; drop the recorded server error ({known})";
            }
            // A static asset's answer depends on the build manifest of this test host (files it lists may live outside the
            // content root); who reaches it is what the matrix pins.
            else if (serverError && !endpoint.Static)
                yield return $"{what}: allowed, but answered HTTP {(int)result.Status} {result.Error}";
            yield break;
        }

        if (result.Invoked) yield return $"{what}: must be refused before the handler, but the handler ran (HTTP {(int)result.Status})";
        if (role is null)
        {
            var ok = endpoint.Rule.Challenge switch
            {
                Challenge.LoginRedirect => result.Status == HttpStatusCode.Redirect && result.Location.Contains("/login", StringComparison.Ordinal),
                Challenge.Unauthorized => result.Status == HttpStatusCode.Unauthorized,
                _ => false,
            };
            if (!ok) yield return $"{what}: expected {endpoint.Rule.Challenge}, got HTTP {(int)result.Status} '{result.Location}'";
        }
        else
        {
            // A signed-in caller without the role: refused with 403 (R1-50: no redirect to an access-denied page that does
            // not exist) and never sent to sign in again.
            if (result.Status != HttpStatusCode.Forbidden)
                yield return $"{what}: expected 403, got HTTP {(int)result.Status} '{result.Location}'";
        }
    }

    // ------------------------------------------------------------------------------------------------ the booted app

    internal sealed record Caller(AppRole? Role, HttpClient Client);

    internal sealed record ProbeResult(HttpStatusCode Status, string Location, bool Invoked, string Error = "");

    /// <summary>One boot of the real app for the whole class: auth on, Production, every default module, no local Docker.</summary>
    public sealed class App : IAsyncLifetime
    {
        private const string Password = "CorrectHorse12!!";
        internal const string ProbeHeader = "X-Endpoint-Matrix-Probe";
        internal const string StaticFallbackPattern = "{**path:file}";
        private string _dataDir = "";

        private readonly bool _anonymousStaticFallback;

        /// <summary>The app as it is (the class fixture).</summary>
        public App() { }

        /// <summary>A second app for a negative test, with the static-asset fallback opened to anonymous callers.</summary>
        internal App(bool anonymousStaticFallback) => _anonymousStaticFallback = anonymousStaticFallback;
        private BootSettings? _settings;
        private WebApplicationFactory<Program>? _factory;
        private readonly HandlerRecorder _recorder = new();
        private readonly List<HttpClient> _clients = [];

        internal IReadOnlyList<Endpoint> Endpoints { get; private set; } = [];
        internal IReadOnlyList<Caller> Callers { get; private set; } = [];

        public async Task InitializeAsync()
        {
            _dataDir = Path.Combine(Path.GetTempPath(), $"sw-matrix-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dataDir);
            var pwFile = Path.Combine(_dataDir, "admin-pw.txt");
            await File.WriteAllTextAsync(pwFile, Password);
            var config = new List<KeyValuePair<string, string?>>
            {
                new("WHISKERS_DATA_DIR", _dataDir),
                new("Auth:Disabled", "false"),
                new("WHISKERS_ADMIN_EMAIL", "admin@matrix.test"),
                new("WHISKERS_ADMIN_PASSWORD_FILE", pwFile),
                new("WHISKERS_DISABLE_LOCAL_DOCKER", "true"),
            };
            _settings = BootSettings.Apply(config);
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Production");
                b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(config));
                b.ConfigureTestServices(services =>
                {
                    services.AddSingleton<ILoggerProvider>(sp =>
                    {
                        _recorder.Accessor = sp.GetRequiredService<IHttpContextAccessor>();
                        return _recorder;
                    });
                    services.Configure<LoggerFilterOptions>(o => o.Rules.Add(new LoggerFilterRule(
                        typeof(HandlerRecorder).FullName, HandlerRecorder.Category, LogLevel.Information, null)));
                    // The regression the matrix must catch, built into a second app only: the static-asset fallback
                    // opened to anonymous callers.
                    if (_anonymousStaticFallback) services.AddSingleton<MatcherPolicy, AnonymousStaticFallback>();
                });
            });

            var anonymous = NewClient();
            Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/healthz")).StatusCode);
            Endpoints = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;
            ArrangeFallbackProbeFile();

            var roles = _factory.Services.GetRequiredService<IRoleService>();
            var callers = new List<Caller> { new(null, anonymous) };
            foreach (var (email, role) in new[] { ("viewer@matrix.test", AppRole.Viewer), ("operator@matrix.test", AppRole.Operator) })
            {
                using (var scope = _factory.Services.CreateScope())
                {
                    var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
                    var created = await users.CreateAsync(new AppUser { UserName = email, Email = email }, Password);
                    Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Code)));
                }
                await GatewayArrange.RunAsync("config.roles", () => roles.SetRoleAsync(email, role));
            }
            foreach (var (email, role) in new[] { ("viewer@matrix.test", AppRole.Viewer), ("operator@matrix.test", AppRole.Operator),
                         ("admin@matrix.test", AppRole.Admin) })
            {
                Assert.Equal(role, roles.GetRole(email));
                var client = NewClient();
                await SignInAsync(client, email);
                callers.Add(new(role, client));
            }
            Callers = callers;
        }

        /// <summary>
        /// The regression for the negative test: when routing selects the static-asset fallback, the endpoint that runs is
        /// a copy carrying <see cref="Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute"/> — exactly what an
        /// <c>.AllowAnonymous()</c> on that endpoint would do. Same route, methods, order, display name and handler.
        /// </summary>
        private sealed class AnonymousStaticFallback : MatcherPolicy, Microsoft.AspNetCore.Routing.Matching.IEndpointSelectorPolicy
        {
            private readonly ConcurrentDictionary<Endpoint, RouteEndpoint> _opened = new();

            public override int Order => 0;

            private static bool IsFallback(Endpoint endpoint) =>
                endpoint is RouteEndpoint route && route.RoutePattern.RawText == StaticFallbackPattern;

            public bool AppliesToEndpoints(IReadOnlyList<Endpoint> endpoints) => endpoints.Any(IsFallback);

            public Task ApplyAsync(HttpContext httpContext, Microsoft.AspNetCore.Routing.Matching.CandidateSet candidates)
            {
                for (var i = 0; i < candidates.Count; i++)
                {
                    if (!candidates.IsValidCandidate(i) || !IsFallback(candidates[i].Endpoint)) continue;
                    var opened = _opened.GetOrAdd(candidates[i].Endpoint, e =>
                    {
                        var route = (RouteEndpoint)e;
                        return new RouteEndpoint(route.RequestDelegate!, route.RoutePattern, route.Order,
                            new EndpointMetadataCollection([.. route.Metadata, new Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute()]),
                            route.DisplayName);
                    });
                    candidates.ReplaceEndpoint(i, opened, candidates[i].Values);
                }
                return Task.CompletedTask;
            }
        }

        /// <summary>The file the static-asset fallback (<c>{**path:file}</c>) is probed with: it exists, but in no
        /// static-asset manifest, so only that fallback can serve it.</summary>
        internal const string FallbackProbeFile = "matrix-probe.txt";

        /// <summary>
        /// Makes <see cref="FallbackProbeFile"/> exist for the static-asset fallback alone. The fallback looks the file up
        /// in the web root's file provider at request time; the static-files middleware took its provider when the
        /// pipeline was built, so it does not see the file and cannot answer the request before routing. The probe of
        /// <c>{**path:file}</c> therefore reaches that endpoint's handler — and an allowed caller must see it run.
        /// </summary>
        private void ArrangeFallbackProbeFile()
        {
            var root = Path.Combine(_dataDir, "fallback-webroot");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, FallbackProbeFile), "endpoint matrix probe");
            var environment = _factory!.Services.GetRequiredService<IWebHostEnvironment>();
            environment.WebRootFileProvider = new CompositeFileProvider(environment.WebRootFileProvider, new PhysicalFileProvider(root));
        }

        private HttpClient NewClient()
        {
            var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            _clients.Add(client);
            return client;
        }

        private static async Task SignInAsync(HttpClient client, string email)
        {
            var html = await (await client.GetAsync("/login")).Content.ReadAsStringAsync();
            var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
            Assert.False(string.IsNullOrEmpty(token), "the sign-in page carries no antiforgery token");
            var response = await client.PostAsync("/login-local", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["email"] = email, ["password"] = Password, ["__RequestVerificationToken"] = token,
            }));
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.DoesNotContain("error", response.Headers.Location!.OriginalString);
        }

        internal async Task<ProbeResult> ProbeAsync(Caller caller, Classified endpoint, string method)
        {
            var probe = Guid.NewGuid().ToString("N");
            var path = "/" + Regex.Replace(endpoint.Pattern.TrimStart('/'), @"\{\*{0,2}([^}:]+)(:[^}]*)?\}",
                m => m.Groups[2].Value.Contains("file", StringComparison.Ordinal) ? FallbackProbeFile : "matrix-probe");
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            request.Headers.Add(ProbeHeader, probe);
            request.Headers.Accept.ParseAdd("text/html");
            if (endpoint.Key.Contains("/mcp/", StringComparison.Ordinal))
            {
                request.Headers.Accept.Clear();
                request.Headers.Accept.ParseAdd("application/json");
                request.Headers.Accept.ParseAdd("text/event-stream");
            }
            if (method is "POST" or "PUT" or "PATCH")
                request.Content = endpoint.Key.Contains("/mcp/", StringComparison.Ordinal)
                    ? new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json")
                    : new FormUrlEncodedContent([]);

            // Streaming endpoints (MCP GET, hubs) may hold the response open: only the head is needed.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                using var response = await caller.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                return new(response.StatusCode, response.Headers.Location?.OriginalString ?? "", _recorder.Ran(probe, endpoint.Endpoints),
                    _recorder.Describe(probe));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The test server rethrows what the app's exception handler could not turn into a response: a 500.
                return new(HttpStatusCode.InternalServerError, "", _recorder.Ran(probe, endpoint.Endpoints),
                    $"({ex.GetType().Name}: {ex.GetBaseException().Message})");
            }
        }

        public async Task DisposeAsync()
        {
            foreach (var client in _clients) client.Dispose();
            if (_factory is not null) await _factory.DisposeAsync();
            _settings?.Dispose();
            TestSqlite.ClearPools(_dataDir);
            try { Directory.Delete(_dataDir, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }

    /// <summary>
    /// Records which endpoint ran for which probe: the endpoint middleware logs "executing endpoint" right before it
    /// calls the endpoint's request delegate. Authorization, antiforgery-free challenges and the rate limiter all decide
    /// before that point, so an entry here means the handler was reached.
    /// </summary>
    internal sealed class HandlerRecorder : ILoggerProvider
    {
        internal const string Category = "Microsoft.AspNetCore.Routing.EndpointMiddleware";
        private readonly ConcurrentDictionary<string, ConcurrentBag<Endpoint>> _ran = new(StringComparer.Ordinal);
        internal IHttpContextAccessor? Accessor { get; set; }

        public ILogger CreateLogger(string categoryName) =>
            categoryName == Category ? new Recorder(this) : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        /// <summary>What ran for a probe, for the failure message.</summary>
        internal string Describe(string probe) =>
            _ran.TryGetValue(probe, out var bag) && !bag.IsEmpty
                ? "(ran: " + string.Join(", ", bag.Select(e => e is RouteEndpoint r ? KeyOf(r) : e.DisplayName)) + ")"
                : "";

        /// <summary>Whether one of exactly these endpoints ran for the probe — by endpoint identity, never by route text:
        /// another endpoint on the same route and method running is not this handler running.</summary>
        internal bool Ran(string probe, IReadOnlyList<Endpoint> endpoints) =>
            _ran.TryGetValue(probe, out var bag)
            && bag.Any(e => endpoints.Any(t => ReferenceEquals(e, t) || IdentityOf(e) == IdentityOf(t)));

        /// <summary>An endpoint's identity across endpoint objects. The routing middleware reads its own copy of the data
        /// sources, and a minimal-API or hub data source builds new endpoint objects for each reader, so the object that ran
        /// is not the one the matrix enumerated. Route, methods, order, display name and host selector together name one
        /// endpoint: <see cref="Classify"/> reports any two classified endpoints it could not tell apart this way.</summary>
        internal static string IdentityOf(Endpoint endpoint) => endpoint is RouteEndpoint route
            ? $"{KeyOf(route)}|{route.Order}|{route.DisplayName}|{string.Join(",", route.Metadata.GetMetadata<IHostMetadata>()?.Hosts ?? [])}"
            : $"(not routed)|{endpoint.DisplayName}";

        internal void Record(string probe, Endpoint endpoint) => _ran.GetOrAdd(probe, _ => []).Add(endpoint);

        private void Record(EventId eventId)
        {
            if (eventId.Name != "ExecutingEndpoint") return;
            var context = Accessor?.HttpContext;
            var probe = context?.Request.Headers[App.ProbeHeader].ToString();
            var endpoint = context?.GetEndpoint();
            if (string.IsNullOrEmpty(probe) || endpoint is null) return;
            Record(probe, endpoint);
        }

        public void Dispose() { }

        private sealed class Recorder(HandlerRecorder owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => owner.Record(eventId);
        }
    }
}
