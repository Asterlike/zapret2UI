namespace Zapret2UI.Services.Warp;

/// <summary>
/// Cloudflare's own answer to «am I actually inside WARP?», published at <c>/cdn-cgi/trace</c> as a few
/// <c>key=value</c> lines.
///
/// <para>This is the only end-to-end proof either transport has. A completed WireGuard handshake and a
/// listening MASQUE proxy both prove that two programs agreed on something; neither proves a packet
/// reached the other side and came back. Asking Cloudflare where it thinks we are does.</para>
/// </summary>
internal static class WarpTrace
{
    /// <summary>Where Cloudflare says the request came from. <paramref name="InsideWarp"/> is the verdict;
    /// the address and the airport code are what make a failure legible in the journal — «идёт мимо
    /// туннеля» is an accusation, «вышли с 144.31.241.124, Франкфурт» is a fact the user can act on.</summary>
    /// <param name="Location">The country the exit address is seen as — what a geo block keys on.</param>
    /// <param name="Colo">Airport code of the Cloudflare edge that answered. NOT the same thing as the
    /// country: a tunnel can run through Frankfurt and still come out as Russia, so both are kept.</param>
    internal readonly record struct Result(bool InsideWarp, string Ip, string Location, string Colo);

    /// <summary>Read a trace body. Null when there is no <c>warp=</c> line at all — that is not a "no",
    /// it means whatever answered was not Cloudflare, and the two must not be collapsed.</summary>
    internal static Result? Parse(string body)
    {
        bool seen = false;
        bool inside = false;
        string ip = "", loc = "", colo = "";

        foreach (string raw in body.Split('\n'))
        {
            string line = raw.Trim();
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;

            string key = line[..eq];
            string value = line[(eq + 1)..];

            switch (key)
            {
                // warp=on for a WireGuard or MASQUE client, warp=plus for a paid one, warp=off for
                // traffic that reached Cloudflare some other way.
                case "warp":
                    seen = true;
                    inside = value.Length > 0 && value != "off";
                    break;
                case "ip": ip = value; break;
                case "loc": loc = value; break;
                case "colo": colo = value; break;
            }
        }

        return seen ? new Result(inside, ip, loc, colo) : null;
    }
}
