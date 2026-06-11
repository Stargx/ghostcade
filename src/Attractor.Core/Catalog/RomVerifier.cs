using System.Text.RegularExpressions;
using Attractor.Core.Mame;

namespace Attractor.Core.Catalog;

/// <summary>
/// Wraps `mame -verifyroms`. Output lines look like:
///   romset 1941 is good
///   romset 1941j [1941] is best available
/// Note MAME exits nonzero when ANY set is bad — the exit code is not failure.
/// </summary>
public static partial class RomVerifier
{
    [GeneratedRegex(@"^romset (\S+)( \[[^\]]*\])? is (good|best available)$", RegexOptions.Compiled)]
    private static partial Regex GoodSetLine();

    public static Dictionary<string, VerifyResult> ParseLine(
        string line, Dictionary<string, VerifyResult> results)
    {
        var m = GoodSetLine().Match(line);
        if (m.Success)
            results[m.Groups[1].Value] =
                m.Groups[3].Value == "good" ? VerifyResult.Good : VerifyResult.BestAvailable;
        return results;
    }

    public static async Task<Dictionary<string, VerifyResult>> VerifyAsync(
        string mameExePath, IProgress<int>? linesSeen = null, CancellationToken ct = default)
    {
        var results = new Dictionary<string, VerifyResult>(StringComparer.Ordinal);
        int lines = 0;
        await MameVerbRunner.RunLinesAsync(mameExePath, ["-verifyroms"], line =>
        {
            ParseLine(line, results);
            if (++lines % 50 == 0)
                linesSeen?.Report(lines);
        }, ct).ConfigureAwait(false);
        return results;
    }
}
