using System.Text;

namespace Attractor.Core.Configuration;

/// <summary>Write-temp-then-rename so readers never observe a half-written file.
/// The temp write is flushed through the OS cache before the rename — otherwise a
/// power cut can commit the rename but not the data, leaving a truncated file
/// (fatal for config.json, which refuses to start on invalid JSON).</summary>
public static class AtomicFile
{
    // No BOM, matching what File.WriteAllText/WriteAllLines produced before.
    private static readonly UTF8Encoding Utf8 = new();

    public static void WriteAllText(string path, string contents) =>
        WriteBytes(path, Utf8.GetBytes(contents));

    public static void WriteAllLines(string path, IEnumerable<string> lines) =>
        WriteBytes(path, Utf8.GetBytes(string.Concat(lines.Select(l => l + Environment.NewLine))));

    private static void WriteBytes(string path, byte[] bytes)
    {
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }
}
