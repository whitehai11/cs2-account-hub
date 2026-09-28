using System.Text.Json;
using CS2AccountHub.Contracts;

Console.WriteLine("CS2 Account Hub Windows Bridge");
Console.WriteLine("The production bridge is launched by the desktop client for a one-time Steam handoff.");

if (args.Length == 1 && File.Exists(args[0]))
{
    var handoffPath = args[0];
    var json = await File.ReadAllTextAsync(handoffPath);
    var handoff = JsonSerializer.Deserialize<SteamSignInHandoff>(json);
    try { File.Delete(handoffPath); } catch { }
    if (handoff is null || handoff.ExpiresUtc <= DateTimeOffset.UtcNow)
    {
        Console.Error.WriteLine("The sign-in handoff is missing or expired.");
        return 2;
    }

    var payload = JsonSerializer.Deserialize<SteamSignInPayload>(handoff.OneTimePayload);
    if (payload is null || string.IsNullOrWhiteSpace(payload.RefreshToken))
    {
        Console.Error.WriteLine("The sign-in handoff payload is invalid.");
        return 3;
    }

    new SteamLocalSignInService().SignIn(payload);
    Console.WriteLine("Steam launch requested.");
    return 0;
}

return 0;
