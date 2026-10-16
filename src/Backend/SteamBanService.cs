using System.Net;
using System.Text.Json;

internal sealed record SteamBanSnapshot(
    bool CommunityBanned,
    bool VacBanned,
    int NumberOfVACBans,
    int NumberOfGameBans,
    int DaysSinceLastBan,
    string EconomyBan,
    DateTimeOffset CheckedUtc)
{
    public bool GameBanned => NumberOfGameBans > 0;
}

internal sealed class SteamBanService
{
    private static readonly HttpClient HttpClient = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = false,
    })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    public async Task<SteamBanSnapshot?> QueryAsync(string steamId, CancellationToken cancellationToken)
    {
        var apiKey = Environment.GetEnvironmentVariable("CS2_HUB_STEAM_API_KEY")?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(steamId)) return null;

        var url = "https://partner.steam-api.com/ISteamUser/GetPlayerBans/v1/"
            + $"?key={Uri.EscapeDataString(apiKey)}&steamids={Uri.EscapeDataString(steamId)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("CS2AccountHub/1.0");

        try
        {
            using var response = await HttpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("players", out var players) || players.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var player in players.EnumerateArray())
            {
                if (!player.TryGetProperty("SteamId", out var id) ||
                    !string.Equals(id.GetString(), steamId, StringComparison.Ordinal))
                {
                    continue;
                }

                return new SteamBanSnapshot(
                    ReadBoolean(player, "CommunityBanned"),
                    ReadBoolean(player, "VACBanned"),
                    ReadInt32(player, "NumberOfVACBans"),
                    ReadInt32(player, "NumberOfGameBans"),
                    ReadInt32(player, "DaysSinceLastBan"),
                    ReadString(player, "EconomyBan") ?? "unknown",
                    DateTimeOffset.UtcNow);
            }

            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool ReadBoolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True;

    private static int ReadInt32(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : 0;

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
