using Pubb.Server;

string Required(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"Set {name} locally before running this example");
using var pubb = new PubbServer(new()
{
    AppId = Required("PUBB_APP_ID"), Key = Required("PUBB_APP_KEY"), Secret = Required("PUBB_APP_SECRET"),
    ApiHost = Environment.GetEnvironmentVariable("PUBB_API_HOST") ?? "https://api.pubb.io",
});
var result = await pubb.TriggerAsync("notifications", "message.sent", new { message = "Hello from .NET!" });
Console.WriteLine($"Accepted publication {result.PublicationId}. Watch a connected subscriber for delivery.");
