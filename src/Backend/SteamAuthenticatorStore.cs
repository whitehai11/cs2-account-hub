using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CS2AccountHub.Contracts;

internal sealed class SteamAuthenticatorStore
{
    private const int MaxFileBytes = 256 * 1024;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, SteamAuthenticatorRecord> _authenticators = new();
    private readonly SecretProtector _secrets;
    private readonly SteamAuthenticatorCodeService _codes;
    private readonly ILogger<SteamAuthenticatorStore> _logger;
    private readonly string _path;

    public SteamAuthenticatorStore(
        SecretProtector secrets,
        SteamAuthenticatorCodeService codes,
        ILogger<SteamAuthenticatorStore> logger)
    {
        _secrets = secrets;
        _codes = codes;
        _logger = logger;

        var dataDirectory = Environment.GetEnvironmentVariable("CS2_HUB_DATA_DIR")
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, "steam-authenticators.json");
        Load();
    }

    public IReadOnlyList<SteamAuthenticatorSummaryDto> List(AccountStore accounts)
    {
        var accountLookup = accounts.List(null, null)
            .Where(account => !string.IsNullOrWhiteSpace(account.AccountName))
            .GroupBy(account => Normalize(account.AccountName!))
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        lock (_gate)
        {
            return _authenticators.Values
                .OrderBy(authenticator => authenticator.AccountName, StringComparer.OrdinalIgnoreCase)
                .Select(authenticator =>
                {
                    accountLookup.TryGetValue(Normalize(authenticator.AccountName), out var account);
                    return new SteamAuthenticatorSummaryDto(
                        authenticator.Id,
                        authenticator.AccountName,
                        account?.Id,
                        account?.PersonaName,
                        authenticator.ImportedUtc);
                })
                .ToArray();
        }
    }

    public SteamAuthenticatorImportResult Import(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxFileBytes)
        {
            throw new FormatException("Die MAFile ist zu groß.");
        }

        var maFile = JsonSerializer.Deserialize<SteamMaFile>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new FormatException("Die MAFile ist leer oder ungültig.");
        var accountName = maFile.AccountName?.Trim();
        var sharedSecret = maFile.SharedSecret?.Trim();

        if (string.IsNullOrWhiteSpace(accountName) || accountName.Length > 128)
        {
            throw new FormatException("Die MAFile enthält keinen gültigen Steam-Anmeldenamen.");
        }

        if (string.IsNullOrWhiteSpace(sharedSecret) || !IsValidSharedSecret(sharedSecret))
        {
            throw new FormatException("Die MAFile enthält keinen gültigen Shared Secret.");
        }

        lock (_gate)
        {
            var record = _authenticators.Values.FirstOrDefault(item =>
                string.Equals(Normalize(item.AccountName), Normalize(accountName), StringComparison.OrdinalIgnoreCase));
            var updated = record is not null;
            record ??= new SteamAuthenticatorRecord { Id = Guid.NewGuid() };
            record.AccountName = accountName;
            record.ProtectedSharedSecret = _secrets.Protect(sharedSecret);
            record.ImportedUtc = DateTimeOffset.UtcNow;
            _authenticators[record.Id] = record;
            SaveUnsafe();
            return new SteamAuthenticatorImportResult(record.Id, accountName, updated);
        }
    }

    public async Task<SteamAuthenticatorCodeDto?> GenerateCodeAsync(Guid id, CancellationToken cancellationToken)
    {
        SteamAuthenticatorRecord? record;
        lock (_gate) record = _authenticators.GetValueOrDefault(id);
        if (record is null) return null;

        string sharedSecret;
        try
        {
            sharedSecret = _secrets.Unprotect(record.ProtectedSharedSecret);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            _logger.LogWarning("Steam authenticator {AuthenticatorId} could not be decrypted ({ExceptionType}).", id, exception.GetType().Name);
            return null;
        }

        var generated = await _codes.GenerateAsync(sharedSecret, cancellationToken);
        return new SteamAuthenticatorCodeDto(id, record.AccountName, generated.Code, generated.SecondsRemaining, DateTimeOffset.UtcNow);
    }

    public async Task<string?> TryGenerateCodeForAccountAsync(string accountName, CancellationToken cancellationToken)
    {
        SteamAuthenticatorRecord? record;
        lock (_gate)
        {
            record = _authenticators.Values.FirstOrDefault(item =>
                string.Equals(Normalize(item.AccountName), Normalize(accountName), StringComparison.OrdinalIgnoreCase));
        }

        if (record is null) return null;

        try
        {
            var sharedSecret = _secrets.Unprotect(record.ProtectedSharedSecret);
            return (await _codes.GenerateAsync(sharedSecret, cancellationToken)).Code;
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            _logger.LogWarning("Steam authenticator for account match could not be decrypted ({ExceptionType}).", exception.GetType().Name);
            return null;
        }
    }

    public bool Delete(Guid id)
    {
        lock (_gate)
        {
            if (!_authenticators.Remove(id)) return false;
            SaveUnsafe();
            return true;
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            foreach (var authenticator in JsonSerializer.Deserialize<List<SteamAuthenticatorRecord>>(File.ReadAllText(_path)) ?? [])
            {
                if (authenticator.Id != Guid.Empty && !string.IsNullOrWhiteSpace(authenticator.AccountName) && !string.IsNullOrWhiteSpace(authenticator.ProtectedSharedSecret))
                {
                    _authenticators[authenticator.Id] = authenticator;
                }
            }
        }
        catch (Exception exception)
        {
            _logger.LogError("Could not load Steam authenticator state ({ExceptionType}).", exception.GetType().Name);
        }
    }

    private void SaveUnsafe()
    {
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_authenticators.Values));
            File.Move(temporaryPath, _path, true);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
        }
    }

    private static bool IsValidSharedSecret(string secret)
    {
        try
        {
            var bytes = Convert.FromBase64String(secret);
            return bytes.Length is >= 10 and <= 128;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Normalize(string value) => value.Trim().ToUpperInvariant();
}

internal sealed class SteamAuthenticatorCodeService
{
    private static readonly byte[] CodeTranslations = Encoding.UTF8.GetBytes("23456789BCDFGHJKMNPQRTVWXY");
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly ILogger<SteamAuthenticatorCodeService> _logger;
    private readonly SemaphoreSlim _timeSyncGate = new(1, 1);
    private long _timeOffsetSeconds;
    private DateTimeOffset _lastTimeSyncUtc = DateTimeOffset.MinValue;

    public SteamAuthenticatorCodeService(ILogger<SteamAuthenticatorCodeService> logger)
    {
        _logger = logger;
    }

    public async Task<SteamAuthenticatorGeneratedCode> GenerateAsync(string sharedSecret, CancellationToken cancellationToken)
    {
        var steamTime = await GetSteamTimeAsync(cancellationToken);
        var code = GenerateCodeForTime(sharedSecret, steamTime);
        var secondsRemaining = 30 - (int)(steamTime % 30);
        return new SteamAuthenticatorGeneratedCode(code, secondsRemaining);
    }

    private async Task<long> GetSteamTimeAsync(CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow - _lastTimeSyncUtc > TimeSpan.FromMinutes(5))
        {
            await _timeSyncGate.WaitAsync(cancellationToken);
            try
            {
                if (DateTimeOffset.UtcNow - _lastTimeSyncUtc > TimeSpan.FromMinutes(5))
                {
                    try
                    {
                        using var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("steamid", "0") });
                        using var response = await HttpClient.PostAsync("https://api.steampowered.com/ITwoFactorService/QueryTime/v0001/", content, cancellationToken);
                        response.EnsureSuccessStatusCode();
                        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
                        var serverTime = document.RootElement.GetProperty("response").GetProperty("server_time").GetInt64();
                        _timeOffsetSeconds = serverTime - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        _lastTimeSyncUtc = DateTimeOffset.UtcNow;
                    }
                    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
                    {
                        _logger.LogWarning("Steam time synchronization failed ({ExceptionType}); using local time.", exception.GetType().Name);
                        _lastTimeSyncUtc = DateTimeOffset.UtcNow;
                    }
                }
            }
            finally
            {
                _timeSyncGate.Release();
            }
        }

        return DateTimeOffset.UtcNow.ToUnixTimeSeconds() + Interlocked.Read(ref _timeOffsetSeconds);
    }

    // Steam Desktop Authenticator / SteamAuth algorithm, adapted from the MIT-licensed
    // SteamAuth library: https://github.com/geel9/SteamAuth
    private static string GenerateCodeForTime(string sharedSecret, long time)
    {
        var secret = Convert.FromBase64String(sharedSecret);
        var timeArray = new byte[8];
        var timeChunk = time / 30L;
        for (var index = 7; index >= 0; index--)
        {
            timeArray[index] = (byte)timeChunk;
            timeChunk >>= 8;
        }

        using var hmac = new HMACSHA1(secret);
        var hash = hmac.ComputeHash(timeArray);
        var offset = hash[^1] & 0x0F;
        var codePoint = (hash[offset] & 0x7F) << 24
            | (hash[offset + 1] & 0xFF) << 16
            | (hash[offset + 2] & 0xFF) << 8
            | (hash[offset + 3] & 0xFF);
        var code = new char[5];
        for (var index = 0; index < code.Length; index++)
        {
            code[index] = (char)CodeTranslations[codePoint % CodeTranslations.Length];
            codePoint /= CodeTranslations.Length;
        }

        return new string(code);
    }
}

internal sealed record SteamAuthenticatorGeneratedCode(string Code, int SecondsRemaining);
internal sealed record SteamAuthenticatorImportResult(Guid Id, string AccountName, bool Updated);

internal sealed class SteamAuthenticatorRecord
{
    public Guid Id { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public string ProtectedSharedSecret { get; set; } = string.Empty;
    public DateTimeOffset ImportedUtc { get; set; }
}

internal sealed class SteamMaFile
{
    [JsonPropertyName("account_name")]
    public string? AccountName { get; set; }

    [JsonPropertyName("shared_secret")]
    public string? SharedSecret { get; set; }
}
