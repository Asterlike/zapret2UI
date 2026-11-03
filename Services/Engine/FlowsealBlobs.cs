using System.IO;
using Zapret2UI.Services.Infrastructure;

namespace Zapret2UI.Services.Engine;

/// <summary>
/// Fake packets taken from Flowseal/zapret-discord-youtube (MIT) that the zapret2 release does not ship.
/// They ride inside our exe and are unpacked into <c>files\flowseal\</c> beside the engine's own blobs —
/// a folder of our own, so an engine update (which copies its release over <c>files\</c>) never touches
/// them, and a declaration can never point at a file the update removed.
///
/// <para>What they are, checked by parsing each file rather than trusting its name: ClientHellos signed
/// with Russian names the TSPU leaves alone — <c>www.sferum.ru</c>, <c>5ka.ru</c>, <c>4pda.to</c>,
/// <c>staging-sochi-park.predict.mail.ru</c> — and <c>stun2.bin</c>, a STUN binding request Flowseal sends
/// as a TLS fake. Their <c>tls_clienthello_max_ru.bin</c> is left out: it carries <c>www.onetrust.com</c>.</para>
/// </summary>
internal static class FlowsealBlobs
{
    /// <summary>Sub-folder of <see cref="AppPaths.FilesDir"/>, as the <c>{FILES}</c> token sees it.</summary>
    internal const string Folder = "flowseal";

    /// <summary>Blob name as strategies use it in <c>blob=</c> / <c>seqovl_pattern=</c>, and its file.</summary>
    internal static readonly (string Name, string File)[] All =
    {
        ("tls_sferum", "tls_clienthello_www_sferum_ru.bin"),
        ("tls_sochi", "tls_clienthello_sochi_park.bin"),
        ("tls_4pda", "tls_clienthello_4pda_to.bin"),
        ("tls_5ka", "tls_clienthello_5ka_ru.bin"),
        ("stun2", "stun2.bin"),
    };

    /// <summary>The global <c>--blob=</c> declarations for every strategy's argument list. Declared
    /// whether or not a strategy uses them: since zapret2 v1.0.4 a blob used but not declared silently
    /// stops the whole execution plan, and declaring one too many costs nothing.</summary>
    internal static IEnumerable<string> Declarations =>
        All.Select(b => $"--blob={b.Name}:@{{FILES}}\\{Folder}\\{b.File}");

    /// <summary>Unpack the files if any is missing or differs. Called before every winws2 start; a file
    /// is rewritten only when its bytes differ, and lands under a temporary name first. Throws when the
    /// folder cannot be written — winws2 would refuse a declared blob it cannot read anyway, and the
    /// message here names the cause.</summary>
    internal static void EnsureExtracted()
    {
        string dir = Path.Combine(AppPaths.FilesDir, Folder);
        Directory.CreateDirectory(dir);
        foreach (var (_, file) in All)
            EmbeddedFile.Extract("Zapret2UI.flowseal." + file, Path.Combine(dir, file));
    }
}
