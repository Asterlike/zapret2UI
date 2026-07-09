namespace Zapret2UI.Models;

/// <summary>
/// Final, human-readable verdict for one service (Discord / YouTube / custom targets) after an
/// auto-select or generation run. Computed from the CHOSEN strategy's real HTTP/2 reachability
/// results (plus the no-bypass baseline) and shown as a big «РАБОТАЕТ / ЧАСТИЧНО / НЕ РАБОТАЕТ»
/// chip in the review popup — the visible validation the plain probe checkmarks never gave.
/// </summary>
public sealed class ServiceVerdict
{
    public required string Service { get; init; }      // "Discord"
    public required string StatusText { get; init; }   // "РАБОТАЕТ" / "ЧАСТИЧНО" / "НЕ РАБОТАЕТ"
    public required string Detail { get; init; }        // "5/5 целей грузятся · разблокировано обходом"

    /// <summary>Reused only for colour via DiagStatusToBrushConverter: Ok = green, Timeout = amber
    /// (partial), Fail = red. Not shown as text.</summary>
    public required DiagStatus Status { get; init; }
}
