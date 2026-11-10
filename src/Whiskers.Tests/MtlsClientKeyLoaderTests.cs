using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Services.Certificates;
using Whiskers.Services.Vault;

namespace Whiskers.Tests;

// Contract 0015 Part A [mTLS layer]: the client key is sealed at rest and loaded in memory; the TLS client presents it.
public sealed class MtlsClientKeyLoaderTests
{
    [Fact]
    public async Task The_loaded_certificate_presents_the_sealed_key_in_a_tls_handshake()
    {
        using var w = new CredentialWorld();
        var paths = await w.Store.IssueAsync(w.Server("rabenhof"), 1, default);
        Assert.True(SealedKeyFiles.IsSealedFile(paths.Key));
        var record = w.With("rabenhof", paths);

        using var client = w.KeyLoader.Load(record);
        Assert.True(client.HasPrivateKey);
        using var fileCert = X509Certificate2.CreateFromPem(File.ReadAllText(paths.Cert));
        Assert.Equal(fileCert.Thumbprint, client.Thumbprint);

        var presented = await HandshakeAsync(w.Ca, client);
        Assert.Equal(fileCert.Thumbprint, presented);
    }

    [Fact]
    public async Task The_shared_plaintext_key_still_loads_until_it_is_sealed()
    {
        using var w = new CredentialWorld();
        using var client = w.KeyLoader.Load(w.Server("rabenhof"));
        Assert.True(client.HasPrivateKey);
        Assert.Equal(client.Thumbprint, await HandshakeAsync(w.Ca, client));
    }

    [Fact]
    public async Task A_key_moved_to_another_slot_or_a_locked_vault_is_refused_with_its_reason()
    {
        using var w = new CredentialWorld();
        var paths = await w.Store.IssueAsync(w.Server("rabenhof"), 1, default);
        var other = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(paths.Key)!)!, "g1-s9");
        Directory.CreateDirectory(other);
        File.Copy(paths.Cert, Path.Combine(other, "client.crt"));
        File.Copy(paths.Key, Path.Combine(other, "client.key"));
        var moved = w.With("rabenhof", new MtlsClientCredentialPaths(Path.Combine(other, "client.crt"), Path.Combine(other, "client.key"), paths.Ca, 1));
        Assert.Throws<SealedKeyException>(() => w.KeyLoader.Load(moved));

        // A vault opened with another key is locked: the cause is named, nothing falls back to another key.
        var vaultDir = Path.Combine(w.Root, "other-vault");
        Directory.CreateDirectory(vaultDir);
        var locked = new VaultService(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["VAULT_KEY"] = "one" }).Build(),
            NullLogger<VaultService>.Instance, Path.Combine(vaultDir, "vault.json"));
        await locked.InitializeAsync();
        var wrong = new VaultService(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["VAULT_KEY"] = "two" }).Build(),
            NullLogger<VaultService>.Instance, Path.Combine(vaultDir, "vault.json"));
        await wrong.InitializeAsync();
        var loader = new MtlsClientKeyLoader(new SealedKeyFiles(wrong, w.Paths), w.Paths);
        var refused = Assert.Throws<SealedKeyException>(() => loader.Load(w.With("rabenhof", paths)));
        Assert.Equal(SealedKeyFiles.VaultLockedReason, refused.Message);
    }

    [Fact]
    public async Task Without_a_usable_vault_no_client_key_is_made()
    {
        using var w = new CredentialWorld();
        var off = new VaultService(new ConfigurationBuilder().Build(), NullLogger<VaultService>.Instance, Path.Combine(w.Root, "vault.json"));
        await off.InitializeAsync();
        var store = new MtlsClientCredentialStore(w.Fleet, NullLogger<MtlsClientCredentialStore>.Instance, w.Paths, w.Servers,
            new SealedKeyFiles(off, w.Paths));

        await Assert.ThrowsAsync<SealedKeyException>(() => store.IssueAsync(w.Server("rabenhof"), 1, default));
        Assert.False(Directory.Exists(Path.Combine(w.MtlsDir, MtlsClientCredentialStore.HostsDirName, "rabenhof")));
        Assert.Empty(w.Fleet.Commands);   // nothing was sent to step-ca
    }

    /// <summary>A TLS server on loopback with a fleet proxy certificate that requires a client certificate; returns the
    /// thumbprint of the certificate the client presented (the handshake proves it holds the key).</summary>
    private static async Task<string?> HandshakeAsync(TestCa ca, X509Certificate2 clientCert)
    {
        using var issued = ca.IssueServer("127.0.0.1");
        using var serverCert = X509CertificateLoader.LoadPkcs12(issued.Export(X509ContentType.Pkcs12), null);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            string? presented = null;
            var serverSide = Task.Run(async () =>
            {
                using var accepted = await listener.AcceptTcpClientAsync();
                await using var ssl = new SslStream(accepted.GetStream(), false, (_, cert, _, _) =>
                {
                    presented = cert is null ? null : new X509Certificate2(cert).Thumbprint;
                    return cert is not null;
                });
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = serverCert,
                    ClientCertificateRequired = true,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                });
                await ssl.WriteAsync(new byte[] { 1 });
            });

            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            await using var client = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
            await client.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "127.0.0.1",
                ClientCertificates = new X509CertificateCollection { clientCert },
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            });
            var buffer = new byte[1];
            _ = await client.ReadAsync(buffer);
            await serverSide.WaitAsync(TimeSpan.FromSeconds(20));
            return presented;
        }
        finally
        {
            listener.Stop();
        }
    }
}
