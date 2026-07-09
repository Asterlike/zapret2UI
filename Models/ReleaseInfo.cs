namespace Zapret2UI.Models;

/// <summary>Resolved information about a single GitHub release of zapret2.</summary>
public sealed record ReleaseInfo(
    string Tag,
    string ZipUrl,
    string? Sha256Url,
    long ZipSize);

public enum UpdatePhase
{
    Checking,
    Downloading,
    Verifying,
    Extracting,
    Done
}

/// <summary>Progress report pushed from <c>UpdaterService</c> to the UI.</summary>
public sealed record UpdateProgress(UpdatePhase Phase, double Fraction, string Message);
