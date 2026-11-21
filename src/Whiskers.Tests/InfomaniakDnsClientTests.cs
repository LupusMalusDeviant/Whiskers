using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Models.Dns;
using Whiskers.Services.Dns;

namespace Whiskers.Tests;

/// <summary>The Infomaniak HTTP layer against a scripted <see cref="HttpMessageHandler"/>: paths, bodies,
/// the Bearer header, the envelope (incl. an error that arrives with HTTP 200), and the apex/TXT spellings.
/// No network — a token is never needed here and must never be.</summary>
public class InfomaniakDnsClientTests
{
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public readonly List<(HttpRequestMessage Request, string? Body)> Sent = new();
        private readonly Queue<HttpResponseMessage> _responses = new();

        public ScriptedHandler Reply(HttpStatusCode status, string json)
        {
            _responses.Enqueue(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Sent.Add((request, body));
            if (_responses.Count == 0) throw new InvalidOperationException("no scripted response left for " + request.RequestUri);
            return _responses.Dequeue();
        }
    }

    private static (InfomaniakDnsClient client, ScriptedHandler handler) Make()
    {
        var handler = new ScriptedHandler();
        var client = new InfomaniakDnsClient(new HttpClient(handler), NullLogger<InfomaniakDnsClient>.Instance);
        return (client, handler);
    }

    private const string Token = "test-token-not-real";

    [Fact]
    public async Task List_parses_records_and_normalises_the_apex_spelling()
    {
        var (client, h) = Make();
        h.Reply(HttpStatusCode.OK, """
            {"result":"success","data":[
              {"id":25,"source":".","type":"NS","ttl":3600,"target":"nsany1.infomaniak.com","updated_at":1},
              {"id":5,"source":"holler.app","type":"A","ttl":300,"target":"1.2.3.4"},
              {"id":7,"source":"","type":"TXT","ttl":360,"target":"\"v=spf1 -all\""}
            ]}
            """);

        var records = await client.ListRecordsAsync(Token, "example.com");

        Assert.Equal(3, records.Count);
        Assert.Equal(new DnsRecord("25", "@", "NS", "nsany1.infomaniak.com", 3600), records[0]);
        Assert.Equal(new DnsRecord("5", "holler.app", "A", "1.2.3.4", 300), records[1]);
        Assert.Equal("@", records[2].Name);
        Assert.Equal("\"v=spf1 -all\"", records[2].Value);

        var (req, _) = Assert.Single(h.Sent);
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("https://api.infomaniak.com/2/zones/example.com/records", req.RequestUri!.ToString());
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.Equal(Token, req.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task Create_posts_source_type_target_ttl_and_returns_the_assigned_id()
    {
        var (client, h) = Make();
        h.Reply(HttpStatusCode.OK, """{"result":"success","data":{"id":4711}}""");

        var created = await client.CreateRecordAsync(Token, "example.com", new DnsRecord(null, "holler.app", "A", "1.2.3.4", 300));

        Assert.Equal("4711", created.Id);
        Assert.Equal("holler.app", created.Name);
        var (req, body) = Assert.Single(h.Sent);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.EndsWith("/2/zones/example.com/records", req.RequestUri!.ToString());
        using var doc = JsonDocument.Parse(body!);
        Assert.Equal("holler.app", doc.RootElement.GetProperty("source").GetString());
        Assert.Equal("A", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("1.2.3.4", doc.RootElement.GetProperty("target").GetString());
        Assert.Equal(300, doc.RootElement.GetProperty("ttl").GetInt32());
    }

    [Fact]
    public async Task Create_at_the_apex_sends_the_providers_apex_source()
    {
        var (client, h) = Make();
        h.Reply(HttpStatusCode.OK, """{"result":"success","data":{"id":1,"source":".","type":"A","target":"1.2.3.4","ttl":300}}""");

        var created = await client.CreateRecordAsync(Token, "example.com", new DnsRecord(null, "@", "A", "1.2.3.4", 300));

        using var doc = JsonDocument.Parse(h.Sent[0].Body!);
        Assert.Equal(InfomaniakDnsClient.ApexSource, doc.RootElement.GetProperty("source").GetString());
        Assert.Equal("@", created.Name); // never leaks the wire spelling back to callers
    }

    [Fact]
    public async Task Update_puts_only_target_and_ttl_to_the_record_url()
    {
        var (client, h) = Make();
        h.Reply(HttpStatusCode.OK, """{"result":"success","data":{"id":5,"source":"holler.app","type":"A","target":"5.6.7.8","ttl":600}}""");

        var updated = await client.UpdateRecordAsync(Token, "example.com", "5", new DnsRecord("5", "holler.app", "A", "5.6.7.8", 600));

        var (req, body) = Assert.Single(h.Sent);
        Assert.Equal(HttpMethod.Put, req.Method);
        Assert.EndsWith("/2/zones/example.com/records/5", req.RequestUri!.ToString());
        using var doc = JsonDocument.Parse(body!);
        Assert.Equal("5.6.7.8", doc.RootElement.GetProperty("target").GetString());
        Assert.Equal(600, doc.RootElement.GetProperty("ttl").GetInt32());
        Assert.False(doc.RootElement.TryGetProperty("source", out _));
        Assert.Equal("5.6.7.8", updated.Value);
    }

    [Fact]
    public async Task Delete_hits_the_record_url()
    {
        var (client, h) = Make();
        h.Reply(HttpStatusCode.OK, """{"result":"success","data":true}""");

        await client.DeleteRecordAsync(Token, "example.com", "5");

        var (req, _) = Assert.Single(h.Sent);
        Assert.Equal(HttpMethod.Delete, req.Method);
        Assert.EndsWith("/2/zones/example.com/records/5", req.RequestUri!.ToString());
    }

    [Fact]
    public async Task Http_error_surfaces_status_and_the_apis_description()
    {
        var (client, h) = Make();
        h.Reply(HttpStatusCode.Forbidden, """{"result":"error","error":{"code":"not_authorized","description":"Zone not owned"}}""");

        var ex = await Assert.ThrowsAsync<DnsProviderException>(() => client.ListRecordsAsync(Token, "other.dev"));

        Assert.Equal(403, ex.StatusCode);
        Assert.Contains("HTTP 403", ex.Message);
        Assert.Contains("Zone not owned", ex.Message);
        Assert.Contains("not_authorized", ex.Message);
        Assert.DoesNotContain(Token, ex.Message);
    }

    [Fact]
    public async Task Error_envelope_with_http_200_is_still_an_error()
    {
        var (client, h) = Make();
        h.Reply(HttpStatusCode.OK, """{"result":"error","error":{"code":"validation_failed","description":"target is invalid"}}""");

        var ex = await Assert.ThrowsAsync<DnsProviderException>(() =>
            client.CreateRecordAsync(Token, "example.com", new DnsRecord(null, "x", "A", "1.2.3.4", 300)));

        Assert.Contains("target is invalid", ex.Message);
    }

    [Fact]
    public async Task Non_json_body_is_reported_with_status_and_a_bounded_excerpt()
    {
        var (client, h) = Make();
        h.Reply(HttpStatusCode.BadGateway, "<html>" + new string('x', 1000) + "</html>");

        var ex = await Assert.ThrowsAsync<DnsProviderException>(() => client.ListRecordsAsync(Token, "example.com"));

        Assert.Equal(502, ex.StatusCode);
        Assert.True(ex.Message.Length < 500, "excerpt must be bounded");
    }

    [Fact]
    public async Task Missing_token_fails_before_any_request_is_sent()
    {
        var (client, h) = Make();

        await Assert.ThrowsAsync<DnsProviderException>(() => client.ListRecordsAsync("", "example.com"));

        Assert.Empty(h.Sent);
    }

    [Theory]
    [InlineData(null, "@")]
    [InlineData("", "@")]
    [InlineData(".", "@")]
    [InlineData("@", "@")]
    [InlineData("www", "www")]
    [InlineData("a.b.", "a.b")]
    public void FromSource_maps_every_apex_spelling_to_at(string? source, string expected)
        => Assert.Equal(expected, InfomaniakDnsClient.FromSource(source));
}
