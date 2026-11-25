namespace PhonePlayLink.Core;

public static class Logger
{
    private static readonly object Sync = new();

    public static void Info(string tag, string message) => Write("INFO ", tag, message, ConsoleColor.Gray);
    public static void Ok(string tag, string message) => Write("OK   ", tag, message, ConsoleColor.Green);
    public static void Warn(string tag, string message) => Write("WARN ", tag, message, ConsoleColor.Yellow);
    public static void Error(string tag, string message) => Write("ERROR", tag, message, ConsoleColor.Red);

    private static void Write(string level, string tag, string message, ConsoleColor color)
    {
        string time = DateTime.Now.ToString("HH:mm:ss.fff");
        lock (Sync)
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"[{time}] ");
            Console.ForegroundColor = color;
            Console.Write($"[{level}] ");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write($"[{tag}] ");
            Console.ForegroundColor = prev;
            Console.WriteLine(message);
        }
    }
}
