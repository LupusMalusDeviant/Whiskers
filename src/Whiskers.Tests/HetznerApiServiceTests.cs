using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Services.Hetzner;

namespace Whiskers.Tests;

/// <summary>Contract 0018 part D: the request bodies of the three Hetzner calls the rescue bootstrap adds —
/// <c>enable_rescue</c> with <c>ssh_keys</c>, <c>POST /ssh_keys</c> and <c>DELETE /ssh_keys/{id}</c>.</summary>
public sealed class HetznerApiServiceTests
{
    private sealed class Recorder(string answer) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Body, string? Auth)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(ct), request.Headers.Authorization?.ToString()));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
        }
    }

    private static (HetznerApiService Api, Recorder Http) Api(string answer = "{}")
    {
        var recorder = new Recorder(answer);
        return (new HetznerApiService(new HttpClient(recorder), NullLogger<HetznerApiService>.Instance), recorder);
    }

    [Fact]
    public async Task Enable_rescue_with_keys_sends_linux64_and_the_key_ids()
    {
        var (api, http) = Api("{\"action\":{\"id\":1},\"root_password\":\"x\"}");
        var token = "tok-" + Guid.NewGuid().ToString("N");

        await api.EnableRescueWithKeysAsync(token, 20, [77, 78]);

        var request = Assert.Single(http.Requests);
        Assert.Equal((HttpMethod.Post, "/v1/servers/20/actions/enable_rescue"), (request.Method, request.Path));
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("linux64", body.RootElement.GetProperty("type").GetString());
        Assert.Equal([77L, 78L], body.RootElement.GetProperty("ssh_keys").EnumerateArray().Select(e => e.GetInt64()));
        Assert.Equal("Bearer " + token, request.Auth);
    }

    [Fact]
    public async Task Create_ssh_key_posts_name_public_key_and_labels()
    {
        var (api, http) = Api("{\"ssh_key\":{\"id\":77,\"name\":\"whiskers-bootstrap-web-new-0123abcd\"}}");
        var publicKey = "ssh-ed25519 " + FakeSshToolchain.KeyText('g') + " whiskers-bootstrap-web-new-0123abcd";

        var key = await api.CreateSshKeyAsync("tok", "whiskers-bootstrap-web-new-0123abcd", publicKey,
            new Dictionary<string, string> { ["managed-by"] = "whiskers" });

        Assert.Equal(77, key.Id);
        var request = Assert.Single(http.Requests);
        Assert.Equal((HttpMethod.Post, "/v1/ssh_keys"), (request.Method, request.Path));
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("whiskers-bootstrap-web-new-0123abcd", body.RootElement.GetProperty("name").GetString());
        Assert.Equal(publicKey, body.RootElement.GetProperty("public_key").GetString());
        Assert.Equal("whiskers", body.RootElement.GetProperty("labels").GetProperty("managed-by").GetString());
    }

    [Fact]
    public async Task Create_ssh_key_without_a_key_in_the_answer_throws()
    {
        var (api, _) = Api("{}");

        await Assert.ThrowsAsync<InvalidOperationException>(() => api.CreateSshKeyAsync("tok", "n", "ssh-ed25519 " + FakeSshToolchain.KeyText('g'),
            new Dictionary<string, string>()));
    }

    [Fact]
    public async Task Delete_ssh_key_sends_delete_to_the_key()
    {
        var (api, http) = Api();

        await api.DeleteSshKeyAsync("tok", 77);

        var request = Assert.Single(http.Requests);
        Assert.Equal((HttpMethod.Delete, "/v1/ssh_keys/77"), (request.Method, request.Path));
        Assert.True(string.IsNullOrEmpty(request.Body));
    }
}
