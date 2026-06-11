namespace Attractor.Core.Configuration;

/// <summary>Write-temp-then-rename so readers never observe a half-written file.</summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);
        File.Move(tmp, path, overwrite: true);
    }

    public static void WriteAllLines(string path, IEnumerable<string> lines)
    {
        var tmp = path + ".tmp";
        File.WriteAllLines(tmp, lines);
        File.Move(tmp, path, overwrite: true);
    }
}
