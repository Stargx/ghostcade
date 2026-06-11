using Attractor.Core.Catalog;

namespace Attractor.Core.Tests;

public class RomVerifierTests
{
    [Fact]
    public void Parses_good_best_available_and_clone_bracket_lines()
    {
        var results = new Dictionary<string, VerifyResult>();
        RomVerifier.ParseLine("romset 1941 is good", results);
        RomVerifier.ParseLine("romset 1941j [1941] is best available", results);
        RomVerifier.ParseLine("romset broken is bad", results);
        RomVerifier.ParseLine("1942    : maincpu (16K) - NOT FOUND", results);
        RomVerifier.ParseLine("3 romsets found, 2 were OK.", results);

        Assert.Equal(2, results.Count);
        Assert.Equal(VerifyResult.Good, results["1941"]);
        Assert.Equal(VerifyResult.BestAvailable, results["1941j"]);
        Assert.False(results.ContainsKey("broken"));
    }
}
