namespace FATXTools.DiskTypes;

internal static class AppLogger
{
    public static void WriteLine(string message)
    {
        Console.Error.WriteLine(message);
    }
}
