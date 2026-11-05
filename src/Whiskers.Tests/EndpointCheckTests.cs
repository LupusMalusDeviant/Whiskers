using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.EndpointChecks;
using Whiskers.Services.Metrics.HostLoad;
using Whiskers.Services.Mutations;
using Whiskers.Services.Notifications;
using Whiskers.Services.Server;
using Whiskers.Utils;
using static Whiskers.Tests.EndpointCheckTestDoubles;

namespace Whiskers.Tests;

/// <summary>
/// R1-57 (contract 0004, "Tests (acceptance)"): the endpoint checks without the gateway — templates, ports, literals,
/// resolution, exceptions, parsers, bounds, the loop and the alert. Everything runs in-process against a recorded host;
/// Whiskers never connects to a target. The gateway row is <see cref="EndpointCheckGatingTests"/>.
/// </summary>
public sealed class EndpointCheckTests : IDisposable
{
    private readonly Db _db = new();
    private readonly FakeServerConfig _servers = new(SshServer(), SshServer("db"), SshServer("api"),
        new Whiskers.Models.ServerConfig { Id = "mtls", Name = "mtls", ConnectionType = ConnectionType.TCP });

    public void Dispose() => _db.Dispose();

    private static readonly IPAddress Public4 = IPAddress.Parse("93.184.215.14");
    private static readonly IPAddress Public6 = IPAddress.Parse("2606:4700::1111");

    /// <summary>A runner whose decision is already open (as "Run now" holds it): the probe logic alone.</summary>
    private EndpointCheckRunner Runner(Host host) =>
        new(AutomationMutationRunnerTests.Build().Runner, host, _servers, new EndpointCheckStore(_db.Scopes, _servers));

    private static IReadOnlyList<string> Errors(EndpointCheckDraft draft) => EndpointCheckValidator.Validate(draft, SshServer());

    // ------------------------------------------------------------------------------------------------ templates

    [Fact]
    public void The_resolve_template_quotes_the_name_and_bounds_the_output()
    {
        Assert.Equal("( getent ahosts 'example.com' 2>&1; echo \"__rc=$?\" ) | head -c 16384",
            EndpointCheckCommands.Resolve("example.com"));
    }

    [Fact]
    public void The_http_template_pins_the_address_allows_only_http_and_follows_no_redirect()
    {
        var command = EndpointCheckCommands.Http("https://example.com/health?x=1", "example.com", Public4, 443, 10);

        Assert.Equal(
            "( curl -q -g -s -S -o /dev/null -w '%{http_code} %{time_total}' --proto '=http,https' --max-time 10 " +
            "--resolve 'example.com:443:93.184.215.14' --noproxy '*' 'https://example.com/health?x=1' 2>&1; " +
            "echo \"__rc=$?\" ) | head -c 16384", command);
        Assert.DoesNotContain(" -L", command);
        Assert.DoesNotContain("--location", command);
    }

    [Fact]
    public void The_http_template_of_an_ip_literal_url_needs_no_resolve()
    {
        var command = EndpointCheckCommands.Http("http://93.184.215.14:8080/", "93.184.215.14", Public4, 8080, 3);

        Assert.DoesNotContain("--resolve", command);
        Assert.Contains("--max-time 3 --noproxy '*' 'http://93.184.215.14:8080/'", command);
    }

    [Fact]
    public void The_tcp_template_runs_only_openbsd_netcat_and_says_so_otherwise()
    {
        Assert.Equal(
            "( if ! command -v nc >/dev/null 2>&1; then echo \"__rc=127\"; " +
            "elif nc -h 2>&1 | grep -q 'OpenBSD netcat'; then nc -z -w 5 '93.184.215.14' '5432' 2>&1; echo \"__rc=$?\"; " +
            "else echo \"__nc=unsupported\"; fi ) | head -c 16384",
            EndpointCheckCommands.Tcp(Public4, 5432, 5));
    }

    [Fact]
    public void The_tls_template_sends_sni_for_a_name_and_none_for_a_literal()
    {
        Assert.Equal(
            "( openssl s_client -connect '93.184.215.14:443' -servername 'example.com' -showcerts </dev/null 2>&1; " +
            "echo \"__rc=$?\" ) | head -c 16384",
            EndpointCheckCommands.Tls("example.com", Public4, 443));
        Assert.DoesNotContain("-servername", EndpointCheckCommands.Tls("93.184.215.14", Public4, 443));
    }

    [Theory]
    [InlineData("-oProxyCommand=id")]
    [InlineData("--help")]
    [InlineData("a;id")]
    [InlineData("a|id")]
    [InlineData("a&id")]
    [InlineData("$(id)")]
    [InlineData("`id`")]
    [InlineData("a'b")]
    [InlineData("a\"b")]
    [InlineData("a b")]
    [InlineData("a\tb")]
    [InlineData("a\nb")]
    [InlineData("a>b")]
    [InlineData("*.example.com")]
    [InlineData("{a,b}.example.com")]
    [InlineData("[a-z].example.com")]
    public async Task A_hostile_host_operand_is_refused_before_anything_runs(string target)
    {
        Assert.NotEmpty(Errors(Draft(EndpointCheckKind.Tcp, target, 443)));
        Assert.NotEmpty(Errors(Draft(EndpointCheckKind.TlsExpiry, target, 443)));

        // Re-validated before every run: a stored definition that does not pass runs nothing.
        var host = new Host();
        var run = await Runner(host).ProbeDefinitionAsync(Definition(EndpointCheckKind.Tcp, target, 443), CancellationToken.None);
        Assert.Equal(EndpointCheckOutcome.Refused, run.Outcome);
        Assert.Empty(host.Calls);
    }

    [Theory]
    [InlineData("-x")]
    [InlineData("a;id")]
    [InlineData("a b")]
    [InlineData("a'b")]
    public void The_templates_themselves_refuse_an_unsafe_name(string name)
    {
        Assert.Throws<UnsafeOperandException>(() => EndpointCheckCommands.Resolve(name));
        Assert.Throws<UnsafeOperandException>(() => EndpointCheckCommands.Tls(name, Public4, 443));
        Assert.Throws<UnsafeOperandException>(() => EndpointCheckCommands.Http("https://example.com/", name, Public4, 443, 5));
    }

    [Theory]
    [InlineData("https://example.com/'x")]
    [InlineData("https://example.com/a\"b")]
    [InlineData("https://example.com/a b")]
    [InlineData("https://example.com/$(id)")]
    [InlineData("https://example.com/`id`")]
    [InlineData("https://example.com/a;id")]
    [InlineData("https://example.com/a|id")]
    [InlineData("https://example.com/?a=1&b=2")]
    [InlineData("https://example.com/{a,b}")]
    [InlineData("https://example.com/[1-9]")]
    [InlineData("https://ex[a-z]mple.com/")]
    [InlineData("https://user:pw@example.com/")]
    [InlineData("https://-x.example.com/")]
    [InlineData("https://example.com/\\x")]
    [InlineData("https://example.com/a\nb")]
    public async Task A_hostile_url_is_refused_before_anything_runs(string url)
    {
        Assert.NotEmpty(Errors(Draft(EndpointCheckKind.Http, url)));

        var host = new Host();
        var run = await Runner(host).ProbeDefinitionAsync(Definition(EndpointCheckKind.Http, url), CancellationToken.None);
        Assert.Equal(EndpointCheckOutcome.Refused, run.Outcome);
        Assert.Empty(host.Calls);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://example.com/_x")]
    [InlineData("dict://example.com:11211/stat")]
    [InlineData("ftp://example.com/")]
    [InlineData("HTTPS://example.com/")]
    [InlineData("//example.com/")]
    [InlineData("example.com")]
    public void Only_http_and_https_urls_are_saved(string url)
    {
        Assert.Contains(Errors(Draft(EndpointCheckKind.Http, url)), e => e.Contains("http", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_http_template_limits_curl_to_http_and_https_whatever_the_url_says()
    {
        // A redirect or a crafted URL cannot switch protocol: --proto is part of the fixed template.
        Assert.Contains("--proto '=http,https'", EndpointCheckCommands.Http("https://example.com/", "example.com", Public4, 443, 5));
        Assert.Throws<ArgumentException>(() => EndpointCheckCommands.Http("gopher://example.com/", "example.com", Public4, 70, 5));
        Assert.Throws<ArgumentException>(() => EndpointCheckCommands.Http("file:///etc/passwd", "example.com", Public4, 443, 5));
    }

    [Fact]
    public void A_valid_definition_of_each_kind_is_accepted()
    {
        Assert.Empty(Errors(Draft(EndpointCheckKind.Http, "https://example.com/health")));
        Assert.Empty(Errors(Draft(EndpointCheckKind.Http, "http://example.com:8080/x", expectedStatus: 301)));
        Assert.Empty(Errors(Draft(EndpointCheckKind.Tcp, "db.example.com", 5432)));
        Assert.Empty(Errors(Draft(EndpointCheckKind.TlsExpiry, "example.com", 443, tlsWarnDays: 30)));
    }

    [Fact]
    public void Fields_out_of_range_are_refused()
    {
        Assert.NotEmpty(Errors(Draft(EndpointCheckKind.Http, "https://example.com/", expectedStatus: 99)));
        Assert.NotEmpty(Errors(Draft(EndpointCheckKind.Http, "https://example.com/", expectedStatus: 600)));
        Assert.NotEmpty(Errors(Draft(EndpointCheckKind.TlsExpiry, "example.com", 443, tlsWarnDays: 0)));
        Assert.NotEmpty(Errors(Draft(EndpointCheckKind.TlsExpiry, "example.com", 443, tlsWarnDays: 366)));
        Assert.NotEmpty(Errors(Draft(EndpointCheckKind.Tcp, "example.com", 443, timeout: 0)));
        Assert.NotEmpty(Errors(Draft(EndpointCheckKind.Tcp, "example.com", 443, timeout: 31)));
        Assert.NotEmpty(Errors(Draft(EndpointCheckKind.Tcp, "example.com", 443, tlsWarnDays: 14)));
        Assert.NotEmpty(Errors(Draft(EndpointCheckKind.Tcp, "example.com", 443) with { Name = new string('n', 81) }));
        Assert.NotEmpty(Errors(Draft(EndpointCheckKind.Tcp, "example.com", 443) with { Name = "  " }));
        Assert.NotEmpty(EndpointCheckValidator.Validate(Draft(EndpointCheckKind.Tcp, "example.com", 443), null));
    }

    // ------------------------------------------------------------------------------------------------ port

    [Theory]
    [InlineData("https://example.com/", null, 443)]
    [InlineData("http://example.com/", null, 80)]
    [InlineData("http://example.com", null, 80)]
    [InlineData("https://example.com:8443/x", null, 8443)]
    [InlineData("https://example.com:8443/x", 8443, 8443)]
    [InlineData("https://example.com/", 443, 443)]
    public void The_effective_port_of_a_url(string url, int? port, int expected)
    {
        Assert.True(EndpointCheckValidator.TryTarget(Draft(EndpointCheckKind.Http, url, port), out var target, out var error), error);
        Assert.Equal(expected, target.Port);
    }

    [Theory]
    [InlineData("https://example.com/", 80)]
    [InlineData("https://example.com/", 8443)]
    [InlineData("http://example.com/", 443)]
    [InlineData("https://example.com:8443/", 443)]
    public void A_definition_port_that_disagrees_with_the_url_is_refused(string url, int port)
    {
        Assert.Contains(Errors(Draft(EndpointCheckKind.Http, url, port)), e => e.Contains("does not match"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(65536)]
    [InlineData(-1)]
    public void A_tcp_or_tls_check_needs_a_port(int? port)
    {
        Assert.NotEmpty(Errors(Draft(EndpointCheckKind.Tcp, "example.com", port)));
        Assert.NotEmpty(Errors(Draft(EndpointCheckKind.TlsExpiry, "example.com", port)));
    }

    // ------------------------------------------------------------------------------------------------ literals

    [Theory]
    [InlineData("93.184.215.14")]
    [InlineData("2606:4700::1111")]
    [InlineData("2001:db8::1")]
    [InlineData("::ffff:169.254.169.254")]
    public void A_canonical_ip_literal_is_accepted(string literal)
    {
        Assert.True(EndpointCheckValidator.TryTarget(Draft(EndpointCheckKind.Tcp, literal, 443), out var target, out var error), error);
        Assert.NotNull(target.Literal);
        Assert.Equal(literal, target.Host);
    }

    [Theory]
    [InlineData("2130706433")]          // decimal 127.0.0.1
    [InlineData("0177.0.0.1")]          // octal
    [InlineData("0x7f.0.0.1")]          // hex
    [InlineData("0x7f000001")]
    [InlineData("0x7f.1")]
    [InlineData("127.1")]               // fewer than four parts
    [InlineData("10.1")]
    [InlineData("169.254.43518")]       // 169.254.169.254, inet_aton style
    [InlineData("1.2.3.04")]
    [InlineData("fe80::1%eth0")]        // a zone
    [InlineData("[2606:4700::1111]")]   // brackets
    [InlineData("2606:4700:0:0:0:0:0:1111")]  // not RFC 5952
    [InlineData("2606:4700:0000::1111")]
    public void A_literal_in_any_other_form_is_refused(string literal)
    {
        Assert.NotEmpty(Errors(Draft(EndpointCheckKind.Tcp, literal, 443)));
        Assert.NotEmpty(Errors(Draft(EndpointCheckKind.TlsExpiry, literal, 443)));
    }

    [Theory]
    [InlineData("http://2130706433/")]
    [InlineData("http://0177.0.0.1/")]
    [InlineData("http://0x7f.0.0.1/")]
    [InlineData("http://127.1/")]
    [InlineData("http://[fe80::1%25eth0]/")]
    [InlineData("http://[2606:4700:0:0:0:0:0:1111]/")]
    public void A_url_with_a_non_canonical_literal_is_refused(string url)
    {
        Assert.NotEmpty(Errors(Draft(EndpointCheckKind.Http, url)));
    }

    [Fact]
    public void An_ipv6_url_keeps_its_brackets_and_is_judged_as_the_address()
    {
        Assert.True(EndpointCheckValidator.TryTarget(Draft(EndpointCheckKind.Http, "https://[2606:4700::1111]:8443/x"),
            out var target, out var error), error);
        Assert.Equal(Public6, target.Literal);
        Assert.Equal(8443, target.Port);
        Assert.Contains("'https://[2606:4700::1111]:8443/x'",
            EndpointCheckCommands.Http(target.Url!, target.Host, target.Literal!, target.Port, 5));
    }

    [Fact]
    public void An_ipv6_address_is_bracketed_in_curl_and_s_client()
    {
        Assert.Contains("--resolve 'example.com:443:[2606:4700::1111]'",
            EndpointCheckCommands.Http("https://example.com/", "example.com", Public6, 443, 5));
        Assert.Contains("-connect '[2606:4700::1111]:443' -servername 'example.com'",
            EndpointCheckCommands.Tls("example.com", Public6, 443));
        Assert.Contains("nc -z -w 5 '2606:4700::1111' '443'", EndpointCheckCommands.Tcp(Public6, 443, 5));
    }

    [Fact]
    public async Task An_ipv4_mapped_literal_is_judged_as_the_ipv4_address_it_carries()
    {
        var host = new Host();
        var run = await Runner(host).ProbeDefinitionAsync(Definition(EndpointCheckKind.Tcp, "::ffff:169.254.169.254", 80),
            CancellationToken.None);

        Assert.Equal(EndpointCheckOutcome.Refused, run.Outcome);
        Assert.Contains("link-local", run.Detail);
        Assert.Empty(host.Calls);
    }

    // ------------------------------------------------------------------------------------------------ resolve

    [Fact]
    public void A_complete_answer_lists_each_address_once_in_answer_order()
    {
        var answer = EndpointCheckParser.ParseResolve(Getent("93.184.215.14", "2606:4700::1111", "93.184.215.14"));

        Assert.True(answer.IsUsable);
        Assert.Equal([Public4, Public6], answer.Addresses);
    }

    [Theory]
    [InlineData("")]
    [InlineData("93.184.215.14   STREAM example.com\n")]                       // cut off: no marker
    [InlineData("93.184.215.14   STREAM example.com\n93.184.2")]
    [InlineData("__rc=0\n")]                                                   // no address at all
    [InlineData("__rc=2\n")]                                                   // does not resolve
    [InlineData("sh: getent: not found\n__rc=127\n")]
    [InlineData("hello world\n__rc=0\n")]
    [InlineData("93.184.215.14 STREAM\nnot an address STREAM\n__rc=0\n")]
    [InlineData("0x7f.1 STREAM\n__rc=0\n")]
    [InlineData("2130706433 STREAM\n__rc=0\n")]
    [InlineData("fe80::1%eth0 STREAM\n__rc=0\n")]
    [InlineData("93.184.215.14 BOGUS\n__rc=0\n")]
    [InlineData("93.184.215.14 STREAM\n__rc=0\ntrailing\n")]
    public void An_incomplete_or_unreadable_answer_is_unusable(string output)
    {
        var answer = EndpointCheckParser.ParseResolve(output);
        Assert.False(answer.IsUsable);
        Assert.NotNull(answer.Unreadable);
    }

    [Fact]
    public async Task An_unreadable_or_truncated_answer_fails_the_run_and_probes_nothing()
    {
        var host = new Host().Answering("garbage\n__rc=0\n", "__rc=0\n");
        var run = await Runner(host).ProbeDefinitionAsync(Definition(EndpointCheckKind.Tcp, "example.com", 443), CancellationToken.None);
        Assert.Equal(EndpointCheckOutcome.Failed, run.Outcome);
        Assert.Single(host.Calls);

        // The executor itself cut the answer off: never read as complete, even though it ends with a marker.
        var cut = new Host { Answer = (_, _, _) => Task.FromResult(new CommandResult { Output = Getent("93.184.215.14"), OutputTruncated = true }) };
        run = await Runner(cut).ProbeDefinitionAsync(Definition(EndpointCheckKind.Tcp, "example.com", 443), CancellationToken.None);
        Assert.Equal(EndpointCheckOutcome.Failed, run.Outcome);
        Assert.Single(cut.Calls);
    }

    [Fact]
    public async Task A_denied_fifth_answer_refuses_the_whole_run()
    {
        // Only four would be tried — but the host would use the fifth too, so the name is refused as a whole.
        var host = new Host().Answering(
            Getent("93.184.215.1", "93.184.215.2", "93.184.215.3", "93.184.215.4", "10.0.0.5"), "__rc=0\n");

        var run = await Runner(host).ProbeDefinitionAsync(Definition(EndpointCheckKind.Tcp, "example.com", 443), CancellationToken.None);

        Assert.Equal(EndpointCheckOutcome.Refused, run.Outcome);
        Assert.Contains("10.0.0.5", run.Detail);
        Assert.Contains("private", run.Detail);
        Assert.Empty(host.Probes);
    }

    [Fact]
    public async Task Five_allowed_answers_try_four_and_count_one_not_tried()
    {
        var host = new Host().Answering(
            Getent("93.184.215.1", "93.184.215.2", "93.184.215.3", "93.184.215.4", "93.184.215.5"), "__rc=1\n");

        var run = await Runner(host).ProbeDefinitionAsync(Definition(EndpointCheckKind.Tcp, "example.com", 443), CancellationToken.None);

        Assert.Equal(EndpointCheckOutcome.Failed, run.Outcome);
        Assert.Equal(4, run.AddressesTried);
        Assert.Equal(1, run.AddressesNotTried);
        Assert.Null(run.AnsweredAddress);
        Assert.Equal(5, host.Calls.Count);                       // 1 resolve + 4 probes
        Assert.Equal(["93.184.215.1", "93.184.215.2", "93.184.215.3", "93.184.215.4"],
            host.Probes.Select(p => p.Split("nc -z -w 5 '")[1].Split('\'')[0]));
    }

    [Fact]
    public async Task The_first_address_that_answers_ends_the_run()
    {
        var host = new Host().Answering(Getent("93.184.215.1", "93.184.215.2", "93.184.215.3"), "__rc=0\n");

        var run = await Runner(host).ProbeDefinitionAsync(Definition(EndpointCheckKind.Tcp, "example.com", 443), CancellationToken.None);

        Assert.Equal(EndpointCheckOutcome.Passed, run.Outcome);
        Assert.Equal("93.184.215.1", run.AnsweredAddress);
        Assert.Equal(1, run.AddressesTried);
        Assert.Equal(2, run.AddressesNotTried);
        Assert.Single(host.Probes);
    }

    [Fact]
    public async Task A_nat64_answer_is_judged_by_its_ipv4_address_but_probed_as_answered()
    {
        // Review m4: an IPv6-only host behind DNS64 must contact the address it was given, not the embedded IPv4 one.
        var host = new Host().Answering(Getent("64:ff9b::5db8:d70e"), "__rc=0\n");

        var run = await Runner(host).ProbeDefinitionAsync(Definition(EndpointCheckKind.Tcp, "example.com", 443), CancellationToken.None);

        Assert.Equal(EndpointCheckOutcome.Passed, run.Outcome);
        Assert.Equal("64:ff9b::5db8:d70e", run.AnsweredAddress);
        Assert.Contains("nc -z -w 5 '64:ff9b::5db8:d70e' '443'", Assert.Single(host.Probes));

        // Judged canonically all the same: NAT64 of an internal address is refused.
        var internalHost = new Host().Answering(Getent("64:ff9b::a00:5"), "__rc=0\n");
        run = await Runner(internalHost).ProbeDefinitionAsync(Definition(EndpointCheckKind.Tcp, "example.com", 443), CancellationToken.None);
        Assert.Equal(EndpointCheckOutcome.Refused, run.Outcome);
        Assert.Empty(internalHost.Probes);
    }

    [Fact]
    public async Task A_literal_target_is_probed_as_written()
    {
        var host = new Host();

        var run = await Runner(host).ProbeDefinitionAsync(Definition(EndpointCheckKind.Tcp, "64:ff9b::5db8:d70e", 443), CancellationToken.None);

        Assert.Equal(EndpointCheckOutcome.Passed, run.Outcome);
        Assert.Contains("'64:ff9b::5db8:d70e' '443'", Assert.Single(host.Calls).Command);
        Assert.Equal("64:ff9b::5db8:d70e", run.AnsweredAddress);
    }

    [Fact]
    public async Task Repeated_addresses_are_tried_once()
    {
        var host = new Host().Answering(Getent("93.184.215.1", "93.184.215.1", "::ffff:93.184.215.1", "93.184.215.2"), "__rc=1\n");

        var run = await Runner(host).ProbeDefinitionAsync(Definition(EndpointCheckKind.Tcp, "example.com", 443), CancellationToken.None);

        Assert.Equal(2, run.AddressesTried);
        Assert.Equal(0, run.AddressesNotTried);
        Assert.Equal(2, host.Probes.Count);
    }

    [Theory]
    [InlineData("169.254.169.254", "link-local")]
    [InlineData("127.0.0.1", "loopback")]
    [InlineData("192.168.1.10", "private")]
    [InlineData("100.100.1.1", "CGNAT")]
    [InlineData("fd00::1", "unique local")]
    [InlineData("::1", "loopback")]
    [InlineData("::ffff:169.254.169.254", "link-local")]   // IPv4-mapped
    [InlineData("::ffff:a9fe:a9fe", "link-local")]         // the same, written in hex
    [InlineData("64:ff9b::a00:5", "private")]              // NAT64 of 10.0.0.5
    [InlineData("2002:a00:5::1", "private")]               // 6to4 of 10.0.0.5
    public async Task A_name_answering_an_internal_address_is_refused(string address, string range)
    {
        var host = new Host().Answering(Getent("93.184.215.1", address), "__rc=0\n");

        var run = await Runner(host).ProbeDefinitionAsync(Definition(EndpointCheckKind.Http, "https://example.com/"), CancellationToken.None);

        Assert.Equal(EndpointCheckOutcome.Refused, run.Outcome);
        Assert.Contains(range, run.Detail);
        Assert.Empty(host.Probes);
    }

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    public async Task An_internal_literal_is_refused_without_an_exception(string literal)
    {
        var host = new Host();
        var run = await Runner(host).ProbeDefinitionAsync(Definition(EndpointCheckKind.Tcp, literal, 5432), CancellationToken.None);

        Assert.Equal(EndpointCheckOutcome.Refused, run.Outcome);
        Assert.Empty(host.Calls);
    }

    // ------------------------------------------------------------------------------------------------ exceptions

    private static readonly OutboundExceptionEntry DbLan = new() { Host = "db.lan", Port = 5432, Addresses = ["10.0.0.5"] };

    [Fact]
    public async Task An_internal_target_runs_with_a_matching_name_exception()
    {
        var host = new Host().Answering(Getent("10.0.0.5"), "__rc=0\n");

        var run = await Runner(host).ProbeDefinitionAsync(
            Definition(EndpointCheckKind.Tcp, "db.lan", 5432, exceptions: [DbLan]), CancellationToken.None);

        Assert.Equal(EndpointCheckOutcome.Passed, run.Outcome);
        Assert.Equal("10.0.0.5", run.AnsweredAddress);
    }

    [Fact]
    public async Task An_internal_literal_runs_with_a_matching_literal_exception()
    {
        var host = new Host();
        var run = await Runner(host).ProbeDefinitionAsync(Definition(EndpointCheckKind.Tcp, "10.0.0.5", 5432,
            exceptions: [new OutboundExceptionEntry { Host = "10.0.0.5", Port = 5432 }]), CancellationToken.None);

        Assert.Equal(EndpointCheckOutcome.Passed, run.Outcome);
        Assert.Single(host.Calls);
    }

    public static TheoryData<string, OutboundExceptionEntry> NonMatchingExceptions => new()
    {
        { "another port", new OutboundExceptionEntry { Host = "db.lan", Port = 5433, Addresses = ["10.0.0.5"] } },
        { "another name", new OutboundExceptionEntry { Host = "cache.lan", Port = 5432, Addresses = ["10.0.0.5"] } },
        { "another address", new OutboundExceptionEntry { Host = "db.lan", Port = 5432, Addresses = ["10.0.0.6"] } },
        { "the literal instead of the name", new OutboundExceptionEntry { Host = "10.0.0.5", Port = 5432 } },
    };

    [Theory]
    [MemberData(nameof(NonMatchingExceptions))]
    public async Task An_exception_that_does_not_match_exactly_allows_nothing(string why, OutboundExceptionEntry entry)
    {
        var host = new Host().Answering(Getent("10.0.0.5"), "__rc=0\n");

        var run = await Runner(host).ProbeDefinitionAsync(
            Definition(EndpointCheckKind.Tcp, "db.lan", 5432, exceptions: [entry]), CancellationToken.None);

        Assert.True(run.Outcome == EndpointCheckOutcome.Refused, why);
        Assert.Empty(host.Probes);
    }

    [Fact]
    public async Task A_name_exception_does_not_cover_a_second_internal_address_it_does_not_list()
    {
        var host = new Host().Answering(Getent("10.0.0.5", "10.0.0.9"), "__rc=0\n");

        var run = await Runner(host).ProbeDefinitionAsync(
            Definition(EndpointCheckKind.Tcp, "db.lan", 5432, exceptions: [DbLan]), CancellationToken.None);

        Assert.Equal(EndpointCheckOutcome.Refused, run.Outcome);
        Assert.Contains("10.0.0.9", run.Detail);
    }

    [Theory]
    [InlineData("10.0.0.0/8", 5432)]
    [InlineData("*.lan", 5432)]
    [InlineData("db.lan", 0)]
    public void An_exception_that_is_a_range_a_wildcard_or_has_no_port_is_refused_on_save(string host, int port)
    {
        var entry = new OutboundExceptionEntry { Host = host, Port = port, Addresses = ["10.0.0.5"] };
        Assert.Contains(Errors(Draft(EndpointCheckKind.Tcp, "db.lan", 5432, exceptions: [entry])), e => e.StartsWith("Exception:"));
    }

    [Fact]
    public void Exceptions_are_admin_only_they_are_saved_with_the_definition_and_only_an_admin_saves_one()
    {
        // There is no other way in: no MCP write tool, and the one capability that stores exceptions needs Admin.
        var catalog = new CapabilityCatalog();
        Assert.Equal(McpPermissionLevels.Admin, catalog.Find("endpointcheck.save")!.RequiredLevel);
        Assert.Equal(McpPermissionLevels.Admin, catalog.Find("endpointcheck.delete")!.RequiredLevel);
        Assert.Equal(McpPermissionLevels.Admin, catalog.Find("endpointcheck.run")!.RequiredLevel);
        Assert.DoesNotContain(catalog.All, c => c.Id.StartsWith("endpointcheck.", StringComparison.Ordinal) && c.McpTool is not null);
    }

    // ------------------------------------------------------------------------------------------------ parsers

    [Theory]
    [InlineData("000 10.001curl: (28) Operation timed out after 10001 milliseconds\n__rc=28\n", "timed out")]
    [InlineData("000 0.002curl: (6) Could not resolve host: example.com\n__rc=6\n", "resolve")]
    [InlineData("000 0.001curl: (7) Failed to connect to example.com port 443\n__rc=7\n", "refused")]
    [InlineData("sh: 1: curl: not found\n__rc=127\n", "not installed")]
    [InlineData("200 0.120", "cut off")]                     // no marker
    [InlineData("", "cut off")]
    [InlineData("__rc=0\n", "could not be read")]            // no status
    [InlineData("OK\n__rc=0\n", "could not be read")]
    [InlineData("000 0.010\n__rc=0\n", "no HTTP answer")]
    [InlineData("500 0.050\n__rc=0\n", "expected 2xx")]
    public void Curl_without_positive_evidence_fails(string output, string detail)
    {
        var result = EndpointCheckParser.ParseHttp(output);
        Assert.False(result.Passed);
        Assert.Contains(detail, result.Detail);
    }

    [Fact]
    public void A_2xx_passes_with_its_latency()
    {
        var result = EndpointCheckParser.ParseHttp("204 0.123\n__rc=0\n");
        Assert.True(result.Passed);
        Assert.Equal(204, result.HttpStatus);
        Assert.Equal(123, result.LatencyMs);
    }

    [Fact]
    public void A_3xx_is_an_answer_that_passes_only_when_it_is_the_expected_status()
    {
        Assert.False(EndpointCheckParser.ParseHttp("301 0.010\n__rc=0\n").Passed);
        Assert.False(EndpointCheckParser.ParseHttp("302 0.010\n__rc=0\n", 301).Passed);
        Assert.True(EndpointCheckParser.ParseHttp("301 0.010\n__rc=0\n", 301).Passed);
        Assert.False(EndpointCheckParser.ParseHttp("200 0.010\n__rc=0\n", 301).Passed);
        Assert.True(EndpointCheckParser.ParseHttp("301 0.010\n__rc=0\n").Answered);
    }

    [Theory]
    [InlineData("__rc=1\n", "nc exit 1")]
    [InlineData("nc: connect to 93.184.215.14 port 5432 (tcp) failed: Connection refused\n__rc=1\n", "nc exit 1")]
    [InlineData("__rc=127\n", "not installed")]
    [InlineData("__nc=unsupported\n", "unsupported nc variant")]
    [InlineData("", "cut off")]
    [InlineData("Connection succeeded", "cut off")]
    public void Netcat_without_a_zero_marker_fails(string output, string detail)
    {
        var result = EndpointCheckParser.ParseTcp(output);
        Assert.False(result.Passed);
        Assert.Contains(detail, result.Detail);
    }

    [Fact]
    public void A_target_that_prints_a_marker_itself_cannot_fake_a_pass()
    {
        // Only the marker at the very end counts: the shell's own, written after the tool ended.
        Assert.False(EndpointCheckParser.ParseTcp("__rc=0\n__rc=1\n").Passed);
        Assert.False(EndpointCheckParser.ParseHttp("200 0.1\n__rc=0\n__rc=7\n").Passed);
        Assert.False(EndpointCheckParser.ParseTls(Cert(DateTime.UtcNow.AddDays(90)) + "__rc=0\n__rc=1\n", DateTime.UtcNow, 14).Passed);
        Assert.True(EndpointCheckParser.ParseTcp("__rc=0\n").Passed);
    }

    [Fact]
    public void Output_that_reaches_the_cap_is_cut_off_even_when_it_ends_in_a_marker()
    {
        // Review m3: when head -c cut the real marker away, whatever the target put at byte 16 384 is the end. A target
        // that pads its answer so the cut lands right after its own "__rc=0" must not pass.
        static string AtCap(string head)
        {
            const string forged = "\n__rc=0";
            return head + new string('A', EndpointCheckCommands.OutputCap - System.Text.Encoding.UTF8.GetByteCount(head) - forged.Length) + forged;
        }

        var tcp = AtCap("nc: banner from the target ");
        Assert.Equal(EndpointCheckCommands.OutputCap, System.Text.Encoding.UTF8.GetByteCount(tcp));
        Assert.False(EndpointCheckParser.ParseTcp(tcp).Passed);
        Assert.False(EndpointCheckParser.ParseHttp(AtCap("200 0.100\n")).Passed);
        Assert.False(EndpointCheckParser.ParseTls(AtCap(Cert(Now.AddDays(90))), Now, 14).Passed);
        Assert.False(EndpointCheckParser.ParseResolve(AtCap("93.184.215.14 STREAM example.com\n")).IsUsable);

        // Multi-byte characters count as bytes, as head -c counts them.
        var wide = new string('ä', EndpointCheckCommands.OutputCap / 2 - 3) + "\n__rc=0";
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(wide) >= EndpointCheckCommands.OutputCap);
        Assert.False(EndpointCheckParser.ParseTcp(wide).Passed);

        // One byte under the cap is a complete answer.
        Assert.True(EndpointCheckParser.ParseTcp(new string('A', EndpointCheckCommands.OutputCap - 8) + "\n__rc=0").Passed);
    }

    private static string Cert(DateTime notAfterUtc)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=example.com", key, HashAlgorithmName.SHA256);
        using var cert = request.CreateSelfSigned(notAfterUtc.AddDays(-400), notAfterUtc);
        return "CONNECTED(00000003)\ndepth=0 CN = example.com\n---\nCertificate chain\n 0 s:CN = example.com\n" +
               cert.ExportCertificatePem() + "\n---\nServer certificate\nsubject=CN = example.com\n---\n";
    }

    private static readonly DateTime Now = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_certificate_with_enough_days_left_passes()
    {
        var result = EndpointCheckParser.ParseTls(Cert(Now.AddDays(90)) + "__rc=0\n", Now, 14);
        Assert.True(result.Passed, result.Detail);
        Assert.Equal(Now.AddDays(90), result.TlsNotAfterUtc!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void A_certificate_under_the_warning_days_or_expired_fails()
    {
        Assert.False(EndpointCheckParser.ParseTls(Cert(Now.AddDays(10)) + "__rc=0\n", Now, 14).Passed);
        Assert.Contains("under the 14-day limit", EndpointCheckParser.ParseTls(Cert(Now.AddDays(10)) + "__rc=0\n", Now, 14).Detail);
        Assert.Contains("expired", EndpointCheckParser.ParseTls(Cert(Now.AddDays(-1)) + "__rc=0\n", Now, 14).Detail);
        Assert.True(EndpointCheckParser.ParseTls(Cert(Now.AddDays(10)) + "__rc=0\n", Now, 7).Passed);
    }

    [Fact]
    public void A_certificate_printed_followed_by_a_failing_openssl_fails()
    {
        // s_client printed the chain, then verification or the handshake failed: no pass on half the evidence.
        var result = EndpointCheckParser.ParseTls(Cert(Now.AddDays(90)) + "__rc=1\n", Now, 14);
        Assert.False(result.Passed);
        Assert.Contains("openssl exit 1", result.Detail);
    }

    [Theory]
    [InlineData("4067F8A1:error:8000006F:system library:BIO_connect:Connection refused\nconnect:errno=111\n__rc=1\n", "openssl exit 1")]
    [InlineData("sh: openssl: not found\n__rc=127\n", "not installed")]
    [InlineData("CONNECTED(00000003)\nno peer certificate available\n__rc=0\n", "no server certificate")]
    [InlineData("-----BEGIN CERTIFICATE-----\nAAAA\n-----END CERTIFICATE-----\n__rc=0\n", "could not be read")]
    [InlineData("CONNECTED(00000003)\n-----BEGIN CERTIFICATE-----\nMIIB", "cut off")]
    public void Openssl_without_a_readable_certificate_fails(string output, string detail)
    {
        var result = EndpointCheckParser.ParseTls(output, Now, 14);
        Assert.False(result.Passed);
        Assert.Contains(detail, result.Detail);
    }

    [Fact]
    public async Task A_missing_tool_fails_the_run_never_passes_it()
    {
        foreach (var (kind, target, port) in new[]
                 {
                     (EndpointCheckKind.Http, "https://93.184.215.14/", (int?)null),
                     (EndpointCheckKind.Tcp, "93.184.215.14", 443),
                     (EndpointCheckKind.TlsExpiry, "93.184.215.14", 443),
                 })
        {
            var host = new Host().Answering("", "sh: 1: tool: not found\n__rc=127\n");
            var run = await Runner(host).ProbeDefinitionAsync(Definition(kind, target, port), CancellationToken.None);
            Assert.Equal(EndpointCheckOutcome.Failed, run.Outcome);
            Assert.Contains("not installed", run.Detail);
        }
    }

    // ------------------------------------------------------------------------------------------------ bounds

    [Fact]
    public async Task A_large_response_is_cut_at_the_template_cap_and_fails()
    {
        // The mTLS ("TCP") transport has no output cap of its own: the template's head -c is the bound, and the marker
        // sits inside it, so a cut-off answer has no marker and is no pass. Review T3: no transport runs here — the fake
        // host applies the command's own "| head -c 16384" as the host's shell would, so this proves the template and the
        // parser; that a real mTLS host cuts the stream is part of the live checks (WORK_PACKAGES R1-57, open).
        var flood = new string('A', 100_000) + "\n__rc=0\n";
        var host = new Host { Answer = (_, _, _) => Task.FromResult(new CommandResult { Output = flood }) };

        var run = await Runner(host).ProbeDefinitionAsync(Definition(EndpointCheckKind.Tcp, "93.184.215.14", 443, serverId: "mtls"),
            CancellationToken.None);

        Assert.Equal(EndpointCheckOutcome.Failed, run.Outcome);
        Assert.Contains("cut off", run.Detail);
        var command = Assert.Single(host.Calls).Command;
        Assert.EndsWith($"echo \"__rc=$?\"; else echo \"__nc=unsupported\"; fi ) | head -c {EndpointCheckCommands.OutputCap}", command);
        Assert.Equal(16_384, EndpointCheckCommands.OutputCap);
    }

    [Fact]
    public void Every_template_writes_its_marker_inside_the_bound()
    {
        foreach (var command in new[]
                 {
                     EndpointCheckCommands.Resolve("example.com"),
                     EndpointCheckCommands.Http("https://example.com/", "example.com", Public4, 443, 5),
                     EndpointCheckCommands.Tcp(Public4, 443, 5),
                     EndpointCheckCommands.Tls("example.com", Public4, 443),
                 })
        {
            Assert.StartsWith("( ", command);
            Assert.EndsWith(" ) | head -c 16384", command);
            Assert.True(command.LastIndexOf("__rc=", StringComparison.Ordinal) < command.LastIndexOf(" ) | head -c", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Each_attempt_is_limited_by_the_timeout_and_a_run_makes_at_most_five_calls()
    {
        // The host honours each call's timeout (as every transport does, #140) but never answers within it. Each call gets
        // the check's timeout + 2 s (review n1: the tool's own limit ends first); five such calls are exactly the run
        // deadline, so even the worst case of every call running to its end stays inside it.
        var host = new Host
        {
            Answer = async (command, timeout, ct) =>
            {
                if (command.Contains("getent", StringComparison.Ordinal))
                    return new CommandResult { Output = Getent("93.184.215.1", "93.184.215.2", "93.184.215.3", "93.184.215.4", "93.184.215.5") };
                await Task.Delay(timeout!.Value, ct);
                return new CommandResult { ExitCode = -1, Error = "Command timed out" };
            },
        };
        var watch = Stopwatch.StartNew();

        var run = await Runner(host).ProbeDefinitionAsync(Definition(EndpointCheckKind.Tcp, "example.com", 443, timeout: 1),
            CancellationToken.None);

        watch.Stop();
        Assert.Equal(EndpointCheckOutcome.Failed, run.Outcome);
        Assert.Equal(5, host.Calls.Count);
        Assert.All(host.Calls, c => Assert.Equal(TimeSpan.FromSeconds(3), c.Timeout));
        Assert.Equal(EndpointCheckValidator.RunDeadline(1), EndpointCheckRunner.AttemptTimeout(1) * 5);
        Assert.Equal(EndpointCheckValidator.RunDeadline(30), EndpointCheckRunner.AttemptTimeout(30) * 5);
        Assert.True(watch.Elapsed < EndpointCheckValidator.RunDeadline(1), $"took {watch.Elapsed}");
        Assert.Contains("timed out", run.Detail);
    }

    [Fact]
    public async Task A_stalled_host_ends_the_run_at_its_deadline()
    {
        // The worst case: a host that ignores the call's timeout and answers only when the caller gives up.
        var cancelled = 0;
        var host = new Host
        {
            Answer = async (_, _, ct) =>
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                catch (OperationCanceledException) { Interlocked.Increment(ref cancelled); throw; }
                return new CommandResult();
            },
        };
        var deadline = EndpointCheckValidator.RunDeadline(1);
        Assert.Equal(TimeSpan.FromSeconds(15), deadline);   // timeout × 5 + 10 s
        var watch = Stopwatch.StartNew();

        var run = await Runner(host).ProbeDefinitionAsync(Definition(EndpointCheckKind.Tcp, "example.com", 443, timeout: 1),
            CancellationToken.None).WaitAsync(deadline + TimeSpan.FromSeconds(10));

        watch.Stop();
        Assert.Equal(EndpointCheckOutcome.Failed, run.Outcome);
        Assert.Contains("did not finish within 15 s", run.Detail);
        Assert.True(watch.Elapsed >= deadline - TimeSpan.FromSeconds(1), $"ended early: {watch.Elapsed}");
        Assert.Equal(1, cancelled);
    }

    [Fact]
    public void The_run_deadline_is_five_attempts_plus_ten_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), EndpointCheckValidator.RunDeadline(10));
        Assert.Equal(TimeSpan.FromSeconds(160), EndpointCheckValidator.RunDeadline(30));
    }

    [Fact]
    public void The_detail_is_one_bounded_line_with_secrets_redacted()
    {
        var detail = EndpointCheckRunner.Bounded("line one\nline two password=hunter2secret " + new string('x', 500));
        Assert.DoesNotContain('\n', detail);
        Assert.True(detail.Length <= EndpointCheckRunner.MaxDetail);
        Assert.DoesNotContain("hunter2secret", detail);
    }

    // ------------------------------------------------------------------------------------------------ loop

    private static readonly DateTime T0 = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
    private static readonly IReadOnlyDictionary<string, string> NoneRunning = new Dictionary<string, string>();

    [Fact]
    public void The_interval_is_at_least_60_seconds()
    {
        Assert.NotEmpty(Errors(Draft(EndpointCheckKind.Tcp, "example.com", 443, interval: 59)));
        Assert.Empty(Errors(Draft(EndpointCheckKind.Tcp, "example.com", 443, interval: 60)));
        Assert.NotEmpty(Errors(Draft(EndpointCheckKind.Tcp, "example.com", 443, interval: 86_401)));

        // A stored interval below the minimum (written past the validator) still waits 60 s.
        var fast = Definition(EndpointCheckKind.Tcp, "example.com", 443, interval: 5, lastRunUtc: T0);
        Assert.Empty(EndpointCheckMonitor.Due([fast], T0.AddSeconds(59), NoneRunning));
        Assert.Single(EndpointCheckMonitor.Due([fast], T0.AddSeconds(60), NoneRunning));

        var slow = Definition(EndpointCheckKind.Tcp, "example.com", 443, interval: 300, lastRunUtc: T0);
        Assert.Empty(EndpointCheckMonitor.Due([slow], T0.AddSeconds(299), NoneRunning));
        Assert.Single(EndpointCheckMonitor.Due([slow], T0.AddSeconds(300), NoneRunning));
    }

    [Fact]
    public void At_most_four_run_at_a_time_the_longest_overdue_first()
    {
        var checks = Enumerable.Range(1, 6)
            .Select(i => Definition(EndpointCheckKind.Tcp, "example.com", 443, serverId: $"s{i}", id: $"c{i}",
                lastRunUtc: T0.AddMinutes(-i)))
            .ToList();

        Assert.Equal(["c6", "c5", "c4", "c3"], EndpointCheckMonitor.Due(checks, T0, NoneRunning).Select(c => c.Id));

        var running = new Dictionary<string, string> { ["x1"] = "other1", ["x2"] = "other2", ["x3"] = "other3" };
        Assert.Single(EndpointCheckMonitor.Due(checks, T0, running));
        running["x4"] = "other4";
        Assert.Empty(EndpointCheckMonitor.Due(checks, T0, running));
    }

    [Fact]
    public void At_most_one_runs_per_server()
    {
        var checks = new[]
        {
            Definition(EndpointCheckKind.Tcp, "example.com", 443, serverId: "a", id: "a1"),
            Definition(EndpointCheckKind.Tcp, "example.com", 444, serverId: "a", id: "a2"),
            Definition(EndpointCheckKind.Tcp, "example.com", 445, serverId: "a", id: "a3"),
            Definition(EndpointCheckKind.Tcp, "example.com", 443, serverId: "b", id: "b1"),
            Definition(EndpointCheckKind.Tcp, "example.com", 443, serverId: "c", id: "c1", enabled: false),
        };

        var due = EndpointCheckMonitor.Due(checks, T0, NoneRunning);
        Assert.Equal(["a", "b"], due.Select(c => c.ServerId).Order());

        // A check still running on "a" holds the server.
        due = EndpointCheckMonitor.Due(checks, T0, new Dictionary<string, string> { ["a1"] = "a" });
        Assert.Equal(["b1"], due.Select(c => c.Id));
    }

    private sealed class ScriptedRunner(ManualTime time) : IEndpointCheckRunner
    {
        public Func<EndpointCheckDefinition, EndpointCheckOutcome> Outcome { get; set; } = _ => EndpointCheckOutcome.Passed;
        public List<string> Ran { get; } = [];

        public Task<EndpointCheckRun> RunAsync(EndpointCheckDefinition check, CancellationToken ct)
        {
            lock (Ran) Ran.Add(check.Id);
            var outcome = Outcome(check);
            return Task.FromResult(new EndpointCheckRun(check.Id, time.GetUtcNow().UtcDateTime, outcome, outcome.ToString()));
        }

        public Task<EndpointCheckRun> RunInDecisionAsync(string checkId, CancellationToken ct) => throw new NotSupportedException();
    }

    private (EndpointCheckMonitor Monitor, EndpointCheckStore Store, FakeNotifications Notes) Monitor(
        IEndpointCheckRunner runner, ManualTime time)
    {
        var fixture = AutomationMutationRunnerTests.Build();
        fixture.Whitelist.Allowed.Add(Admin);
        fixture.Roles.Map[Admin] = AppRole.Admin;
        var store = new EndpointCheckStore(_db.Scopes, _servers, time);
        var notes = new FakeNotifications();
        var monitor = new EndpointCheckMonitor(store, runner, fixture.Runner, _servers, notes,
            NullLogger<EndpointCheckMonitor>.Instance, time: time);
        return (monitor, store, notes);
    }

    [Fact]
    public async Task A_failing_run_is_recorded_and_does_not_stop_the_loop()
    {
        var time = new ManualTime();
        var runner = new ThrowingRunner(time);
        var (monitor, store, _) = Monitor(runner, time);
        var bad = await store.SaveAsync(Draft(EndpointCheckKind.Tcp, "example.com", 443, serverId: "web", name: "bad"), AdminId, default);
        var good = await store.SaveAsync(Draft(EndpointCheckKind.Tcp, "example.com", 443, serverId: "db", name: "good"), AdminId, default);
        runner.Throws.Add(bad.Id);

        await monitor.CycleAsync(CancellationToken.None);
        await SettleAsync(monitor);

        Assert.Equal(EndpointCheckOutcome.Failed, store.Get(bad.Id)!.LastOutcome);
        Assert.Contains("could not be completed", store.Results(bad.Id).Single().Detail);
        Assert.Equal(EndpointCheckOutcome.Passed, store.Get(good.Id)!.LastOutcome);

        // The next cycle runs both again.
        time.Advance(TimeSpan.FromSeconds(60));
        await monitor.CycleAsync(CancellationToken.None);
        await SettleAsync(monitor);
        Assert.Equal(2, runner.Ran.Count(id => id == bad.Id));
        Assert.Equal(2, runner.Ran.Count(id => id == good.Id));
        Assert.Equal(2, store.Results(bad.Id).Count);
    }

    private sealed class ThrowingRunner(ManualTime time) : IEndpointCheckRunner
    {
        public HashSet<string> Throws { get; } = [];
        public List<string> Ran { get; } = [];

        public Task<EndpointCheckRun> RunAsync(EndpointCheckDefinition check, CancellationToken ct)
        {
            lock (Ran) Ran.Add(check.Id);
            if (Throws.Contains(check.Id)) throw new InvalidOperationException("the host went away");
            return Task.FromResult(new EndpointCheckRun(check.Id, time.GetUtcNow().UtcDateTime, EndpointCheckOutcome.Passed, "ok"));
        }

        public Task<EndpointCheckRun> RunInDecisionAsync(string checkId, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Results_older_than_seven_days_are_pruned_but_the_last_one_is_kept()
    {
        var time = new ManualTime();
        var store = new EndpointCheckStore(_db.Scopes, _servers, time);
        var check = await store.SaveAsync(Draft(EndpointCheckKind.Tcp, "example.com", 443), AdminId, default);
        var now = time.GetUtcNow().UtcDateTime;
        await store.RecordAsync(new EndpointCheckRun(check.Id, now.AddDays(-9), EndpointCheckOutcome.Passed, "old"));
        await store.RecordAsync(new EndpointCheckRun(check.Id, now.AddDays(-8), EndpointCheckOutcome.Failed, "last"));
        var other = await store.SaveAsync(Draft(EndpointCheckKind.Tcp, "example.com", 444), AdminId, default);
        await store.RecordAsync(new EndpointCheckRun(other.Id, now.AddDays(-8), EndpointCheckOutcome.Passed, "old"));
        await store.RecordAsync(new EndpointCheckRun(other.Id, now.AddDays(-1), EndpointCheckOutcome.Passed, "recent"));

        Assert.Equal(TimeSpan.FromDays(7), EndpointCheckMonitor.Retention);
        Assert.Equal(2, await store.PruneAsync(now - EndpointCheckMonitor.Retention));

        Assert.Equal("last", store.Results(check.Id).Single().Detail);
        Assert.Equal("recent", store.Results(other.Id).Single().Detail);
    }

    // ------------------------------------------------------------------------------------------------ slots (review m1)

    [Fact]
    public void The_slots_allow_one_run_per_check_and_server_and_four_across_the_fleet()
    {
        var slots = new EndpointCheckSlots();

        Assert.True(slots.TryAcquire("a1", "a"));
        Assert.False(slots.TryAcquire("a1", "a"));          // the same check again
        Assert.False(slots.TryAcquire("a2", "a"));          // another check on the busy server
        Assert.True(slots.TryAcquire("b1", "b"));
        Assert.True(slots.TryAcquire("c1", "c"));
        Assert.True(slots.TryAcquire("d1", "d"));
        Assert.False(slots.TryAcquire("e1", "e"));          // four in flight
        Assert.Equal(4, slots.Running.Count);
        Assert.Equal("a", slots.Running["a1"]);

        slots.Release("a1");
        Assert.True(slots.TryAcquire("a2", "a"));
        slots.Release("nothing");                           // releasing what is not held changes nothing
        Assert.Equal(4, slots.Running.Count);
    }

    [Fact]
    public void Concurrent_acquires_never_exceed_the_limits()
    {
        var slots = new EndpointCheckSlots();
        var granted = new System.Collections.Concurrent.ConcurrentBag<(string Check, string Server)>();
        Parallel.For(0, 400, i =>
        {
            var check = $"c{i % 40}";
            var server = $"s{i % 6}";
            if (slots.TryAcquire(check, server)) granted.Add((check, server));
        });

        Assert.Equal(4, granted.Count);
        Assert.Equal(4, granted.Select(g => g.Server).Distinct().Count());
    }

    private sealed class GatedRunner(ManualTime time) : IEndpointCheckRunner
    {
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HashSet<string> Slow { get; } = [];
        public List<string> Ran { get; } = [];

        public async Task<EndpointCheckRun> RunAsync(EndpointCheckDefinition check, CancellationToken ct)
        {
            lock (Ran) Ran.Add(check.Id);
            if (Slow.Contains(check.Id)) await Gate.Task.WaitAsync(ct);
            return new EndpointCheckRun(check.Id, time.GetUtcNow().UtcDateTime, EndpointCheckOutcome.Passed, "ok");
        }

        public Task<EndpointCheckRun> RunInDecisionAsync(string checkId, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_slow_run_holds_its_check_and_server_across_ticks_and_run_now_shares_the_slots()
    {
        var time = new ManualTime();
        var runner = new GatedRunner(time);
        var slots = new EndpointCheckSlots();
        var fixture = AutomationMutationRunnerTests.Build();
        fixture.Whitelist.Allowed.Add(Admin);
        fixture.Roles.Map[Admin] = AppRole.Admin;
        var store = new EndpointCheckStore(_db.Scopes, _servers, time);
        var monitor = new EndpointCheckMonitor(store, runner, fixture.Runner, _servers, new FakeNotifications(),
            NullLogger<EndpointCheckMonitor>.Instance, time: time, slots: slots);
        var slow = await store.SaveAsync(Draft(EndpointCheckKind.Tcp, "example.com", 443, serverId: "web", name: "slow"), AdminId, default);
        var sameServer = await store.SaveAsync(Draft(EndpointCheckKind.Tcp, "example.com", 444, serverId: "web", name: "same"), AdminId, default);
        var other = await store.SaveAsync(Draft(EndpointCheckKind.Tcp, "example.com", 443, serverId: "db", name: "other"), AdminId, default);
        runner.Slow.Add(slow.Id);
        runner.Slow.Add(sameServer.Id);

        // First tick: one of the two "web" checks and the "db" one.
        await monitor.CycleAsync(CancellationToken.None);
        await WaitUntil(() => runner.Ran.Count == 2 && !slots.Running.ContainsKey(other.Id));
        var first = runner.Ran.Single(id => id != other.Id);

        // Ticks while it hangs: neither "web" check starts again; "db" runs on.
        for (var i = 0; i < 3; i++)
        {
            time.Advance(TimeSpan.FromSeconds(60));
            await monitor.CycleAsync(CancellationToken.None);
            await WaitUntil(() => runner.Ran.Count(id => id == other.Id) == i + 2 && !slots.Running.ContainsKey(other.Id));
        }
        Assert.Equal(1, runner.Ran.Count(id => id != other.Id));
        Assert.Equal("web", slots.Running[first]);

        // "Run now" of any "web" check finds no slot while the monitor's run holds the server.
        Assert.False(slots.TryAcquire(slow.Id, "web"));
        Assert.False(slots.TryAcquire(sameServer.Id, "web"));

        runner.Gate.SetResult();
        await SettleAsync(monitor);
        Assert.DoesNotContain(first, slots.Running.Keys);

        // A slot "Run now" holds keeps the monitor off that server in turn.
        Assert.True(slots.TryAcquire(sameServer.Id, "web"));
        time.Advance(TimeSpan.FromSeconds(60));
        var before = runner.Ran.Count(id => id != other.Id);
        await monitor.CycleAsync(CancellationToken.None);
        await SettleAsync(monitor);
        Assert.Equal(before, runner.Ran.Count(id => id != other.Id));
        slots.Release(sameServer.Id);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("The condition was not met within 10 s.");
            await Task.Delay(10);
        }
    }

    // ------------------------------------------------------------------------------------------------ disclosure (review m6, m7)

    [Fact]
    public void The_mcp_summary_gives_the_numbers_but_never_the_detail_with_its_addresses()
    {
        var result = new EndpointCheckResultEntity
        {
            CheckId = "c1", AtUtc = T0, Outcome = EndpointCheckOutcome.Passed, Detail = "10.0.0.5: TCP connect succeeded",
            AnsweredAddress = "10.0.0.5", LatencyMs = 12, HttpStatus = 204, TlsNotAfterUtc = T0.AddDays(30),
        };

        var summary = Whiskers.Mcp.Tools.EndpointCheckTools.Summary(result);

        Assert.Equal("Passed at 2026-11-05 12:00 UTC, HTTP 204, 12 ms, certificate valid until 2026-12-05", summary);
        Assert.DoesNotContain("10.0.0.5", summary);
    }

    [Fact]
    public void The_page_hides_a_urls_query_from_non_admins_and_redacts_secrets_for_everyone()
    {
        var check = Definition(EndpointCheckKind.Http, "https://example.com/health?token=abc123secret#top");

        foreach (var role in new[] { AppRole.Viewer, AppRole.Operator })
        {
            var shown = Whiskers.Components.Pages.EndpointChecks.DisplayTarget(check, role);
            Assert.Equal("https://example.com/health?…#top", shown);
        }
        Assert.DoesNotContain("abc123secret", Whiskers.Components.Pages.EndpointChecks.DisplayTarget(check, AppRole.Admin));
        Assert.Equal("db.lan:5432",
            Whiskers.Components.Pages.EndpointChecks.DisplayTarget(Definition(EndpointCheckKind.Tcp, "db.lan", 5432), AppRole.Viewer));
    }

    // ------------------------------------------------------------------------------------------------ orphans (review m8)

    [Fact]
    public async Task A_run_of_a_check_deleted_meanwhile_is_not_recorded()
    {
        var store = new EndpointCheckStore(_db.Scopes, _servers);
        var check = await store.SaveAsync(Draft(EndpointCheckKind.Tcp, "example.com", 443), AdminId, default);
        await store.DeleteAsync(check.Id, default);

        await store.RecordAsync(new EndpointCheckRun(check.Id, T0, EndpointCheckOutcome.Passed, "late"));

        Assert.Equal(0, _db.ResultCount());
    }

    [Fact]
    public async Task Pruning_removes_the_results_of_a_check_that_is_gone()
    {
        var store = new EndpointCheckStore(_db.Scopes, _servers);
        var kept = await store.SaveAsync(Draft(EndpointCheckKind.Tcp, "example.com", 443), AdminId, default);
        await store.RecordAsync(new EndpointCheckRun(kept.Id, T0, EndpointCheckOutcome.Passed, "kept"));
        using (var scope = _db.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Whiskers.Services.Persistence.MetricsDbContext>();
            db.EndpointCheckResults.Add(new EndpointCheckResultEntity { CheckId = "gone", AtUtc = T0, Detail = "orphan" });
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, await store.PruneAsync(T0.AddDays(-7)));

        Assert.Equal(1, _db.ResultCount());
        Assert.Single(store.Results(kept.Id));
    }

    // ------------------------------------------------------------------------------------------------ alerting

    private static HostLoadFinding? Feed(BreachTracker tracker, DateTime at, int value, int interval = 60) =>
        tracker.Consider(at, EndpointCheckMonitor.SeriesOf("c1"), "check on web", NotificationEventTypes.EndpointCheckFailed,
            value, 1, (_, _) => "failing", _ => "passing", EndpointCheckMonitor.RulesFor(interval));

    [Fact]
    public void Sustained_failure_opens_the_alert_after_five_minutes_and_recovery_clears_it()
    {
        var tracker = new BreachTracker(new HostLoadThresholds());
        for (var m = 0; m < 5; m++) Assert.Null(Feed(tracker, T0.AddMinutes(m), 1));

        var raised = Feed(tracker, T0.AddMinutes(5), 1);
        Assert.Equal(FindingKind.Raised, raised!.What);
        Assert.Equal(NotificationEventTypes.EndpointCheckFailed, raised.EventType);
        Assert.Null(Feed(tracker, T0.AddMinutes(6), 1));             // said once, not restated

        for (var m = 7; m < 12; m++) Assert.Null(Feed(tracker, T0.AddMinutes(m), 0));
        var cleared = Feed(tracker, T0.AddMinutes(12), 0);
        Assert.Equal(FindingKind.Cleared, cleared!.What);
        Assert.Equal(NotificationEventTypes.Recovered(NotificationEventTypes.EndpointCheckFailed), cleared.EventType);
    }

    [Fact]
    public void One_failed_run_never_opens_the_alert_two_runs_five_minutes_apart_do()
    {
        var tracker = new BreachTracker(new HostLoadThresholds());
        Assert.Null(Feed(tracker, T0, 1, interval: 600));
        Assert.Equal(FindingKind.Raised, Feed(tracker, T0.AddMinutes(10), 1, interval: 600)!.What);

        // One pass in between does not end a failure (hysteresis: it must pass for five minutes)...
        tracker = new BreachTracker(new HostLoadThresholds());
        Feed(tracker, T0, 1);
        Assert.Null(Feed(tracker, T0.AddMinutes(3), 0));
        Assert.Null(Feed(tracker, T0.AddMinutes(4), 1));
        Assert.Equal(FindingKind.Raised, Feed(tracker, T0.AddMinutes(5), 1)!.What);

        // ...five minutes of passing do, silently (nothing was said), and the next failure starts from zero.
        tracker = new BreachTracker(new HostLoadThresholds());
        Feed(tracker, T0, 1);
        for (var m = 1; m <= 6; m++) Assert.Null(Feed(tracker, T0.AddMinutes(m), 0));
        for (var m = 7; m < 12; m++) Assert.Null(Feed(tracker, T0.AddMinutes(m), 1));
        Assert.Equal(FindingKind.Raised, Feed(tracker, T0.AddMinutes(12), 1)!.What);
    }

    [Fact]
    public void The_alert_kind_is_recoverable_and_suppressible()
    {
        Assert.Contains(NotificationEventTypes.EndpointCheckFailed, NotificationEventTypes.RecoverableKinds);
        Assert.Contains(NotificationEventTypes.EndpointCheckFailed, NotificationEventTypes.SuppressibleEvents);
        Assert.Equal("endpoint_check_failed", NotificationEventTypes.EndpointCheckFailed);
    }

    [Fact]
    public async Task A_refused_run_counts_as_failed_and_a_passing_one_clears_the_alert()
    {
        var time = new ManualTime();
        var runner = new ScriptedRunner(time) { Outcome = _ => EndpointCheckOutcome.Refused };
        var (monitor, store, notes) = Monitor(runner, time);
        await store.SaveAsync(Draft(EndpointCheckKind.Tcp, "example.com", 443), AdminId, default);

        async Task Minutes(int count)
        {
            for (var i = 0; i < count; i++)
            {
                await monitor.CycleAsync(CancellationToken.None);
                await SettleAsync(monitor);
                time.Advance(TimeSpan.FromSeconds(60));
            }
        }

        await Minutes(5);
        Assert.DoesNotContain(notes.Events, e => e.EventType == NotificationEventTypes.EndpointCheckFailed);
        await Minutes(1);
        var raised = Assert.Single(notes.Events, e => e.EventType == NotificationEventTypes.EndpointCheckFailed);
        Assert.Equal("web", raised.ServerId);

        runner.Outcome = _ => EndpointCheckOutcome.Passed;
        await Minutes(6);
        Assert.Single(notes.Events, e => e.EventType == NotificationEventTypes.Recovered(NotificationEventTypes.EndpointCheckFailed));
        Assert.Equal(12, runner.Ran.Count);
    }
}
