namespace Zapret2UI.Services.Warp;

/// <summary>
/// The sites the HMS chain carries by default («Вести эти сайты через цепочку») — and, just as deliberately,
/// the ones that do not.
///
/// <para><b>Why a list rather than «весь трафик».</b> The chain that gives a foreign exit costs latency
/// and bandwidth — through Tor it is seconds and single-digit megabits. Sending the whole machine that
/// way to reach two websites is the kind of trade nobody would make knowingly, so the routing is aimed:
/// these names go the long way round, everything else keeps going straight out as before.</para>
///
/// <para><b>Why Google is only three entries.</b> Gemini lives on <c>gemini.google.com</c> but the page
/// also talks to <c>www.google.com</c>, <c>gstatic.com</c> and <c>clients6.google.com</c> — names shared
/// with YouTube, Gmail and Search. Routing those would drag the user's whole Google account through a
/// foreign address to reach one chat window, which is both slower and far more likely to trip Google's
/// own «new sign-in location» machinery than the geo-block it was meant to solve. Measured in a browser:
/// the chat itself is served from <c>gemini.google.com</c>, so that is what gets routed. The login stays
/// on the user's own address.</para>
///
/// <para><b>Suffix match, not substring.</b> Each entry matches the name itself and anything under it,
/// so <c>openai.com</c> covers <c>api.</c>, <c>auth.</c> and <c>platform.</c> without listing them —
/// and <c>notopenai.com</c> is not a match, which a naive substring test would get wrong.</para>
/// </summary>
internal static class AiDomains
{
    /// <summary>One place to edit. Anthropic is deliberately absent — the owner chose the three services
    /// below for the first version — and adding it later is this line plus a rebuilt PAC.</summary>
    internal static readonly IReadOnlyList<string> Default = new[]
    {
        // OpenAI. The app and the API refuse by country; the two content hosts carry the files the
        // session uploads and downloads, and sending those from a different country than the session
        // itself is exactly the mismatch anti-fraud looks for.
        "chatgpt.com",
        "openai.com",
        "oaistatic.com",
        "oaiusercontent.com",

        // Google — only the AI surfaces themselves, see the note above.
        "gemini.google.com",
        "aistudio.google.com",
        "generativelanguage.googleapis.com",

        // xAI. grok.com serves the app and cdn.grok.com under it; x.ai carries the account and the API.
        "grok.com",
        "x.ai",
    };

    /// <summary>Names that go PAST the chain even though one of the entries above covers them.
    ///
    /// <para><b>Why a second list and not just a shorter first one.</b> The match is by suffix, so
    /// <c>grok.com</c> catches <c>cdn.grok.com</c> too and no amount of editing the list above can let one
    /// through while routing the other. This is the exception list, and a subdomain is the only thing it
    /// is for.</para>
    ///
    /// <para><b>Why this one entry.</b> MEASURED in a browser: opening grok.com makes 219 requests, and
    /// 188 of them go to <c>cdn.grok.com</c> — static files that carry no session and care nothing about
    /// which country asks for them. Through a chain that adds a second of latency to every round trip,
    /// that is the difference between a page that loads and a page that crawls. The page's own host stays
    /// routed, so what the site checks still comes from the chosen country.</para>
    ///
    /// <para><b>The honest cost,</b> and it is said in the interface too: a name listed here is fetched
    /// from the user's own address — or through the ordinary WARP proxy when that is on — and not from
    /// the country the chain was raised for. ChatGPT gets nothing from this list, deliberately: measured,
    /// it serves its own application from <c>chatgpt.com</c> itself (73 requests, 543 KB, not one of them
    /// on another host), so there is nothing there to send the fast way.</para></summary>
    internal static readonly IReadOnlyList<string> Bypass = new[]
    {
        "cdn.grok.com",
    };

    /// <summary>The exceptions as the settings file stores them, which is also what a factory reset puts
    /// back. Stored as text rather than as «empty means the shipped list» — the trick the routed list
    /// uses — because a user who empties this box means it.</summary>
    internal static readonly string BypassText = string.Join(Environment.NewLine, Bypass);
}
