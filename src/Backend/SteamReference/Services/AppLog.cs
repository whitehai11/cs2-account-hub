namespace NfaLoader.Services;

internal static class AppLog
{
    public static void Info(string message) => Console.WriteLine($"INFO {message}");
    public static void Warn(string message) => Console.WriteLine($"WARN {message}");
    public static void Error(string message) => Console.Error.WriteLine($"ERROR {message}");
    public static void Error(string message, Exception exception) => Console.Error.WriteLine($"ERROR {message}: {exception.Message}");
}
