using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Models.Dns;
using Whiskers.Services.Dns;

namespace Whiskers.Tests;

/// <summary>The provider-neutral layer: idempotent set (create / update / unchanged), delete, the zone fence,
/// name normalisation and per-type value validation — against an in-memory fake provider.</summary>
public class DnsRecordServiceTests
{
    private sealed class FakeProvider : IDnsProviderClient
    {
        public readonly List<DnsRecord> Records = new();
        public readonly List<string> Calls = new();
        private int _nextId = 100;

        public string ProviderId => "infomaniak";

        public string? LastToken { get; private set; }

        public Task<List<DnsRecord>> ListRecordsAsync(string token, string zone, CancellationToken ct = default)
        {
            LastToken = token;
            Calls.Add($"list {zone}");
            return Task.FromResult(Records.ToList());
        }

        public Task<DnsRecord> CreateRecordAsync(string token, string zone, DnsRecord record, CancellationToken ct = default)
        {
            Calls.Add($"create {zone} {record}");
            var created = record with { Id = (_nextId++).ToString() };
            Records.Add(created);
            return Task.FromResult(created);
        }

        public Task<DnsRecord> UpdateRecordAsync(string token, string zone, string recordId, DnsRecord record, CancellationToken ct = default)
        {
            Calls.Add($"update {zone} #{recordId} {record}");
            var idx = Records.FindIndex(r => r.Id == recordId);
            Records[idx] = Records[idx] with { Value = record.Value, Ttl = record.Ttl };
            return Task.FromResult(Records[idx]);
        }

        public Task DeleteRecordAsync(string token, string zone, string recordId, CancellationToken ct = default)
        {
            Calls.Add($"delete {zone} #{recordId}");
            Records.RemoveAll(r => r.Id == recordId);
            return Task.CompletedTask;
        }
    }

    private sealed class Monitor : IOptionsMonitor<DnsSettings>
    {
        public Monitor(DnsSettings v) => CurrentValue = v;
        public DnsSettings CurrentValue { get; set; }
        public DnsSettings Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<DnsSettings, string?> listener) => null;
    }

    private static (DnsRecordService svc, FakeProvider provider) Make(params string[] allowedZones)
    {
        var provider = new FakeProvider();
        var settings = new DnsSettings { Provider = "infomaniak", ApiToken = "t", AllowedZones = allowedZones.ToList(), DefaultTtl = 300 };
        var svc = new DnsRecordService(new[] { provider }, new Monitor(settings), NullLogger<DnsRecordService>.Instance);
        return (svc, provider);
    }

    [Fact]
    public async Task Set_creates_when_nothing_matches_and_reports_no_before()
    {
        var (svc, p) = Make();

        var r = await svc.SetAsync("lupusmalus.dev", "holler.app", "a", "1.2.3.4", null);

        Assert.Equal(DnsSetAction.Created, r.Action);
        Assert.Null(r.Before);
        Assert.Equal(new DnsRecord("100", "holler.app", "A", "1.2.3.4", 300), r.After);
        Assert.Single(p.Records);
    }

    [Fact]
    public async Task Set_updates_the_existing_record_of_same_name_and_type_instead_of_duplicating()
    {
        var (svc, p) = Make();
        p.Records.Add(new DnsRecord("1", "holler.app", "A", "1.2.3.4", 300));

        var r = await svc.SetAsync("lupusmalus.dev", "holler.app.lupusmalus.dev", "A", "5.6.7.8", 600);

        Assert.Equal(DnsSetAction.Updated, r.Action);
        Assert.Equal("1.2.3.4", r.Before!.Value);
        Assert.Equal("5.6.7.8", r.After.Value);
        Assert.Equal(600, r.After.Ttl);
        Assert.Equal("1", r.After.Id);
        Assert.Single(p.Records);
        Assert.Contains(p.Calls, c => c.StartsWith("update lupusmalus.dev #1"));
        Assert.DoesNotContain(p.Calls, c => c.StartsWith("create"));
    }

    [Fact]
    public async Task Set_with_identical_value_and_ttl_is_unchanged_and_writes_nothing()
    {
        var (svc, p) = Make();
        p.Records.Add(new DnsRecord("1", "holler.app", "A", "1.2.3.4", 300));

        var r = await svc.SetAsync("lupusmalus.dev", "holler.app", "A", "1.2.3.4", 300);

        Assert.Equal(DnsSetAction.Unchanged, r.Action);
        Assert.Equal(new[] { "list lupusmalus.dev" }, p.Calls);
    }

    [Fact]
    public async Task Set_treats_quoted_txt_from_the_provider_as_equal_to_the_bare_value()
    {
        var (svc, p) = Make();
        p.Records.Add(new DnsRecord("1", "@", "TXT", "\"v=spf1 -all\"", 300));

        var r = await svc.SetAsync("lupusmalus.dev", "@", "TXT", "v=spf1 -all", 300);

        Assert.Equal(DnsSetAction.Unchanged, r.Action);
    }

    [Fact]
    public async Task Set_collapses_several_records_of_the_same_name_and_type_onto_one()
    {
        var (svc, p) = Make();
        p.Records.Add(new DnsRecord("1", "rr", "A", "1.1.1.1", 300));
        p.Records.Add(new DnsRecord("2", "rr", "A", "2.2.2.2", 300));

        var r = await svc.SetAsync("lupusmalus.dev", "rr", "A", "3.3.3.3", 300);

        Assert.Equal(DnsSetAction.Updated, r.Action);
        var only = Assert.Single(p.Records);
        Assert.Equal("3.3.3.3", only.Value);
        Assert.Contains("delete lupusmalus.dev #2", p.Calls);
    }

    [Fact]
    public async Task Set_refuses_a_cname_next_to_other_data_and_vice_versa()
    {
        var (svc, p) = Make();
        p.Records.Add(new DnsRecord("1", "www", "A", "1.2.3.4", 300));
        p.Records.Add(new DnsRecord("2", "alias", "CNAME", "www.lupusmalus.dev", 300));

        var ex1 = await Assert.ThrowsAsync<ArgumentException>(() => svc.SetAsync("lupusmalus.dev", "www", "CNAME", "x.example", null));
        Assert.Contains("CNAME", ex1.Message);
        var ex2 = await Assert.ThrowsAsync<ArgumentException>(() => svc.SetAsync("lupusmalus.dev", "alias", "A", "1.2.3.4", null));
        Assert.Contains("CNAME", ex2.Message);
        Assert.Equal(2, p.Records.Count);
    }

    [Fact]
    public async Task Delete_removes_every_match_and_returns_them()
    {
        var (svc, p) = Make();
        p.Records.Add(new DnsRecord("1", "x", "A", "1.1.1.1", 300));
        p.Records.Add(new DnsRecord("2", "x", "A", "2.2.2.2", 300));
        p.Records.Add(new DnsRecord("3", "x", "TXT", "keep", 300));

        var removed = await svc.DeleteAsync("lupusmalus.dev", "x.lupusmalus.dev.", "A");

        Assert.Equal(2, removed.Count);
        var left = Assert.Single(p.Records);
        Assert.Equal("TXT", left.Type);
    }

    [Fact]
    public async Task Delete_of_a_missing_record_is_a_noop()
    {
        var (svc, p) = Make();
        Assert.Empty(await svc.DeleteAsync("lupusmalus.dev", "nope", "A"));
        Assert.Equal(new[] { "list lupusmalus.dev" }, p.Calls);
    }

    [Fact]
    public async Task Zone_fence_blocks_zones_outside_the_allow_list()
    {
        var (svc, p) = Make("lupusmalus.dev");

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => svc.ListAsync("other.dev"));
        Assert.Contains("nicht freigegeben", ex.Message);
        Assert.Empty(p.Calls);

        await svc.ListAsync("LupusMalus.dev."); // case + trailing dot are normalised, not rejected
        Assert.Single(p.Calls);
    }

    [Fact]
    public async Task Without_a_token_nothing_is_called()
    {
        var provider = new FakeProvider();
        var svc = new DnsRecordService(new[] { provider }, new Monitor(new DnsSettings { ApiToken = "" }), NullLogger<DnsRecordService>.Instance);

        Assert.False(svc.IsConfigured);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ListAsync("lupusmalus.dev"));
        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task A_token_kept_in_the_vault_is_read_from_it_and_a_locked_vault_is_an_error()
    {
        // R1-29: app-settings.json names the vault key; the token itself is only in the vault.
        var dir = Path.Combine(Path.GetTempPath(), "sw-tests", "dns-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "vault.json");
            var token = "DNSTOKEN-" + Guid.NewGuid().ToString("N");
            var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["VAULT_KEY"] = "dns-test-passphrase" }).Build();
            var vault = new Whiskers.Services.Vault.VaultService(config, NullLogger<Whiskers.Services.Vault.VaultService>.Instance, path);
            await vault.InitializeAsync();
            await vault.SetSecretAsync("dns:apitoken:1", token);

            var provider = new FakeProvider();
            var settings = new DnsSettings { Provider = "infomaniak", ApiTokenRef = "dns:apitoken:1" };
            var svc = new DnsRecordService(new[] { provider }, new Monitor(settings), NullLogger<DnsRecordService>.Instance, vault);
            Assert.True(svc.IsConfigured);
            await svc.ListAsync("lupusmalus.dev");
            Assert.Equal(token, provider.LastToken);

            var disabled = new Whiskers.Services.Vault.VaultService(
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
                NullLogger<Whiskers.Services.Vault.VaultService>.Instance, path);
            await disabled.InitializeAsync();
            var locked = new DnsRecordService(new[] { new FakeProvider() }, new Monitor(settings), NullLogger<DnsRecordService>.Instance, disabled);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => locked.ListAsync("lupusmalus.dev"));
            Assert.Contains("Vault", ex.Message);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Unknown_provider_id_is_reported_with_the_supported_ones()
    {
        var provider = new FakeProvider();
        var svc = new DnsRecordService(new[] { provider }, new Monitor(new DnsSettings { Provider = "cloudflare", ApiToken = "t" }), NullLogger<DnsRecordService>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ListAsync("lupusmalus.dev"));
        Assert.Contains("infomaniak", ex.Message);
    }

    [Theory]
    [InlineData("@", "@")]
    [InlineData("", "@")]
    [InlineData("lupusmalus.dev", "@")]
    [InlineData("lupusmalus.dev.", "@")]
    [InlineData("Holler.App", "holler.app")]
    [InlineData("holler.app.lupusmalus.dev", "holler.app")]
    [InlineData("holler.app.lupusmalus.dev.", "holler.app")]
    [InlineData("*.apps", "*.apps")]
    [InlineData("_acme-challenge", "_acme-challenge")]
    public void NormalizeName_strips_the_zone_and_maps_the_apex(string input, string expected)
        => Assert.Equal(expected, DnsRecordService.NormalizeName(input, "lupusmalus.dev"));

    [Theory]
    [InlineData("bad name")]
    [InlineData("-lead")]
    [InlineData("a..b")]
    public void NormalizeName_rejects_invalid_labels(string input)
        => Assert.Throws<ArgumentException>(() => DnsRecordService.NormalizeName(input, "lupusmalus.dev"));

    [Theory]
    [InlineData("A", "1.2.3.4", "1.2.3.4")]
    [InlineData("AAAA", "2001:DB8::1", "2001:db8::1")]
    [InlineData("CNAME", "Target.Example.Org.", "target.example.org")]
    [InlineData("TXT", "v=spf1 -all", "v=spf1 -all")]
    public void NormalizeValue_accepts_and_canonicalises_valid_values(string type, string input, string expected)
        => Assert.Equal(expected, DnsRecordService.NormalizeValue(type, input));

    [Theory]
    [InlineData("A", "2001:db8::1")]
    [InlineData("A", "1.2.3")]
    [InlineData("A", "300.1.1.1")]
    [InlineData("AAAA", "1.2.3.4")]
    [InlineData("CNAME", "not a host")]
    [InlineData("CNAME", "")]
    [InlineData("TXT", "line\nbreak")]
    public void NormalizeValue_rejects_values_that_do_not_fit_the_type(string type, string input)
        => Assert.Throws<ArgumentException>(() => DnsRecordService.NormalizeValue(type, input));

    [Theory]
    [InlineData("mx")]
    [InlineData("NS")]
    [InlineData("SOA")]
    [InlineData("")]
    public void NormalizeType_only_allows_the_four_supported_types(string type)
        => Assert.Throws<ArgumentException>(() => DnsRecordService.NormalizeType(type));

    [Fact]
    public async Task Ttl_outside_the_providers_range_is_rejected_before_any_call()
    {
        var (svc, p) = Make();
        await Assert.ThrowsAsync<ArgumentException>(() => svc.SetAsync("lupusmalus.dev", "x", "A", "1.2.3.4", 10));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.SetAsync("lupusmalus.dev", "x", "A", "1.2.3.4", 100000));
        Assert.Empty(p.Calls);
    }
}
