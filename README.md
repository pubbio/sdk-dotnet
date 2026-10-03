# Pubb .NET server SDK

Standalone .NET 8+ library for publishing Pubb events and signing private/presence
subscriptions. No runtime dependencies beyond .NET. Source: [pubbio/sdk-dotnet](https://github.com/pubbio/sdk-dotnet).

This repository does not publish to NuGet automatically. Until `Pubb.Server` version
`0.2.0` is published and verified, use a project reference or the local package:

```sh
dotnet pack src/Pubb.Server -c Release -o artifacts
dotnet add /path/to/consumer package Pubb.Server --version 0.2.0 --source /absolute/path/to/sdk-dotnet/artifacts
```

## Publish

```csharp
using Pubb.Server;

using var pubb = new PubbServer(new()
{
    AppId = Environment.GetEnvironmentVariable("PUBB_APP_ID")!,
    Key = Environment.GetEnvironmentVariable("PUBB_APP_KEY")!,
    Secret = Environment.GetEnvironmentVariable("PUBB_APP_SECRET")!,
    ApiHost = "https://api.pubb.io",
});
var result = await pubb.TriggerAsync("notifications", "message.sent", new { message = "Hello!" });
Console.WriteLine(result.PublicationId);
```

The overload accepting `string[]` publishes to multiple channels. `TriggerOptions`
supports `SocketId` to exclude a sender and `IdempotencyKey` for retries of the same
publication. Pass a `CancellationToken` to cancel a request. No automatic publish
retries occur. HTTP 202 confirms acceptance, not subscriber delivery; the response
contains `Success`, `Accepted`, `Channels`, `PublicationId` and `Duplicate`.

HTTP failures throw `PubbApiException` with `StatusCode`. Invalid response bodies,
network failures and cancellation propagate; the SDK never synthesizes success.
An optional constructor `HttpClient` parameter supports dependency injection and
testing. Dispose the SDK when finished; it disposes only clients it created.
For an injected client, configure your own timeout and disable automatic redirects.

## Authorize private and presence channels

In your own authenticated endpoint, validate the current user's access to the
requested channel, then return the SDK's response as JSON:

```csharp
// Only after authenticating the user and authorizing channelName:
var auth = pubb.AuthenticatePrivateChannel(socketId, "private-room");
var presence = pubb.AuthenticatePresenceChannel(
    socketId, "presence-room", verifiedUserId, new { name = verifiedUserName });
```

The client request uses `socket_id` and `channel_name`; map these with
`[JsonPropertyName]` in your request DTO. Responses serialize to `auth` and
`channel_data` as required by Pubb. Derive the presence user ID from the verified
session. Signing functions do not implement your application's access policy.

Never place `PUBB_APP_SECRET` in browser, mobile, desktop or Blazor WebAssembly
code. This is a trusted server SDK; subscribe in your frontend using its client SDK.
Authenticate and authorize callers of application routes that publish events.
Public channels must not carry sensitive data. Configure environment variables
locally; `.env.example` is a template and is not loaded automatically.

## Checks and example

```sh
dotnet test tests/Pubb.Server.Tests -c Release
dotnet build examples/Console -c Release
dotnet pack src/Pubb.Server -c Release -o artifacts
# With real credentials set locally, and a subscriber listening:
dotnet run --project examples/Console
```

Automated tests cover exact HTTP requests, response failures, cancellation,
client ownership, and independently calculated HMAC vectors. They use a mocked
HTTP handler. A live publish/delivery check needs a running Pubb application and
is not claimed by the unit suite. GitHub CI runs tests, builds the example and
packs the NuGet artifact. Package registry publication requires a separate release.

The older monorepo's channel-listing helpers are omitted because the current API
does not expose those endpoints. License: [MIT](LICENSE).
