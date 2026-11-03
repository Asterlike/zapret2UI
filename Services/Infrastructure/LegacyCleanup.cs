using System.IO;
using Zapret2UI.Services.Warp;

namespace Zapret2UI.Services.Infrastructure;

/// <summary>
/// Removes what earlier versions left in the profile and nothing reads any more: the programs from before
/// the move to Program Files (each only once its replacement is in place — until then it is the fallback),
/// the WireGuard folder from before WARP moved to MASQUE, and the whole pre-rename <c>ZapretUI</c> folder.
/// Best-effort throughout: a leftover that cannot be deleted does no harm.
/// </summary>
internal static class LegacyCleanup
{
    internal static void Run(UpdaterService updater)
    {
        updater.DeleteLegacyEngine();
        MasqueRuntime.DeleteLegacyClient();
        TorRuntime.DeleteLegacyBundle();
        TryDeleteDir(Path.Combine(AppPaths.Root, "wireguard"));

        // The rename's move ran only when the new folder did not exist; where both exist, the old one is
        // a copy nobody reads.
        if (Directory.Exists(AppPaths.Root)) TryDeleteDir(AppPaths.LegacyRoot);
    }

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* best effort */ }
    }
}
