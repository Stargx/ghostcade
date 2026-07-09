namespace Attractor.Core.Catalog;

public enum DriverStatus { Unknown, Good, Imperfect, Preliminary }

public enum VerifyResult { Good, BestAvailable }

/// <summary>Raw -listxml metadata for one machine (cached for ALL machines).</summary>
public sealed record MachineInfo(
    string Name,
    string? Description,
    string? Year,
    string? Manufacturer,
    bool IsBios,
    bool IsDevice,
    bool Runnable,
    string? CloneOf,
    DriverStatus Driver,
    int Rotate,
    bool IsMechanical = false); // fruit/quiz machines etc; captured for a future filter

/// <summary>A rotation-eligible game: listxml metadata joined with verify results.
/// <paramref name="Genres"/> are the catver.ini category tags (top-level genre first,
/// then any subgenres), attached after assembly via
/// <see cref="GameDatabase.ApplyGenres"/>; null = no catver data.</summary>
public sealed record GameEntry(
    string Name,
    string Title,
    string Year,
    string Manufacturer,
    DriverStatus Driver,
    VerifyResult Verify,
    int Rotate,
    string? CloneOf = null,
    IReadOnlyList<string>? Genres = null)
{
    public bool IsVertical => Rotate is 90 or 270;

    /// <summary>The decade this game's year falls in (e.g. 1980 for the 1980s), or
    /// null when the year is unknown or too vague ("????", "19??"). A partial year
    /// like "198?" still resolves to 1980.</summary>
    public int? DecadeStart()
    {
        if (Year.Length < 3) return null;
        for (int i = 0; i < 3; i++)
            if (!char.IsDigit(Year[i])) return null;
        return int.Parse(Year.AsSpan(0, 3)) * 10;
    }
}
