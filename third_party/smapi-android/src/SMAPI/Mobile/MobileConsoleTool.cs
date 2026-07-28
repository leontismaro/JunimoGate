using System.Collections.Concurrent;

namespace StardewModdingAPI.Mobile;

public static class MobileConsoleTool
{
    private static readonly BlockingCollection<string> Lines = new();

    public static void WriteLine(string line) => Lines.Add(line);

    public static string ReadLine() => Lines.Take();
}
