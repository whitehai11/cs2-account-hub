namespace NfaLoader.Localization;

internal static class Loc
{
    public static string T(string key) => key;
    public static string Tf(string key, params object?[] args) => args.Length == 0 ? key : $"{key}: {string.Join(", ", args)}";
}
