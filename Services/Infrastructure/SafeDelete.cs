using System.IO;

namespace Zapret2UI.Services.Infrastructure;

/// <summary>Deletes for leftovers and temporary files: a failure — locked, already gone, no rights — is not
/// worth failing the operation that is cleaning up after itself.</summary>
internal static class SafeDelete
{
    internal static void File(string path)
    {
        try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); } catch { /* best effort */ }
    }

    internal static void Directory(string path)
    {
        try { if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, recursive: true); } catch { /* best effort */ }
    }
}
