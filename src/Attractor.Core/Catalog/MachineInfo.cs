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

/// <summary>A rotation-eligible game: listxml metadata joined with verify results.</summary>
public sealed record GameEntry(
    string Name,
    string Title,
    string Year,
    string Manufacturer,
    DriverStatus Driver,
    VerifyResult Verify,
    int Rotate)
{
    public bool IsVertical => Rotate is 90 or 270;
}
