using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pubb.Server;
using Xunit;

public class ServerTests
{
    private static readonly PubbServerOptions Options = new() { AppId = "app/id", Key = "public-key", Secret = "test-secret", ApiHost = "http://localhost:38000/" };
    private const string Accepted = "{\"success\":true,\"accepted\":true,\"channels\":[\"notifications\"],\"publicationId\":\"pub-1\",\"duplicate\":false}";

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public bool Disposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    [Fact]
    public async Task SendsExpectedRequestAndParsesAcceptance()
    {
        using var handler = new Handler(async (request, token) =>
        {
            Assert.Equal("http://localhost:38000/api/apps/app%2Fid/events", request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("Bearer test-secret", request.Headers.Authorization!.ToString());
            Assert.Equal("event-1", request.Headers.GetValues("Idempotency-Key").Single());
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(token));
            Assert.Equal("notifications", body.RootElement.GetProperty("channel").GetString());
            Assert.Equal("message.sent", body.RootElement.GetProperty("name").GetString());
            Assert.Equal("Hello", body.RootElement.GetProperty("data").GetProperty("message").GetString());
            Assert.Equal("1.2", body.RootElement.GetProperty("socket_id").GetString());
            return new(HttpStatusCode.Accepted) { Content = new StringContent(Accepted) };
        });
        using var http = new HttpClient(handler);
        using var pubb = new PubbServer(Options, http);
        var result = await pubb.TriggerAsync("notifications", "message.sent", new { message = "Hello" }, new() { SocketId = "1.2", IdempotencyKey = "event-1" });
        Assert.True(result.Accepted);
        Assert.Equal("pub-1", result.PublicationId);
        Assert.False(result.Duplicate);
        Assert.Null(http.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task SupportsMultipleChannelsAndNullData()
    {
        using var http = new HttpClient(new Handler(async (request, _) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(2, body.RootElement.GetProperty("channel").GetArrayLength());
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("data").ValueKind);
            Assert.False(body.RootElement.TryGetProperty("socket_id", out var excludedSocket));
            return new(HttpStatusCode.Accepted) { Content = new StringContent(Accepted) };
        }));
        using var pubb = new PubbServer(Options, http);
        await pubb.TriggerAsync(["a", "b"], "message.sent", null);
    }

    [Theory]
    [InlineData(401)] [InlineData(429)] [InlineData(500)] [InlineData(302)] [InlineData(200)]
    public async Task RejectsUnacceptedResponsesWithoutEchoingResponseBody(int status)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("test-secret") })));
        using var pubb = new PubbServer(Options, http);
        var error = await Assert.ThrowsAsync<PubbApiException>(() => pubb.TriggerAsync("notifications", "message.sent", new { }));
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.DoesNotContain("test-secret", error.Message);
    }

    [Theory]
    [InlineData("null")] [InlineData("{}")] [InlineData("invalid")]
    public async Task DoesNotInventSuccessForInvalidJson(string body)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent(body) })));
        using var pubb = new PubbServer(Options, http);
        await Assert.ThrowsAsync<JsonException>(() => pubb.TriggerAsync("notifications", "message.sent", null));
    }

    [Fact]
    public void SignsKnownPrivateAndPresenceVectors()
    {
        using var pubb = new PubbServer(Options);
        Assert.Equal("public-key:f3312a32998c7c174fb5d44146dad485d980398fe616181bf1f758764ccf603a", pubb.AuthenticatePrivateChannel("123.456", "private-room").Auth);
        var presence = pubb.AuthenticatePresenceChannel("123.456", "presence-room", "user-1", new { name = "Ada" });
        Assert.Equal("{\"user_id\":\"user-1\",\"user_info\":{\"name\":\"Ada\"}}", presence.ChannelData);
        Assert.Equal("public-key:41d9f92ca8a5496a5f346458be7d75c0d79bce9928260f8d139330140140d939", presence.Auth);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(presence));
        Assert.Equal(presence.ChannelData, json.RootElement.GetProperty("channel_data").GetString());
    }

    [Fact]
    public async Task CancellationAndNetworkErrorsPropagate()
    {
        using var http = new HttpClient(new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new(HttpStatusCode.Accepted); }));
        using var pubb = new PubbServer(Options, http);
        using var source = new CancellationTokenSource();
        var request = pubb.TriggerAsync("notifications", "message.sent", null, cancellationToken: source.Token);
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        using var failedHttp = new HttpClient(new Handler((_, _) => throw new HttpRequestException("Network unavailable")));
        using var failedPubb = new PubbServer(Options, failedHttp);
        await Assert.ThrowsAsync<HttpRequestException>(() => failedPubb.TriggerAsync("notifications", "message.sent", null));
    }

    [Fact]
    public async Task DoesNotDisposeBorrowedHttpClientAndRejectsUseAfterDispose()
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent(Accepted) }));
        using var http = new HttpClient(handler);
        var pubb = new PubbServer(Options, http);
        pubb.Dispose();
        Assert.False(handler.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pubb.TriggerAsync("notifications", "message.sent", null));
    }

    [Fact]
    public void RejectsInvalidCredentialsAndAuthChannelTypes()
    {
        Assert.Throws<ArgumentException>(() => new PubbServer(new() { AppId = "app", Key = "key", Secret = "" }));
        using var pubb = new PubbServer(Options);
        Assert.Throws<ArgumentException>(() => pubb.AuthenticatePrivateChannel("123.456", "public-room"));
        Assert.Throws<ArgumentException>(() => pubb.AuthenticatePresenceChannel("123.456", "presence-room", ""));
    }
}
