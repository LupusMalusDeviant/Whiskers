using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Services.Cve;
using Whiskers.Services.Server;

namespace Whiskers.Tests;

/// <summary>
/// A scan that stops happening, and says so in a language nobody reads.
///
/// <para>Trivy's JSON for a large image runs to several megabytes; the host executor capped command output at
/// 1 MB and appended "… (Ausgabe gekürzt)" to what was left. The scanner then handed that to a JSON reader,
/// which reported "'0xE2' is an invalid start of a value" — the first byte of the ellipsis. Every cycle, for
/// months, the Authentik image logged a parser error and silently kept its stale CVE results.</para>
///
/// <para>These tests are about the confusion rather than the parser: a document that was cut off must be
/// reported as cut off, because that is a limit to raise, and a document that is genuinely malformed must
/// still fail loudly, because that is a bug to find. Collapsing the two is what cost six months.</para>
/// </summary>
public class TrivyOutputTruncationTests
{
    /// <summary>Records what the scanner asked for, and answers with whatever the test needs it to.</summary>
    private sealed class RecordingExecutor(CommandResult result) : IHostCommandExecutor
    {
        public int? RequestedCap { get; private set; }
        public string LastCommand { get; private set; } = "";

        public Task<CommandResult> ExecuteAsync(string serverId, string command, TimeSpan? timeout = null,
            CancellationToken ct = default, int? maxOutputChars = null)
        {
            RequestedCap = maxOutputChars;
            LastCommand = command;
            return Task.FromResult(result);
        }
    }

    private static TrivyScanner Scanner(CommandResult result, out RecordingExecutor executor)
    {
        executor = new RecordingExecutor(result);
        return new TrivyScanner(executor, new StaticOptionsMonitor<CveMonitorSettings>(new CveMonitorSettings()),
            NullLogger<TrivyScanner>.Instance);
    }

    /// <summary>A minimal but valid Trivy document with one finding.</summary>
    private const string OneFinding = """
        {
          "Metadata": { "OS": { "Family": "debian", "Name": "12" } },
          "Results": [
            { "Vulnerabilities": [ { "VulnerabilityID": "CVE-2026-1111", "PkgName": "openssl",
                                     "Severity": "HIGH" } ] }
          ]
        }
        """;

    // ---- the executor's cap: the mechanism the whole bug turned on -------------------------------------

    [Fact]
    public async Task Output_within_the_cap_is_returned_whole_and_reported_as_complete()
    {
        var payload = new string('x', 5_000);

        var (text, truncated) = await HostCommandExecutor.ReadCappedAsync(
            new StringReader(payload), cap: 10_000, CancellationToken.None);

        Assert.False(truncated);
        Assert.Equal(payload, text);
    }

    [Fact]
    public async Task Output_beyond_the_cap_is_cut_AND_says_that_it_was_cut()
    {
        // The half that was missing. Cutting was always correct — a runaway command must not exhaust memory.
        // Failing to SAY so is what turned a raised-limit problem into a parser mystery, so the flag matters
        // more here than the text does.
        var payload = string.Concat(Enumerable.Repeat(new string('x', 99) + "\n", 50));

        var (text, truncated) = await HostCommandExecutor.ReadCappedAsync(
            new StringReader(payload), cap: 1_000, CancellationToken.None);

        Assert.True(truncated);
        Assert.StartsWith(payload[..999], text);   // ten complete lines; the eleventh began inside the cap
        Assert.True(text.Length < payload.Length);
    }

    [Theory]
    [InlineData("ok line\nclone https://deploy:canary-value-r124@git.example.org/r.git\n")]
    [InlineData("one long line ending in https://deploy:canary-value-r124@git.example.org/r.git")]
    [InlineData("{\"repo\":\"https://deploy:canary-value-r124@git.example.org/r.git\"}")]   // no boundary: dropped whole
    public async Task A_credential_split_by_the_cut_is_dropped_whole(string output)
    {
        // R1-24: the cut must not leave the beginning of a secret the URL rule can no longer recognise.
        var cap = output.IndexOf("canary", StringComparison.Ordinal) + 6;

        var (text, truncated) = await HostCommandExecutor.ReadCappedAsync(new StringReader(output), cap, CancellationToken.None);

        Assert.True(truncated);
        Assert.DoesNotContain("canary", text);
        Assert.DoesNotContain("deploy:", text);
    }

    [Fact]
    public async Task The_marker_that_a_parser_chokes_on_is_still_the_marker_that_is_appended()
    {
        // Pins the actual byte from the incident. If the marker ever changes, this test is where somebody
        // learns that the character a JSON reader reported as "0xE2" came from us, not from Trivy.
        var (text, _) = await HostCommandExecutor.ReadCappedAsync(
            new StringReader(new string('x', 9) + "\n" + new string('x', 90)), cap: 12, CancellationToken.None);

        var marker = text[9..];
        Assert.Contains('…', marker);
        Assert.Equal(0xE2, System.Text.Encoding.UTF8.GetBytes(marker.TrimStart('\n'))[0]);
    }

    // ---- the scanner: cut-off and malformed must not look alike ---------------------------------------

    [Fact]
    public async Task A_cut_off_scan_is_reported_as_cut_off_and_yields_no_findings()
    {
        // THE assertion. A truncated document parsed leniently would produce a partial CVE list that reads
        // exactly like a clean bill of health for everything the cut removed.
        var scanner = Scanner(new CommandResult
        {
            ExitCode = 0,
            Output = OneFinding[..40] + "\n… (Ausgabe gekürzt)",
            OutputTruncated = true
        }, out _);

        var result = await scanner.ScanContainerImageAsync("local", "c1", "authentik-worker-1",
            "ghcr.io/goauthentik/server:2026.5.3");

        Assert.Empty(result.Findings);
        Assert.Contains("cut off", result.Error);
        Assert.Contains("MB", result.Error);
        // And explicitly NOT the message that sent everyone looking at the parser for months.
        Assert.DoesNotContain("invalid start of a value", result.Error);
    }

    [Fact]
    public async Task Genuinely_malformed_output_still_fails_loudly()
    {
        // The counter-proof: the truncation guard must not become a catch-all that swallows real corruption.
        // Output that was never cut and still cannot be read is a bug to find, and has to keep saying so.
        var scanner = Scanner(new CommandResult
        {
            ExitCode = 0,
            Output = "{ \"Results\": [ this is not json",
            OutputTruncated = false
        }, out _);

        var result = await scanner.ScanContainerImageAsync("local", "c1", "x", "img:1");

        Assert.Empty(result.Findings);
        Assert.False(string.IsNullOrEmpty(result.Error));
        Assert.DoesNotContain("cut off", result.Error);
    }

    [Fact]
    public async Task A_complete_document_is_parsed_as_before()
    {
        var scanner = Scanner(new CommandResult { ExitCode = 0, Output = OneFinding }, out _);

        var result = await scanner.ScanContainerImageAsync("local", "c1", "x", "img:1");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("CVE-2026-1111", finding.CveId);
        Assert.Equal("debian 12", finding.OsContext);
        Assert.True(string.IsNullOrEmpty(result.Error));
    }

    // ---- and the fix has to actually reach production -------------------------------------------------

    [Fact]
    public async Task The_scanner_asks_for_a_cap_large_enough_for_a_real_image()
    {
        // Without this the rest of the file passes while nothing improves: the guard would report truncation
        // correctly and the scan would go on being truncated. The largest output measured in the field was
        // 3.4 MB, so anything at or below that is not a fix.
        var scanner = Scanner(new CommandResult { ExitCode = 0, Output = OneFinding }, out var executor);

        await scanner.ScanContainerImageAsync("local", "c1", "x", "img:1");

        Assert.NotNull(executor.RequestedCap);
        Assert.True(executor.RequestedCap >= 8 * 1024 * 1024,
            $"asked for {executor.RequestedCap} characters — a real image needs several megabytes");
    }
}
