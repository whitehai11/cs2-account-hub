using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using CS2AccountHub.Contracts;
using NfaLoader.Services;

internal sealed class SteamLocalSignInService
{
    public void SignIn(SteamSignInPayload payload)
    {
        var installPath = ResolveSteamInstallPath()
            ?? throw new InvalidOperationException("Steam installation was not found.");
        var configPath = Path.Combine(installPath, "config");
        var localVdfPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Steam", "local.vdf");
        var steamExe = Path.Combine(installPath, "steam.exe");

        StopSteam(steamExe);
        Directory.CreateDirectory(configPath);

        var encryptedToken = Protect(payload.RefreshToken, payload.AccountName);
        WriteConfig(Path.Combine(configPath, "config.vdf"), payload.AccountName, payload.SteamId);
        WriteLoginUsers(Path.Combine(configPath, "loginusers.vdf"), payload.AccountName, payload.SteamId);
        WriteConnectCache(localVdfPath, payload.AccountName, encryptedToken);

        var startInfo = new ProcessStartInfo
        {
            FileName = steamExe,
            WorkingDirectory = installPath,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-login");
        startInfo.ArgumentList.Add(payload.AccountName);
        Process.Start(startInfo)?.Dispose();
    }

    private static void WriteConfig(string path, string accountName, string steamId)
    {
        var root = new Dictionary<string, object>(StringComparer.Ordinal);
        var steam = EnsurePath(root, "InstallConfigStore", "Software", "Valve", "Steam");
        steam["AutoUpdateWindowEnabled"] = "0";
        steam["MTBF"] = Random.Shared.Next(100000000, 999999999).ToString();
        EnsureObject(steam, "Accounts")[accountName] = new Dictionary<string, object>
        {
            ["SteamID"] = steamId,
        };
        VdfDocument.Save(path, root);
    }

    private static void WriteLoginUsers(string path, string accountName, string steamId)
    {
        var root = VdfDocument.LoadOrEmpty(path);
        var users = EnsureObject(root, "users");
        foreach (var user in users.Values.OfType<Dictionary<string, object>>()) user["MostRecent"] = "0";
        users[steamId] = new Dictionary<string, object>
        {
            ["AccountName"] = accountName,
            ["PersonaName"] = accountName,
            ["RememberPassword"] = "1",
            ["WantsOfflineMode"] = "0",
            ["SkipOfflineModeWarning"] = "0",
            ["AllowAutoLogin"] = "1",
            ["MostRecent"] = "1",
            ["Timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
        };
        VdfDocument.Save(path, root);
    }

    private static void WriteConnectCache(string path, string accountName, string encryptedToken)
    {
        var root = VdfDocument.LoadOrEmpty(path);
        var connectCache = EnsurePath(root, "MachineUserConfigStore", "Software", "Valve", "Steam", "ConnectCache");
        connectCache[Crc32.ComputeSteamAccountKey(accountName)] = encryptedToken;
        VdfDocument.Save(path, root);
    }

    private static Dictionary<string, object> EnsurePath(Dictionary<string, object> root, params string[] keys)
    {
        var current = root;
        foreach (var key in keys) current = EnsureObject(current, key);
        return current;
    }

    private static Dictionary<string, object> EnsureObject(Dictionary<string, object> parent, string key)
    {
        if (parent.TryGetValue(key, out var value) && value is Dictionary<string, object> existing) return existing;
        var created = new Dictionary<string, object>(StringComparer.Ordinal);
        parent[key] = created;
        return created;
    }

    private static string? ResolveSteamInstallPath()
    {
        var candidates = new List<string?>
        {
            Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string,
            Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"),
        };
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(Path.Combine(path!, "steam.exe")));
    }

    private static void StopSteam(string steamExe)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo { FileName = steamExe, UseShellExecute = false, ArgumentList = { "-shutdown" } });
            process?.WaitForExit(5000);
        }
        catch
        {
            // Steam may already be closed; the launch below remains the final authority.
        }
    }

    private static string Protect(string token, string accountName)
    {
        var input = Encoding.UTF8.GetBytes(token);
        var entropy = Encoding.UTF8.GetBytes(accountName);
        var protectedBytes = ProtectedData.Protect(input, entropy, DataProtectionScope.CurrentUser);
        return Convert.ToHexString(protectedBytes).ToLowerInvariant();
    }
}
