namespace NfaLoader.Localization;

/// <summary>
/// Server-side compatibility shim for the reference services. API consumers receive stable
/// English operation messages; the desktop app owns user-facing localization.
/// </summary>
internal static class Loc
{
    public static string T(string key) => key;

    public static string Tf(string key, params object?[] args) =>
        args.Length == 0 ? key : $"{key}: {string.Join(", ", args.Select(x => x?.ToString() ?? ""))}";
}
