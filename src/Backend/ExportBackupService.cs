using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CS2AccountHub.Contracts;

internal sealed class ExportBackupService
{
    private const int KeySize = 32;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int Pbkdf2Iterations = 600_000;
    private static readonly byte[] Magic = Encoding.UTF8.GetBytes("CS2HUB-BACKUP-V1");
    private readonly string _dataDirectory;

    public ExportBackupService()
    {
        _dataDirectory = Environment.GetEnvironmentVariable("CS2_HUB_DATA_DIR")
            ?? Path.Combine(AppContext.BaseDirectory, "data");
    }

    public static AccountExportBundleDto BuildExport(AccountStore accounts, AccountEventStore events)
    {
        var accountSnapshot = accounts.List(null, null);
        var accountIds = accountSnapshot.Select(account => account.Id).ToHashSet();
        var safeAccounts = accountSnapshot.Select(account => new SafeAccountExportDto(
            account.Id,
            account.AccountName,
            account.SteamId,
            account.PersonaName,
            account.Prime,
            account.Section,
            account.Cs2Owned,
            account.VacBanned,
            account.SteamGameBanned,
            account.SteamVacBanned,
            account.HealthStatus,
            account.LoginRequiresReauthentication,
            account.LastLoginUtc,
            account.StatusUpdatedUtc)).ToArray();

        var safeEvents = events.ListAll()
            .Where(item => accountIds.Contains(item.AccountId))
            .Select(item =>
            {
                var account = accountSnapshot.FirstOrDefault(candidate => candidate.Id == item.AccountId);
                return new AccountTimelineEventDto(
                    item.Id,
                    item.AccountId,
                    account?.SteamId ?? "unbekannt",
                    account?.PersonaName,
                    account?.AvatarUrl,
                    account?.IsPermaGreen ?? false,
                    item.Type,
                    item.Title,
                    item.Detail,
                    item.Severity,
                    item.OccurredUtc);
            })
            .ToArray();

        return new AccountExportBundleDto(DateTimeOffset.UtcNow, safeAccounts, safeEvents);
    }

    public static string ToCsv(AccountExportBundleDto bundle)
    {
        var builder = new StringBuilder();
        builder.AppendLine("record_type,id,account_name,steam_id,persona_name,prime,section,cs2_owned,vac_banned,steam_game_banned,steam_vac_banned,health_status,login_requires_reauthentication,last_login_utc,status_updated_utc,event_type,event_title,event_detail,event_severity,event_occurred_utc");
        foreach (var account in bundle.Accounts)
        {
            builder.AppendLine(string.Join(',',
                Csv("account"), Csv(account.Id), Csv(account.AccountName), Csv(account.SteamId), Csv(account.PersonaName),
                Csv(account.Prime), Csv(account.Section), Csv(account.Cs2Owned), Csv(account.VacBanned),
                Csv(account.SteamGameBanned), Csv(account.SteamVacBanned), Csv(account.HealthStatus),
                Csv(account.LoginRequiresReauthentication), Csv(account.LastLoginUtc), Csv(account.StatusUpdatedUtc),
                "", "", "", "", ""));
        }
        foreach (var item in bundle.Events)
        {
            builder.AppendLine(string.Join(',',
                Csv("event"), Csv(item.Id), "", Csv(item.SteamId), Csv(item.PersonaName), "", "", "", "", "", "", "", "", "", "",
                Csv(item.Type), Csv(item.Title), Csv(item.Detail), Csv(item.Severity), Csv(item.OccurredUtc)));
        }
        return builder.ToString();
    }

    public byte[] CreateEncryptedBackup(string passphrase)
    {
        ValidatePassphrase(passphrase);
        var archive = CreateArchive();
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeySize);
        var cipher = new byte[archive.Length];
        var tag = new byte[TagSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, archive, cipher, tag, Magic);
        }
        finally { CryptographicOperations.ZeroMemory(key); }

        using var output = new MemoryStream();
        output.Write(Magic);
        output.Write(salt);
        output.Write(nonce);
        output.Write(tag);
        output.Write(cipher);
        return output.ToArray();
    }

    private byte[] CreateArchive()
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var fileName in new[] { "accounts.json", "account-events.json", "steam-authenticators.json", "steam-mail.json", "loadout.json", "personalize.json" })
            {
                AddFile(archive, fileName, Path.Combine(_dataDirectory, fileName));
            }

            var keyDirectory = Path.Combine(_dataDirectory, "keys");
            if (Directory.Exists(keyDirectory))
            {
                foreach (var path in Directory.EnumerateFiles(keyDirectory, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(_dataDirectory, path).Replace(Path.DirectorySeparatorChar, '/');
                    AddFile(archive, relative, path);
                }
            }
        }
        return output.ToArray();
    }

    private static void AddFile(ZipArchive archive, string entryName, string path)
    {
        if (!File.Exists(path)) return;
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var target = entry.Open();
        source.CopyTo(target);
    }

    private static void ValidatePassphrase(string passphrase)
    {
        if (string.IsNullOrWhiteSpace(passphrase) || passphrase.Length is < 12 or > 256)
        {
            throw new ArgumentException("Das Backup-Passwort muss zwischen 12 und 256 Zeichen lang sein.", nameof(passphrase));
        }
    }

    private static string Csv(object? value)
    {
        var text = value?.ToString() ?? string.Empty;
        return $"\"{text.Replace("\"", "\"\"")}\"";
    }
}
