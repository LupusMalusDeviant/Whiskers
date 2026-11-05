using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Services.Auth;

namespace Whiskers.Tests;

/// <summary>
/// R1-49 (review round 2, m-B): the recovery page's per-client limit keys on the client address the circuit's own
/// hub connection came from — over a real Blazor circuit on the running app, over WebSockets and long polling (Blazor's binary protocol cannot use Server-Sent Events), with the address
/// the forwarded-headers middleware resolved (a trusted proxy's <c>X-Forwarded-For</c>, an untrusted one's own
/// address). The circuit is started the way <c>blazor.web.js</c> starts it: the prerendered page's component marker
/// goes to the hub's <c>StartCircuit</c>, the circuit renders the page, and the page's account service reads the address.
/// </summary>
[Collection("WebAppBoot")] // the boot needs the process-wide WHISKERS_DATA_DIR
public sealed class LocalAccountCircuitAddressTests
{
    /// <summary>The test server's connection has no address; this request header sets it (as a real socket would).</summary>
    private const string RemoteHeader = "X-Test-Remote-Address";

    [Theory]
    [InlineData(HttpTransportType.WebSockets, "10.0.0.5", "203.0.113.9")]           // trusted proxy (configured)
    [InlineData(HttpTransportType.LongPolling, "10.0.0.5", "203.0.113.9")]
    [InlineData(HttpTransportType.LongPolling, "198.51.100.20", "198.51.100.20")]   // untrusted: its own address
    [InlineData(HttpTransportType.WebSockets, "198.51.100.20", "198.51.100.20")]
    public async Task The_recovery_page_in_a_real_circuit_keys_on_the_hub_connections_client_address(
        HttpTransportType transport, string proxy, string expected)
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"sw-r149-circuit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        var pwFile = Path.Combine(dataDir, "admin-pw.txt");
        await File.WriteAllTextAsync(pwFile, LocalAccountWorld.Password);
        var prev = Environment.GetEnvironmentVariable("WHISKERS_DATA_DIR");
        Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", dataDir);
        var config = new List<KeyValuePair<string, string?>>
        {
            new("Auth:Disabled", "false"),
            new("WHISKERS_ADMIN_EMAIL", LocalAccountWorld.Root),
            new("WHISKERS_ADMIN_PASSWORD_FILE", pwFile),
            // R1-50: only loopback is trusted by default; the proxy of this test is named, as an operator would.
            new("ForwardedHeaders:TrustedNetworks:0", "10.0.0.0/8"),
        };
        config.AddRange(BootSettings.OnlyCore());
        var seen = new ConcurrentQueue<string?>();
        try
        {
            using var bootSettings = BootSettings.Apply(config);
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Development");
                b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(config));
                b.ConfigureTestServices(services =>
                {
                    services.AddSingleton<IStartupFilter>(new RemoteAddressFromHeader());
                    // The real client address, recorded as the account service reads it.
                    services.AddScoped<IClientAddress>(sp =>
                        new RecordingAddress(ActivatorUtilities.CreateInstance<HttpClientAddress>(sp), seen));
                });
            });
            var server = factory.Server;

            // 1. The page, prerendered over plain HTTP from another address (so only the circuit can report `expected`).
            using var http = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var get = new HttpRequestMessage(HttpMethod.Get, "/account/recover");
            get.Headers.Accept.ParseAdd("text/html");
            get.Headers.Add(RemoteHeader, "10.0.0.99");
            get.Headers.Add("X-Forwarded-For", "192.0.2.77");
            var page = await http.SendAsync(get);
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            var html = await page.Content.ReadAsStringAsync();
            var markers = new JsonArray(Regex.Matches(html, "<!--Blazor:(\\{.*?\\})-->")
                .Select(m => JsonNode.Parse(m.Groups[1].Value)!)
                .Where(n => (string?)n["type"] == "server")
                .ToArray());
            Assert.NotEmpty(markers);
            var state = Regex.Match(html, "<!--Blazor-Server-Component-State:(.*?)-->").Groups[1].Value;
            seen.Clear();

            // 2. The circuit, over the transport under test, from `proxy` forwarding for 203.0.113.9.
            var builder = new HubConnectionBuilder();
            builder.Services.AddSingleton(BlazorPack());
            var connection = builder
                .WithUrl(new Uri(server.BaseAddress, "_blazor"), options =>
                {
                    options.Transports = transport;
                    options.SkipNegotiation = false;
                    options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                    options.Headers[RemoteHeader] = proxy;
                    options.Headers["X-Forwarded-For"] = "203.0.113.9";
                    options.WebSocketFactory = async (context, ct) =>
                    {
                        var ws = server.CreateWebSocketClient();
                        ws.ConfigureRequest = request =>
                        {
                            request.Headers[RemoteHeader] = proxy;
                            request.Headers["X-Forwarded-For"] = "203.0.113.9";
                        };
                        return await ws.ConnectAsync(context.Uri, ct);
                    };
                })
                .Build();
            var errors = new ConcurrentQueue<string>();
            connection.On<string>("JS.Error", e => errors.Enqueue(e));
            await connection.StartAsync();
            try
            {
                var circuit = await connection.InvokeAsync<string?>("StartCircuit", server.BaseAddress.ToString(),
                    new Uri(server.BaseAddress, "account/recover").ToString(), markers.ToJsonString(), state);
                Assert.False(string.IsNullOrEmpty(circuit), "the circuit did not start: " + string.Join("; ", errors));

                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (seen.IsEmpty && DateTime.UtcNow < deadline) await Task.Delay(50);
                Assert.False(seen.IsEmpty, "the page's account service was not created: " + string.Join("; ", errors));
                Assert.All(seen, address => Assert.Equal(expected, address));
            }
            finally { await connection.DisposeAsync(); }
        }
        finally
        {
            Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", prev);
            TestSqlite.ClearPools(dataDir);
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>The circuit hub speaks only Blazor's own MessagePack protocol ("blazorpack"), which ships inside the
    /// server assembly; the client uses the same implementation.</summary>
    [Fact]
    public async Task A_circuit_keeps_the_address_of_the_hub_call_that_started_it_for_work_outside_any_hub_call()
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.44");
        var caller = new Caller(http);
        CircuitClientAddress? circuit = null;
        var filter = new ClientAddressHubFilter();

        // Blazor creates the circuit's handlers inside the hub call StartCircuit.
        await filter.InvokeMethodAsync(
            new Microsoft.AspNetCore.SignalR.HubInvocationContext(caller, new ServiceCollection().BuildServiceProvider(), new NoHub(),
                typeof(NoHub).GetMethod(nameof(NoHub.Start))!, []),
            _ =>
            {
                circuit = new CircuitClientAddress();
                return ValueTask.FromResult<object?>(null);
            });

        // Later, in work no hub call started (and without an HTTP request), the circuit still knows its client.
        Assert.Null(ClientAddressHubFilter.Current);
        Assert.Equal("203.0.113.44", new HttpClientAddress(new HttpContextAccessor(), circuit).Address);
        // Outside a circuit there is nothing to tell: one shared bucket.
        Assert.Null(new HttpClientAddress(new HttpContextAccessor(), new CircuitClientAddress()).Address);
    }

    private sealed class NoHub : Microsoft.AspNetCore.SignalR.Hub
    {
        public void Start() { }
    }

    private sealed class Caller(HttpContext http) : Microsoft.AspNetCore.SignalR.HubCallerContext
    {
        private readonly Microsoft.AspNetCore.Http.Features.FeatureCollection _features = NewFeatures(http);
        public override string ConnectionId => "c1";
        public override string? UserIdentifier => null;
        public override System.Security.Claims.ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override Microsoft.AspNetCore.Http.Features.IFeatureCollection Features => _features;
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }

        private static Microsoft.AspNetCore.Http.Features.FeatureCollection NewFeatures(HttpContext http)
        {
            var features = new Microsoft.AspNetCore.Http.Features.FeatureCollection();
            features.Set<Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature>(new HttpContextFeature(http));
            return features;
        }
    }

    private sealed class HttpContextFeature(HttpContext http) : Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature
    {
        public HttpContext? HttpContext { get; set; } = http;
    }

    private static IHubProtocol BlazorPack()
    {
        var type = Type.GetType(
            "Microsoft.AspNetCore.Components.Server.BlazorPack.BlazorPackHubProtocol, Microsoft.AspNetCore.Components.Server",
            throwOnError: true)!;
        return (IHubProtocol)Activator.CreateInstance(type, nonPublic: true)!;
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

    private sealed class RecordingAddress(IClientAddress inner, ConcurrentQueue<string?> seen) : IClientAddress
    {
        public string? Address
        {
            get
            {
                var address = inner.Address;
                seen.Enqueue(address);
                return address;
            }
        }
    }
}
