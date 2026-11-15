using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Services.Certificates;
using Whiskers.Services.Server;

namespace Whiskers.Tests;

/// <summary>Contract 0017 part E: the host's CSR answer never reaches a command line on the CA host (E1), and a CSR or
/// issued certificate names exactly the expected identity, counted over every kind of SAN (E2).</summary>
public sealed class MtlsCsrReencodeTests : IDisposable
{
    private static readonly MtlsIdentity Web1 = MtlsIdentity.ForNewHost("web1", "100.64.0.5");
    private readonly TestCa _ca = new();

    public void Dispose() => _ca.Dispose();

    private static string HonestCsrPem(ECDsa key, MtlsIdentity identity, Action<SubjectAlternativeNameBuilder>? more = null)
    {
        var request = new CertificateRequest($"CN={identity.CommonName}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var dns in identity.DnsNames) san.AddDnsName(dns);
        foreach (var ip in identity.IpAddresses) san.AddIpAddress(IPAddress.Parse(ip));
        more?.Invoke(san);
        request.CertificateExtensions.Add(san.Build());
        return request.CreateSigningRequestPem();
    }

    private static byte[] DerOf(string pem) => Convert.FromBase64String(pem[PemEncoding.Find(pem).Base64Data]);

    private static string B64(string s) => Convert.ToBase64String(Encoding.ASCII.GetBytes(s));

    private static string DecodeRootScript(string command)
    {
        // AsRootScript: echo <base64> | base64 -d | { … sh … }
        var b64 = command["echo ".Length..command.IndexOf(" | base64 -d", StringComparison.Ordinal)];
        return Encoding.UTF8.GetString(Convert.FromBase64String(b64));
    }

    private sealed class RecordingExecutor(string csrAnswer) : IHostCommandExecutor
    {
        public List<(string ServerId, string Command)> Commands { get; } = [];

        public Task<CommandResult> ExecuteAsync(string serverId, string command, TimeSpan? timeout = null,
            CancellationToken ct = default, int? maxOutputChars = null)
        {
            Commands.Add((serverId, command));
            return Task.FromResult(serverId == "local"
                ? new CommandResult { ExitCode = 1, Error = "stopped by the test" }
                : new CommandResult { ExitCode = 0, Output = csrAnswer + "\n" });
        }
    }

    /// <summary>Runs an issue against <paramref name="answer"/> (signing is answered with a failure) and returns the
    /// commands sent to the CA host.</summary>
    private static async Task<List<string>> CaCommandsFor(string answer)
    {
        var exec = new RecordingExecutor(answer);
        var service = new MtlsCertificateService(exec, servers: null!, NullLogger<MtlsCertificateService>.Instance);
        await Assert.ThrowsAnyAsync<Exception>(() => service.IssueForNewHostAsync("web1", Web1, CancellationToken.None));
        return exec.Commands.Where(c => c.ServerId == "local").Select(c => c.Command).ToList();
    }

    // ---- review repros (1.0.0 full review, area Security, findings 1 and 8) ----

    /// <summary>The host's answer to "generate key and CSR" was pasted as it came into the root script run on the CA
    /// host. .NET's base64 decoder skips line breaks and blanks, and the PEM reader skips text in front of the block,
    /// so a managed host could answer with extra lines that the CA host's root shell runs as commands.</summary>
    [Fact]
    public async Task A_managed_hosts_csr_answer_cannot_add_command_lines_to_the_root_script_on_the_ca_host()
    {
        var identity = MtlsIdentity.ForNewHost("web1", "100.64.0.5");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = HonestCsrPem(key, identity);

        // What a compromised host sends back: 16 base64 characters of junk, split so that one line reads as a command,
        // then the honest CSR.
        const string injected = "echo INJECTED";
        var answer = "AAAA\n" + injected + "\n" + Convert.ToBase64String(Encoding.ASCII.GetBytes("\n" + pem));

        var exec = new RecordingExecutor(answer);
        var service = new MtlsCertificateService(exec, servers: null!, NullLogger<MtlsCertificateService>.Instance);

        // The signing step is answered with a failure, so nothing after it runs; what matters is the script it was sent.
        await Assert.ThrowsAnyAsync<Exception>(() => service.IssueForNewHostAsync("web1", identity, CancellationToken.None));

        var local = Assert.Single(exec.Commands, c => c.ServerId == "local");
        var script = DecodeRootScript(local.Command);
        var lines = script.Split('\n').Select(l => l.Trim()).ToList();
        Assert.False(lines.Contains(injected),
            "The root script sent to the CA host contains a line the managed host chose:\n" + script[..Math.Min(script.Length, 400)]);
    }

    /// <summary>A managed host's CSR must name exactly its identity. A SAN of another kind (a URI, an e-mail address)
    /// was invisible to the DNS/IP comparison.</summary>
    [Fact]
    public void A_hosts_csr_with_a_subject_alternative_name_of_another_kind_is_refused()
    {
        var identity = MtlsIdentity.ForNewHost("web1", "100.64.0.5");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = HonestCsrPem(key, identity, san =>
        {
            san.AddUri(new Uri("spiffe://fleet/whiskers-client"));
            san.AddEmailAddress("root@fleet.example");
        });

        var refused = Record.Exception(() => MtlsCertificateValidator.ValidateCsr(pem, identity));

        Assert.True(refused is InvalidOperationException,
            "A CSR that asks for a URI and an e-mail SAN besides the host's own names was accepted for signing.");
    }

    // ---- acceptance (contract 0017 E1, E2) ----

    /// <summary>An inserted line made only of base64-alphabet characters passes the decoder (it skips line breaks). It
    /// must either fail or leave a CA script whose only variable text is base64 Whiskers produced from the verified DER.</summary>
    [Theory]
    [InlineData("reboot")]
    [InlineData("AAAA\nreboot")]
    [InlineData("AAA\nreboot\n=")]
    public async Task An_inserted_line_of_base64_characters_never_reaches_the_ca_script(string inserted)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = HonestCsrPem(key, Web1);
        var honest = B64("\n" + pem);
        string[] answers = [inserted + "\n" + honest, honest + "\n" + inserted, honest[..64] + "\n" + inserted + "\n" + honest[64..]];

        foreach (var answer in answers)
        {
            foreach (var command in await CaCommandsFor(answer))
            {
                // Exactly the script Whiskers builds from the DER it verified: the only variable text is its base64.
                Assert.Equal(MtlsCertificateCommands.SignCsr(DerOf(pem), Web1.CommonName), command);
                Assert.DoesNotContain("reboot", DecodeRootScript(command), StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task The_ca_script_carries_only_a_pem_whiskers_wrote_from_the_verified_der()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = HonestCsrPem(key, Web1);
        var der = DerOf(pem);
        // Line-wrapped like `base64` without -w0 would print it: still base64, but not the text Whiskers sends on.
        var wrapped = string.Join("\n", B64(pem).Chunk(76).Select(c => new string(c)));

        var command = Assert.Single(await CaCommandsFor(wrapped));
        Assert.Equal(MtlsCertificateCommands.SignCsr(der, Web1.CommonName), command);

        var script = DecodeRootScript(command);
        var pemB64 = Convert.ToBase64String(Encoding.ASCII.GetBytes(PemEncoding.WriteString("CERTIFICATE REQUEST", der) + "\n"));
        Assert.Contains($"echo {pemB64} | base64 -d > /opt/step-ca/certs/whiskers-{Web1.CommonName}.csr", script);
        // The answer's line breaks do not survive: the script's base64 is one line Whiskers encoded.
        Assert.DoesNotContain(wrapped, script);
    }

    [Fact]
    public async Task A_second_pem_block_in_the_answer_is_refused_before_the_ca_host()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = HonestCsrPem(key, Web1);

        Assert.Empty(await CaCommandsFor(B64(pem + "\n" + HonestCsrPem(other, Web1))));
        Assert.Empty(await CaCommandsFor(B64(pem + "\n" + PemEncoding.WriteString("CERTIFICATE", [1, 2, 3]))));
        Assert.Empty(await CaCommandsFor(B64(PemEncoding.WriteString("PRIVATE KEY", [1, 2, 3]) + "\n" + pem)));
    }

    [Fact]
    public async Task A_csr_with_data_after_the_request_is_refused_before_the_ca_host()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var der = DerOf(HonestCsrPem(key, Web1));
        var padded = PemEncoding.WriteString("CERTIFICATE REQUEST", [.. der, 0x05, 0x00]);

        Assert.Throws<InvalidOperationException>(() => MtlsCertificateValidator.ValidateCsr(padded, Web1));
        Assert.Empty(await CaCommandsFor(B64(padded)));
    }

    [Fact]
    public void An_answer_with_a_non_base64_character_is_refused()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var honest = B64(HonestCsrPem(key, Web1));
        Assert.Throws<InvalidOperationException>(() => MtlsCertificateValidator.ValidateCsrAnswer(honest + "\ntouch /tmp/x;", Web1));
        Assert.Throws<InvalidOperationException>(() => MtlsCertificateValidator.ValidateCsrAnswer("$(id)\n" + honest, Web1));
        Assert.NotNull(MtlsCertificateValidator.ValidateCsrAnswer(honest, Web1));
    }

    [Fact]
    public void An_issued_certificate_with_a_uri_san_is_refused()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csrKey = MtlsCertificateValidator.ValidateCsr(HonestCsrPem(key, Web1), Web1);
        // step-ca's template copies the CSR's SANs; here the CA hands back one with a URI next to the expected names.
        var bundle = _ca.Sign(HonestCsrPem(key, Web1, san => san.AddUri(new Uri("spiffe://fleet/whiskers-client"))), out var leaf);
        leaf.Dispose();

        var refused = Assert.Throws<InvalidOperationException>(() =>
            MtlsCertificateValidator.ValidateIssued(bundle, _ca.BundlePem, csrKey, Web1));
        Assert.Contains("subject alternative names", refused.Message);

        // The same certificate without the URI passes, so it is the extra name that is refused.
        var clean = _ca.Sign(HonestCsrPem(key, Web1), out var cleanLeaf);
        cleanLeaf.Dispose();
        using var accepted = MtlsCertificateValidator.ValidateIssued(clean, _ca.BundlePem, csrKey, Web1);
        Assert.NotNull(accepted);
    }
}
