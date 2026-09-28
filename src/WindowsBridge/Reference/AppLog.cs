namespace NfaLoader.Services;

internal static class AppLog
{
    public static void Warn(string message) => Console.WriteLine($"WARN {message}");
    public static void Error(string message) => Console.Error.WriteLine($"ERROR {message}");
}
