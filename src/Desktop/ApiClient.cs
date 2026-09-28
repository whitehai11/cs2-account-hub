using System.Net.Http.Json;
using CS2AccountHub.Contracts;

namespace CS2AccountHub.Desktop;

internal sealed class ApiClient
{
    private readonly HttpClient _http;

    public ApiClient()
    {
        var baseUrl = Environment.GetEnvironmentVariable("CS2_HUB_API_URL") ?? "http://localhost:8080";
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
        var apiKey = Environment.GetEnvironmentVariable("CS2_HUB_API_KEY");
        if (!string.IsNullOrWhiteSpace(apiKey)) _http.DefaultRequestHeaders.Add("X-CS2-Hub-Key", apiKey);
    }

    public async Task<IReadOnlyList<AccountSummaryDto>> GetAccountsAsync(AccountSection section, AccountPrimeBucket prime, CancellationToken cancellationToken = default)
    {
        var url = $"api/accounts?section={section}&prime={prime}";
        return await _http.GetFromJsonAsync<AccountSummaryDto[]>(url, cancellationToken) ?? [];
    }

    public Task<AccountDetailDto?> GetAccountAsync(Guid id, CancellationToken cancellationToken = default) =>
        _http.GetFromJsonAsync<AccountDetailDto>($"api/accounts/{id}", cancellationToken);

    public async Task<StartAuthResponse?> StartAuthAsync(string accountName, string password, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync("api/auth/sessions", new StartAuthRequest(accountName, password), cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<StartAuthResponse>(cancellationToken: cancellationToken);
    }

    public Task<StartAuthResponse?> GetAuthSessionAsync(Guid id, CancellationToken cancellationToken = default) =>
        _http.GetFromJsonAsync<StartAuthResponse>($"api/auth/sessions/{id}", cancellationToken);

    public async Task<StartAuthResponse?> SubmitGuardCodeAsync(Guid id, string code, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync($"api/auth/sessions/{id}/code", new SubmitGuardCodeRequest(code), cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<StartAuthResponse>(cancellationToken: cancellationToken);
    }

    public async Task<OperationDto?> QueueCommandAsync(Guid id, AccountCommand command, object? payload = null, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync($"api/accounts/{id}/commands", new CommandRequest(command, payload), cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<OperationDto>(cancellationToken: cancellationToken);
    }

    public Task<OperationDto?> GetOperationAsync(Guid id, CancellationToken cancellationToken = default) =>
        _http.GetFromJsonAsync<OperationDto>($"api/operations/{id}", cancellationToken);

    public Task<SteamSignInHandoff?> ClaimHandoffAsync(Guid operationId, CancellationToken cancellationToken = default) =>
        _http.GetFromJsonAsync<SteamSignInHandoff>($"api/operations/{operationId}/handoff", cancellationToken);

    public Task<LoadoutPresetDto?> GetLoadoutAsync(CancellationToken cancellationToken = default) =>
        _http.GetFromJsonAsync<LoadoutPresetDto>("api/settings/loadout", cancellationToken);

    public Task<PersonalizePresetDto?> GetPersonalizeAsync(CancellationToken cancellationToken = default) =>
        _http.GetFromJsonAsync<PersonalizePresetDto>("api/settings/personalize", cancellationToken);

    public async Task SaveLoadoutAsync(LoadoutPresetDto preset, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PutAsJsonAsync("api/settings/loadout", preset, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task SavePersonalizeAsync(PersonalizePresetDto preset, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PutAsJsonAsync("api/settings/personalize", preset, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task SaveNoteAsync(Guid id, string? note, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PatchAsJsonAsync($"api/accounts/{id}/note", new UpdateNoteRequest(note), cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
