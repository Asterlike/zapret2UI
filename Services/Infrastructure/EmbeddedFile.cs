using System.IO;
using System.Reflection;

namespace Zapret2UI.Services.Infrastructure;

/// <summary>Files the exe carries and unpacks next to the engine: usque, its licence, the Flowseal fakes.</summary>
internal static class EmbeddedFile
{
    /// <summary>
    /// Write an embedded resource to <paramref name="path"/> unless the file there already holds exactly its
    /// bytes. Compared by CONTENT, streamed: a size check alone misses a same-size replacement, and reading
    /// a 13 MB client into one array on every WARP start would be a large-object allocation for nothing.
    /// A half-written executable would be worse than none, so it lands under a temporary name first.
    /// </summary>
    internal static void Extract(string resource, string path)
    {
        var asm = Assembly.GetExecutingAssembly();
        using (Stream src = Open(asm, resource))
            if (SameContent(src, path)) return;

        string tmp = path + ".new";
        using (Stream src = Open(asm, resource))
        using (var dst = File.Create(tmp))
            src.CopyTo(dst);
        File.Move(tmp, path, overwrite: true);
    }

    private static Stream Open(Assembly asm, string resource) =>
        asm.GetManifestResourceStream(resource) ?? throw new FileNotFoundException($"embedded resource missing: {resource}");

    private static bool SameContent(Stream src, string path)
    {
        if (!File.Exists(path)) return false;
        using var file = File.OpenRead(path);
        if (file.Length != src.Length) return false;

        var a = new byte[64 * 1024];
        var b = new byte[64 * 1024];
        int n;
        while ((n = src.ReadAtLeast(a, a.Length, throwOnEndOfStream: false)) > 0)
        {
            file.ReadExactly(b, 0, n);
            if (!a.AsSpan(0, n).SequenceEqual(b.AsSpan(0, n))) return false;
        }
        return true;
    }
}
