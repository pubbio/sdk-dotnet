using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pubb.Server;

public sealed class PubbServerOptions
{
    public required string AppId { get; init; }
    public required string Key { get; init; }
    public required string Secret { get; init; }
    public string ApiHost { get; init; } = "https://api.pubb.io";
}

public sealed class TriggerOptions
{
    public string? SocketId { get; init; }
    public string? IdempotencyKey { get; init; }
}

public sealed record TriggerResponse
{
    [JsonPropertyName("success")] public required bool Success { get; init; }
    [JsonPropertyName("accepted")] public required bool Accepted { get; init; }
    [JsonPropertyName("channels")] public required string[] Channels { get; init; }
    [JsonPropertyName("publicationId")] public required string PublicationId { get; init; }
    [JsonPropertyName("duplicate")] public required bool Duplicate { get; init; }
}

public sealed record PrivateChannelAuthResponse([property: JsonPropertyName("auth")] string Auth);
public sealed record PresenceChannelAuthResponse(
    [property: JsonPropertyName("auth")] string Auth,
    [property: JsonPropertyName("channel_data")] string ChannelData);

public sealed class PubbApiException(HttpStatusCode statusCode) : Exception($"Pubb publish failed (HTTP {(int)statusCode})")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

/// <summary>Use only in trusted server code. Authorize users before signing subscriptions.</summary>
public sealed class PubbServer : IDisposable
{
    private readonly string key;
    private readonly string secret;
    private readonly Uri endpoint;
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private bool disposed;

    /// <summary>An injected HttpClient remains owned by its caller.</summary>
    public PubbServer(PubbServerOptions options, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.AppId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Key);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Secret);
        if (!Uri.TryCreate(options.ApiHost, UriKind.Absolute, out var host) ||
            (host.Scheme != "http" && host.Scheme != "https") || host.Query.Length > 0 || host.Fragment.Length > 0 || host.UserInfo.Length > 0)
            throw new ArgumentException("ApiHost must be an HTTP(S) base URL without credentials, query or fragment", nameof(options));
        key = options.Key;
        secret = options.Secret;
        endpoint = new Uri($"{options.ApiHost.TrimEnd('/')}/api/apps/{Uri.EscapeDataString(options.AppId)}/events");
        ownsHttp = httpClient is null;
        http = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public Task<TriggerResponse> TriggerAsync(string channel, string eventName, object? data, TriggerOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        return PublishAsync(channel, eventName, data, options, cancellationToken);
    }

    public Task<TriggerResponse> TriggerAsync(string[] channels, string eventName, object? data, TriggerOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channels);
        if (channels.Length is 0 or > 100 || channels.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Provide 1–100 nonempty channels", nameof(channels));
        return PublishAsync(channels, eventName, data, options, cancellationToken);
    }

    private async Task<TriggerResponse> PublishAsync(object channel, string eventName, object? data, TriggerOptions? options, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        if (options?.IdempotencyKey is { } idempotencyKey)
        {
            if (idempotencyKey.Length is 0 or > 128 || idempotencyKey.Any(c => c < '!' || c > '~'))
                throw new ArgumentException("IdempotencyKey must contain 1–128 printable non-space ASCII characters", nameof(options));
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }
        var payload = new Dictionary<string, object?> { ["channel"] = channel, ["name"] = eventName, ["data"] = data };
        if (options?.SocketId is { } socketId) payload["socket_id"] = socketId;
        request.Content = JsonContent.Create(payload);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Accepted) throw new PubbApiException(response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<TriggerResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result is null || !result.Success || !result.Accepted || result.Channels is null || string.IsNullOrEmpty(result.PublicationId))
            throw new JsonException("Invalid Pubb acceptance response");
        return result;
    }

    public PrivateChannelAuthResponse AuthenticatePrivateChannel(string socketId, string channelName)
    {
        ValidateAuth(socketId, channelName, "private-");
        return new(Sign($"{socketId}:{channelName}"));
    }

    public PresenceChannelAuthResponse AuthenticatePresenceChannel(string socketId, string channelName, string userId, object? userInfo = null)
    {
        ValidateAuth(socketId, channelName, "presence-");
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var channelData = JsonSerializer.Serialize(new { user_id = userId, user_info = userInfo ?? new { } });
        return new(Sign($"{socketId}:{channelName}:{channelData}"), channelData);
    }

    private void ValidateAuth(string socketId, string channelName, string prefix)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(socketId);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelName);
        if (!channelName.StartsWith(prefix, StringComparison.Ordinal)) throw new ArgumentException($"Channel must start with {prefix}", nameof(channelName));
    }

    private string Sign(string message) => $"{key}:{Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(message))).ToLowerInvariant()}";

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (ownsHttp) http.Dispose();
    }
}
