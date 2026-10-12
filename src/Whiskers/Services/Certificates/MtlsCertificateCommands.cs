using System.Text;

namespace Whiskers.Services.Certificates;

/// <summary>
/// Shell scripts for issuing and renewing an mTLS proxy certificate. Pure builders, so every line that reaches a
/// host can be asserted in a test.
///
/// <para>Each script travels base64-encoded and is decoded into <c>sh</c> on the target, so no value ever meets
/// shell quoting. The values inside come from <see cref="MtlsIdentity"/> (allow-listed) or are base64 themselves.</para>
/// </summary>
public static class MtlsCertificateCommands
{
    public const string CertDir = "/opt/dockerproxy/certs";
    public const string GuardScript = "/opt/dockerproxy/renew-guard.sh";
    public const string StepCaContainer = "step-ca";
    /// <summary>How long a renewed host waits for Whiskers to confirm before it restores the old certificate.</summary>
    public static readonly TimeSpan GuardDelay = TimeSpan.FromMinutes(3);

    /// <summary>Wraps a script so it runs as root whether the session is root (mTLS helper) or a sudo user (SSH).</summary>
    public static string AsRootScript(string script)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        return $"echo {b64} | base64 -d | {{ if [ \"$(id -u)\" -eq 0 ]; then sh; else sudo sh; fi; }}";
    }

    /// <summary>On the host: a fresh P-256 key that never leaves it, and a CSR for <paramref name="id"/>. Prints the
    /// CSR base64-encoded. The key goes to <c>server.key.new</c> so a running proxy keeps its current one.</summary>
    public static string GenerateKeyAndCsr(MtlsIdentity id) => AsRootScript($"""
        set -e
        mkdir -p {CertDir}
        cd {CertDir}
        umask 077
        rm -f server.key.new server.csr.new
        openssl req -new -newkey ec -pkeyopt ec_paramgen_curve:P-256 -nodes -keyout server.key.new -out server.csr.new -subj /CN={id.CommonName} -addext subjectAltName={id.OpenSslSubjectAltName}
        base64 -w0 server.csr.new
        rm -f server.csr.new
        """);

    /// <summary>On Badwolf: has step-ca sign the CSR (leaf + intermediate, one year) and prints the bundle base64.
    /// The CSR is checked by Whiskers before this runs; step's leaf template copies subject and SANs from it.</summary>
    public static string SignCsr(string csrB64, string commonName)
    {
        var file = $"whiskers-{commonName}.csr";
        return AsRootScript($$"""
            set -e
            echo {{csrB64}} | base64 -d > /opt/step-ca/certs/{{file}}
            chmod 644 /opt/step-ca/certs/{{file}}
            out=$(docker exec {{StepCaContainer}} step certificate sign /home/step/certs/{{file}} /home/step/certs/intermediate_ca.crt /home/step/secrets/intermediate_ca_key --password-file /home/step/secrets/password --not-after 8760h --bundle) || { rm -f /opt/step-ca/certs/{{file}}; exit 1; }
            rm -f /opt/step-ca/certs/{{file}}
            printf '%s\n' "$out" | base64 -w0
            """);
    }

    /// <summary>On Badwolf: root + intermediate, the chain ghostunnel must present to the leaf-only .NET client.</summary>
    public static string ReadCaBundle() =>
        "cat /opt/step-ca/certs/root_ca.crt /opt/step-ca/certs/intermediate_ca.crt | base64 -w0";

    /// <summary>On the host: writes the new certificate and CA bundle next to the new key and refuses unless the
    /// certificate belongs to that key.</summary>
    public static string StageCertificate(string certB64, string caBundleB64) => AsRootScript($$"""
        set -e
        cd {{CertDir}}
        umask 077
        echo {{certB64}} | base64 -d > server.crt.new
        umask 022
        echo {{caBundleB64}} | base64 -d > ca.crt.new
        [ "$(openssl x509 -noout -pubkey -in server.crt.new)" = "$(openssl pkey -pubout -in server.key.new)" ] || { echo 'certificate does not match the new key' >&2; exit 1; }
        echo staged
        """);

    /// <summary>Onboarding: nothing runs yet, so the staged files simply become the live ones.</summary>
    public static string ActivateFresh() => AsRootScript($"""
        set -e
        cd {CertDir}
        mv -f server.key.new server.key
        mv -f server.crt.new server.crt
        mv -f ca.crt.new ca.crt
        echo activated
        """);

    /// <summary>
    /// Renewal: keeps the current files as <c>.prev</c>, arms a dead-man timer, swaps in the new files and restarts
    /// ghostunnel detached.
    ///
    /// <para>Every command Whiskers sends to this host passes through that very ghostunnel. If the new certificate
    /// does not work, Whiskers can no longer reach the host to undo it — so the host undoes it itself: unless
    /// <see cref="Confirm"/> has left <c>.renew-ok</c> by the time the timer fires, the old files come back.</para>
    /// </summary>
    public static string ActivateWithGuard(string unitSuffix) => AsRootScript($"""
        set -e
        cd {CertDir}
        cp -p server.key server.key.prev
        cp -p server.crt server.crt.prev
        cp -p ca.crt ca.crt.prev
        rm -f .renew-ok
        echo {Convert.ToBase64String(Encoding.UTF8.GetBytes(GuardBody()))} | base64 -d > {GuardScript}
        systemd-run --collect --unit=whiskers-cert-guard-{unitSuffix} --on-active={(int)GuardDelay.TotalSeconds} /bin/sh {GuardScript} >/dev/null 2>&1
        mv -f server.key.new server.key
        mv -f server.crt.new server.crt
        mv -f ca.crt.new ca.crt
        systemd-run --collect --unit=whiskers-ghostunnel-restart-{unitSuffix} --on-active=3 docker restart ghostunnel >/dev/null 2>&1
        echo activated
        """);

    internal static string GuardBody() => $"""
        cd {CertDir} || exit 0
        if [ -f .renew-ok ]; then
          rm -f .renew-ok
        elif [ -f server.key.prev ]; then
          mv -f server.key.prev server.key
          mv -f server.crt.prev server.crt
          mv -f ca.crt.prev ca.crt
          docker restart ghostunnel
        fi
        """;

    /// <summary>Renewal, sent through the NEW certificate: proves it works, stops the guard from rolling back and
    /// removes the old key — the old certificate is still valid for months and must not stay usable.</summary>
    public static string Confirm() => AsRootScript($"""
        set -e
        cd {CertDir}
        touch .renew-ok
        rm -f server.key.prev server.crt.prev ca.crt.prev
        echo confirmed
        """);
}
