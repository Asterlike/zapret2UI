**EN** | [RU](manual_zapret2UI.md "Читать по-русски")

# Zapret2UI — the full manual

A detailed reference for **Zapret2UI**, the graphical shell for the
[zapret2](https://github.com/bol-van/zapret2) DPI-bypass engine (`winws2`). It covers every feature,
tab and setting, the built-in strategies, the format for writing your own, the built-in Telegram
proxy, the files on disk and troubleshooting.

If you just want to get started, read the [README](README.en.md). This document is for anyone who
wants to understand the program as a whole and tune it finely.

> 🌐 **The same material as a website:** [asterlike.github.io/zapret2UI/en](https://asterlike.github.io/zapret2UI/en/) —
> with search, section navigation and screenshots that open full size. It also carries a
> [line-by-line breakdown of all nine built-in strategies](https://asterlike.github.io/zapret2UI/en/strategies.html),
> which is not in this file. This file is the offline version, as one document.

> 🌐 **The program is available in Russian and English.** Switch with the **RU | EN** toggle at the top
> of the Home screen or in Settings — switching restarts the app to apply. This manual describes the
> English interface; the engine's own log output (from `winws2`) stays as raw technical text.

> This is the manual for the **application**. Documentation for the engine itself (the complete list of
> desync verbs and `winws2` flags) is the official one:
> [bol-van/zapret2 · manual.en.md](https://github.com/bol-van/zapret2/blob/master/docs/manual.en.md).

Русская версия этого документа: [manual_zapret2UI.md](manual_zapret2UI.md).

---

## Contents

1. [What this is and what it can do](#1-what-this-is-and-what-it-can-do)
2. [Requirements and installation](#2-requirements-and-installation)
3. [How the bypass works in detail](#3-how-the-bypass-works-in-detail)
4. [Interface: modes and tabs](#4-interface-modes-and-tabs)
5. [Built-in strategies](#5-built-in-strategies)
6. [Automatic selection and generation](#6-automatic-selection-and-generation)
7. [Bypass scope, game filter, QUIC](#7-bypass-scope-game-filter-quic)
8. [Host lists, your own targets and IP-based bypass](#8-host-lists-your-own-targets-and-ip-based-bypass)
9. [Strategy tokens](#9-strategy-tokens)
10. [Writing your own strategies](#10-writing-your-own-strategies)
11. [The built-in Telegram proxy](#11-the-built-in-telegram-proxy)
12. [WARP and changing your address](#12-warp-and-changing-your-address)
13. [TCP timestamps](#13-tcp-timestamps)
14. [Per-network memory](#14-per-network-memory)
15. [Settings: the full list](#15-settings-the-full-list)
16. [Updates](#16-updates)
17. [Files and folders on disk](#17-files-and-folders-on-disk)
18. [Troubleshooting and error codes](#18-troubleshooting-and-error-codes)
19. [Building from source, and architecture](#19-building-from-source-and-architecture)
20. [Links and credits](#20-links-and-credits)

---

## 1. What this is and what it can do

Zapret2UI does not "break" blocking at the server and changes nothing on the sites themselves. It
drives the `winws2` engine, which slightly alters the first network packets of your connections on the
fly so that the provider's equipment (**DPI/TSPU**) cannot tell which site you are connecting to, while
the server itself understands everything correctly. A set of such techniques is called a **strategy**.

What it offers:

- Bypassing blocks and throttling for **Discord** and **YouTube** with one button.
- A built-in **Telegram proxy** (MTProto → WebSocket through Cloudflare) that gets around IP-based
  blocking of Telegram; it works separately and **without administrator rights**.
- **9 ready-made strategies**, **automatic selection** of the best one, and **generation** of a personal
  strategy for your provider.
- **Per-network memory**: a working strategy is remembered for each network and turns on by itself.
- **Auto-repair**: if the bypass falls over, the program re-selects a working variant.
- **Diagnostics**: an availability table by service.
- **Built-in Cloudflare WARP** as a local SOCKS5 proxy: changes the address you arrive from, with no
  administrator rights and no changes to your network (see
  [§12](#12-warp-and-changing-your-address)).
- **Your own host lists and targets**, **IP-based bypass** (ipset), a **game filter**, and **disabling
  QUIC**.
- **A single self-contained `.exe`**; the engine is downloaded on first launch and verified against
  SHA-256.

---

## 2. Requirements and installation

**Requirements:**

- Windows 10 or 11, **x64** (an x86 build of the engine exists, but the 64-bit one is the main one).
- **Administrator rights** for the bypass (the engine loads the WinDivert driver into the kernel). The
  Telegram proxy does not need administrator rights.
- Internet access on first launch (to download the engine).

**Installation:**

1. Download `Zapret2UI.exe` from the
   [releases page](https://github.com/Asterlike/zapret2UI/releases/latest). It is a single file — no
   installation required, and no .NET to install.
2. Run it **as administrator** (right-click → "Run as administrator", or accept the UAC prompt).
3. On first launch the program downloads the `winws2` engine into `%LOCALAPPDATA%\Zapret2UI\engine` and
   verifies the download against its **SHA-256** checksum.

The engine and the application are updated separately (see [§16](#16-updates)).

---

## 3. How the bypass works in detail

- **SNI.** At the start of almost any TLS connection the browser sends the site name in plain text (the
  **SNI** field in the ClientHello). The DPI reads it and, against a blacklist, cuts the connection
  (RST, a "hang", or substitution).
- **Desync.** `winws2` intercepts outgoing packets through **WinDivert** and applies desync to them: it
  splits the ClientHello, inserts "fake" packets, plays with TTL, checksum, seq, ack and timestamps — so
  that the DPI "sees" something other than what actually goes to the server. A correctly configured fake
  **dies** before the server (it never arrives), while the real packet gets through.
- **Why there is no single strategy for everyone.** Different providers filter differently, so a
  technique that punches through for one is useless with another. Hence **selection** and
  **generation**.
- **Allow-list by default.** The program only bypasses what is on the lists (Discord/YouTube plus your
  targets) rather than all traffic, so games and applications are not disturbed (see
  [§7](#7-bypass-scope-game-filter-quic)).
- **What the bypass cannot do.** It lifts blocking **by name**. If a resource is cut off **by IP**, only
  a VPN helps — or, for Telegram, the built-in proxy.

### This is zapret2, not the ordinary zapret

There are two engines, and both were made by the same author, bol-van. The first (`winws.exe`) is the
one most builds, guides and videos grew around. The second (`winws2.exe`) is the next generation, and
Zapret2UI works with that one. It is not a fork or a competitor, but **there is no compatibility
between them**.

The main change is not in the set of techniques but in **where their logic lives**. In the first zapret
every technique was baked into the executable. In the second, the logic is moved out into Lua scripts
next to the engine (the `lua\` folder), which can be read and extended. Everything else follows from
that:

| | zapret (the first) | zapret2 |
|---|---|---|
| Engine | `winws.exe` | `winws2.exe` |
| Techniques set with | `--dpi-desync=` | `--lua-desync=` |
| Where a technique's logic lives | baked into the binary | Lua scripts, open to read |
| Fake packets | separate flags (`--dpi-desync-fake-tls`) | named blobs (`--blob=name:path`) |
| Several techniques in a row | pre-baked combinations | as many `--lua-desync` entries per profile as you like |
| Control on the fly | none | orchestrators (`circular` and others) switch techniques on failure |
| Driver | its own | its own, a different one (do not run both versions at once) |

**How to tell which engine a guide you found is for.** If it mentions `winws.exe`, `--dpi-desync=` or
`--dpi-desync-ttl`, that is the first zapret and it **does not apply** here. If it mentions
`winws2.exe`, `--lua-desync=` or `--blob=`, that is zapret2 — but you still have to check the blob names
(see [§10](#10-writing-your-own-strategies)).

> ⚠️ A word about builds named something like "Zapret 2 GUI": the digit there is the version of the
> build, not the generation of the engine — inside, they most often run the first zapret with
> hand-written Lua scripts bolted on. Their strategies are fine as a source of ideas, but you cannot
> carry lines over directly.

---

## 4. Interface: modes and tabs

At the top centre is the **Простой / Расширенный** (Simple / Advanced) switch.

- **Simple** — one big "Включить обход" (Turn on bypass) button, the Telegram and WARP cards, and the
  target selector. Nothing else.
- **Advanced** — eight tabs: **Главная** (Home), **Стратегии** (Strategies), **Хостлисты** (Host
  lists), **Диагностика** (Diagnostics), **Журнал** (Journal), **Telegram**, **WARP**, **Настройки**
  (Settings).

### 4.1. Home

- **The "Включить / Выключить обход" button** — starts and stops the engine. Above it are a dot and a
  status caption: `Остановлен` (Stopped) → `Запуск…` (Starting) → `Работает` (Running), and
  `Остановка…` (Stopping).
- **Bypass target** ("ЧТО ПОДОБРАТЬ"): `Discord + YouTube`, `Discord` or `YouTube` — what the program
  tries to open during selection and generation.
- **The Telegram card** — the address and secret of the built-in proxy, with "Открыть" (Open) and
  "Копировать" (Copy) buttons.
- **The switch in the Telegram card** — turns the proxy on and off (mirrors the Telegram tab).
- **The WARP card** — the Cloudflare proxy switch and a "Настроить WARP" (Configure WARP) button that
  opens the tab. Remember that while the proxy is on, the bypass scope widens to every site
  ([§12](#12-warp-and-changing-your-address)).
- **"Подобрать стратегию"** (Select a strategy) — works through the proven strategies and keeps the one
  that opens your chosen targets. Start here: it follows a fixed list and is therefore more predictable.
- **"Сгенерировать стратегию"** (Generate a strategy) — assembles a variant from scratch, separately for
  Discord and YouTube. Slower; needed when selection did not punch through.
- A QR code and a **"Поддержать автора"** (Support the author) link.

### 4.2. Strategies

The list of ready-made bypass methods. Select a row → **"Применить"** (Apply), and the bypass restarts
on it. Hover over a row and a tooltip shows the full name and description. The recommended strategy is
marked and enabled by default. **Your own** strategies, saved after selection or generation, also appear
here. The set is covered in [§5](#5-built-in-strategies).

### 4.3. Host lists

Lists of domains (one per line — the `winws2` format) the bypass applies to: `youtube`, `discord` and
others. The built-in lists refresh themselves on every launch; your own lines are left alone. You can
create your own list, edit it and choose the active one. Details in
[§8](#8-host-lists-your-own-targets-and-ip-based-bypass).

### 4.4. Diagnostics

Buttons on the left, the **availability table** on the right: rows by service (Discord: login/API,
Gateway, CDN, updates, the CF challenge; YouTube: web, short links, images, video redirect; plus Google,
Cloudflare, DNS), columns HTTP / TLS 1.2 / TLS 1.3 / Ping. A green cell means it opens, a red one means
it is cut. The buttons:

- **Подобрать лучшую** (Select the best) — works through the ready-made strategies, checks availability
  and keeps the best.
- **Сгенерировать стратегию** (Generate a strategy) — assembles a **personal** strategy for your network
  (slower, more precise).
- **Диагностика** (Diagnostics) — simply check what opens (changes nothing).
- **Проверка DPI** (DPI check) — determine whether **the provider is interfering via DPI specifically**
  (by site name, or by volume/packet count) rather than the site just being unreachable (see below).
- **Свои цели** (My targets) — add a domain so it is checked and bypassed too.

Selection and generation start the engine and need administrator rights. Details in
[§6](#6-automatic-selection-and-generation).

**How the "DPI check" works.** Ordinary diagnostics answers "does it open or not". The DPI check answers
a different question: **is the provider interfering at the DPI level**. For each sensitive host
(`discord.com`, `gateway.discord.gg`, `cdn.discordapp.com`, `www.youtube.com`) it takes two steps:

1. **A TCP connection** to port 443. If that does not go through, this is not name-based DPI but plain
   unreachability (routing or IP blocking); it says exactly that — "нет соединения" (no connection).
2. If the TCP connection went through (the server **accepted** it, so it is alive), a **TLS ClientHello
   with the real site name (SNI)** is sent, and what happens to that specific packet is observed:
   - **a drop (RST)** right after the ClientHello — the provider's DPI **injected a connection reset**
     (the classic Russian signature): the server had already agreed to TCP, so a reset on the packet
     carrying the site name comes from a box in the middle, not from the server;
   - **a freeze** — the ClientHello left, there is no reply, the connection hangs: the packet with the
     SNI was **silently dropped**;
   - **the handshake completed** — the DPI is not interfering ("clean").

The result is shown as a verdict: "the provider is filtering via DPI: reset/freeze" (which a bypass
fixes), "no signs of DPI", or "no connection". It is most accurate to run it **with the bypass turned
off** — that shows the provider's raw behaviour; with the bypass on, "clean" means the bypass has
already dealt with the DPI.

**The second kind of check — a volume/packet limit (the "TCP 16-20" method).** Besides blocking by site
name, a provider may **throttle the connection itself by packet count**: the TCP connection and the first
few dozen kilobytes go through fine, and then the stream **stalls** (around 16–20 packets in each
direction). That is the "the site opens, but video buffers / media will not load / the connection drops
on large transfers" case — the short TLS handshake from the check above never hits the limit and shows as
"clean". So the application separately **pulls a large download** (through a service test channel) and
watches: if the stream passes the threshold comfortably, there is no limit; if it **stalls in the first
few dozen KB**, that is the "TCP 16-20" method. Important: a TLS/SNI bypass does **not always** lift such
a volume limit — sometimes only a change of strategy or network helps. The idea for the method comes from
[hyperion-cs/dpi-checkers](https://github.com/hyperion-cs/dpi-checkers) (Apache-2.0).

### 4.5. Journal

The first place to look if the bypass did not start — the reason is here (a start-up error, the driver,
the antivirus, an exit code). The tab is split in two, so a proxy problem does not get lost among the
engine's output:

- **Движок** (Engine) — live output from `winws2`: start-up, driver attachment, the applied strategy,
  errors.
- **Telegram** — output from the built-in proxy: the listener starting, the path chosen to the data
  centres, the switch to domains behind Cloudflare, connection errors.

Each pane has its own **"Копировать"** (Copy) and **"Очистить"** (Clear) buttons. The logs are also
written to files (see [§17](#17-files-and-folders-on-disk)).

The **`--debug`** switch in the header turns verbose mode on for both panes at once. The engine receives
`--debug=1` and records which connections the techniques were applied to and why; without that, working
out that a strategy silently did nothing is all but impossible. The proxy adds three lines per
connection:

```
[tg-proxy] #7 DC2: открыто через прямой IP
[tg-proxy] #7 DC2: трафик пошёл
[tg-proxy] #7 DC2: закрыто через 45,2 с — Telegram закрыл канал
```

Those read: connection #7 to DC2 was opened through the direct IP, traffic started flowing, and it was
closed after 45.2 seconds because Telegram closed the channel.

**Read these lines by their `#N` number, not in sequence.** Telegram Desktop keeps several connections
open at once and re-opens them all on any network change: the closing lines of old connections end up
interleaved with the opening lines of new ones. Without the number, an adjacent pair looks as though a
connection that just opened lived for 45 seconds — when in fact those are two different connections.

In normal mode the proxy logs only what you can act on: start-up, the first channel opened, a front that
was benched, a failure to connect.

> The engine reads the flag only at start-up, so toggling it **restarts the bypass** — the connection
> drops for a second. The choice is remembered between sessions; do not forget to turn verbose mode off
> when you are done. The journal keeps the last 3000 lines, and in verbose mode ordinary messages scroll
> away noticeably faster.

Useful landmarks in the output: a line about windivert initialising and capture starting means the driver
came up; a line about TCP timestamps being enabled means the mechanism from [§13](#13-tcp-timestamps)
fired; a message about a non-existent desync function means the strategy names a verb the engine does not
have.

### 4.6. Telegram

A dedicated page for the built-in proxy: turning it on, the address and secret, the "Открыть в Telegram"
button, changing the port, starting the proxy automatically, and a step-by-step guide. Details in
[§11](#11-the-built-in-telegram-proxy).

### 4.7. Settings

Interface language, scale, engine updates, autostart, notifications, auto-repair, bypass scope, the
game filter, QUIC, covering the Telegram proxy with the engine, IP-based bypass, "Добавить в
исключения" (Add to exclusions), the environment check and the beginner's walkthrough. At the bottom:
backup, settings reset and the log files. The full list is in [§15](#15-settings-the-full-list).

---

## 5. Built-in strategies

| Strategy | What for / what is inside |
|---|---|
| **Комбо (рекомендуемый)** — Combo (recommended) | Enabled by default. The best option for each service in one command, routed by SNI: Discord → `hostfakesplit` (fast login plus media), YouTube/Google → `fake`+`multidisorder`, everything else → `hostfakesplit`. Voice: STUN plus an RTP fix. Start here. |
| **Комбо — отечественный (VK, целевой)** — domestic (VK, targeted) | For Russia: the fakes for Discord are disguised as `vk.com` (TSPU does not drop domestic traffic). Like the recommended one it uses `hostfakesplit` on the SNI marker, so it gets you into login and to the servers. Use it if the google variants are "green but Discord will not open". |
| **Комбо — Flowseal ALT10 (двойной fake + ts)** — double fake + ts | A translation of the Flowseal general (ALT10) strategy to nfqws2: no splitting — a double fake packet (google plus a vk ClientHello) with ts fooling, and voice through a domestic QUIC blob. For many people "everything just works". |
| **Комбо — Flowseal ALT11 (fake+ts → seqovl)** | The second Flowseal variant: a fake prime with ts plus `multisplit` with a large `seqovl` (a real google ClientHello as the pattern). If ALT10 did not land, try this one. |
| **Комбо — Flowseal (multisplit seqovl)** | A working profile based on splitting the packet with `seqovl`. |
| **Комбо — Flowseal ALT (fake+fakedsplit)** | Another Flowseal one: `fake` with `tcp_ts` plus `fakedsplit`. Try it if the `multisplit` variant did not punch through. |
| **Комбо — окно (wssize)** — window | Bypass by shrinking the TCP window (`wssize`) — helps with some providers. |
| **Discord — голос (QUIC-фейк)** — voice (QUIC fake) | For when Discord text works but voice "connects and nobody can be heard". |
| **Discord — адаптивный (circular, эксперим.)** — adaptive | Discord only. The `circular` orchestrator alternates strategies on the fly by itself (hostfakesplit → double fake → seqovl), catching RSTs and retransmissions, and settles on the first working one. Give it a few seconds after starting. Requires the `zapret-auto.lua` library, which is loaded automatically. |

The order to try them by hand is top to bottom, as in the table. Voice is separate: `Discord — голос
(QUIC-фейк)`.

### How a combo is built

The seven strategies prefixed "Комбо" are assembled from one skeleton and differ **only in the techniques
used for TLS**. Everything else is shared, so there is no need to take each one apart again.

The key idea is **routing by site name**: Discord, YouTube and all other traffic go through different
techniques within one command. That matters because what punches through for Discord often does not suit
YouTube, and the other way round.

The shared part before the first `--new`: the capture width (`{WF_TCP}`/`{WF_UDP}`), the name cache
(`--ipcache-hostname=1`, required by `wssize`), the blobs (`tls_google`, `tls_vk`, `tls_sber`, `tls_gos`,
`quic_google`, `quic_vk`) and the filter fragments for STUN and QUIC. Then seven profiles, in this order:

| Profile | What it catches | What it does |
|---|---|---|
| 1. Discord TLS | `--filter-tcp=443-65535 --filter-l7=tls` plus the Discord list | Login, gateway, media. **This is where the technique that distinguishes the strategies sits** |
| 2. YouTube TLS | the same plus the YouTube/Google list | Its own technique, usually different from Discord's |
| 3. Other TLS | everything else, except the exclusion list | The fallback; the exclusions protect banks and government services |
| 4. QUIC YouTube | `--filter-udp=443-65535 --filter-l7=quic` plus the YouTube list | A fake with a QUIC blob, `repeats=11` |
| 5. QUIC Discord | the same plus the Discord list | Needed for attachments and the CDN over HTTP/3 |
| 6. QUIC other | everything else, except the exclusions | A fake with the standard blob |
| 7. Discord voice | `--filter-udp=19294-19344,50000-65535 --filter-l7=discord,stun` | A separate technique for voice |

In the TLS profiles two "sticky" flags always come before the technique: `--payload=tls_client_hello`
(restricting it to the first packet of the handshake) and `--out-range=-d10` (to the first outgoing data
packets). Their order matters — see [§10](#10-writing-your-own-strategies).

**The voice profile** deserves an explanation, because the logic there is inverted: a QUIC blob is mixed
into the voice stream that is **junk** as far as that stream is concerned. The server discards it without
parsing, so the voice stream's numbering is not corrupted, and the fake exists purely for the DPI. The
port range is taken as the whole high range rather than a narrow `50000-50100`: the narrow one missed half
the voice servers, which produced a permanent ping of 5000.

> 📖 **A line-by-line breakdown of all nine strategies** — what each argument does and why it was chosen —
> is on the site:
> [Strategies explained](https://asterlike.github.io/zapret2UI/en/strategies.html). It also carries a
> "symptom → which strategy to try" table.

---

## 6. Automatic selection and generation

Both features live on the **Диагностика** (Diagnostics) tab; a window with live progress opens while
they run.

- **Подобрать лучшую** (Select the best; fast). The program launches candidates from the built-in
  catalogue one by one, checks the availability of the target hosts under each (TLS 1.2 and 1.3), counts
  the failures and picks the variant with the fewest. The result can be **applied** and **saved as a
  strategy**.
- **Сгенерировать стратегию** (Generate a strategy; more precise, slower). The program tests individual
  bypass components, optimises Discord and YouTube separately, then assembles them into a single combo
  and checks the whole build. The output is a **personal strategy** for your network, which can also be
  saved.

The target (Discord+YouTube / Discord / YouTube) is set with the switch and determines which hosts count
as "targets". The strategy that is found is tied to the current network (see
[§14](#14-per-network-memory)).

> Important: both selection and diagnostics check availability at a low level. A "green" check does not
> always mean the site will open in a browser — it may be cut some other way (ECH, IP blocking). If the
> check is green and the site still does not work, try another strategy, turn QUIC off, or, for IP-based
> blocking, use a VPN.

---

## 7. Bypass scope, game filter, QUIC

Three settings on the **Настройки** (Settings) tab determine *what exactly* the engine touches.

- **Bypass scope** (`BypassAllSites`):
  - **Lists only** (the default; safe, as in Flowseal) — only domains from the host lists
    (YouTube/Discord/Telegram) plus your targets are bypassed. The catch-all profiles (bypassing all
    other TLS/QUIC) are re-pointed at your targets or dropped, so games and applications that are not on
    the lists are **left alone**.
  - **All sites** — all TLS/QUIC is bypassed (except the exclusion list: banks, government services and
    so on). Convenient, but it may break a game or application that is not on the exclusion list.
  - **While the WARP proxy is on** the scope widens to "all sites" by itself, whatever the switch says:
    otherwise the connection to Cloudflare does not come up (see
    [§12](#12-warp-and-changing-your-address)). Your choice is kept and comes back as soon as the proxy
    is switched off.
- **Game filter** (`GameFilter`): widens **UDP** capture to all high ports
  (`--wf-udp-out=443-65535`) so the bypass also reaches throttled games. Off by default: over UDP only
  443 (QUIC), STUN and the Discord voice range are caught, and game traffic goes straight through.
  **TCP capture is not affected by it** — that is always wide (`80,443-65535`), because Discord media and
  attachments live on high TCP ports and a narrow capture would silently leave them without a bypass.
- **QUIC / HTTP-3** (`DisableQuic`): when enabled, QUIC for the bypassed services is **dropped** and the
  browser falls back to TCP/H2. Turn it on if your provider cuts or throttles QUIC (YouTube "buffers"
  over HTTP-3 but works fine over TCP).

Technically the capture width is set by the `{WF_TCP}`/`{WF_UDP}` tokens (see
[§9](#9-strategy-tokens)); TCP capture is always `80,443-65535`, so Discord media and CDN on high ports
are not lost.

---

## 8. Host lists, your own targets and IP-based bypass

- **A host list** is a text file with one domain per line (`youtube.com`, `discord.gg`, …), with no
  protocol, slashes or asterisks. That is exactly how `winws2` reads them. A domain covers its
  subdomains too: the line `discord.com` also matches `canary.discord.com`.

  What lives in `lists\`:

  | File | What for |
  |---|---|
  | `discord.txt` | Discord domains: login, gateway, CDN, media. Built in, refreshed on launch |
  | `youtube.txt` | YouTube domains and related Google services. Also built in |
  | `exclude.txt` | The **exclusion** list (banks, government services): it works the other way round, protecting those domains from the catch-all profiles |
  | `tgproxy-fronts.txt` | The domains behind Cloudflare for the built-in proxy; needed with `TgProxyCoverage` |
  | `warp-api.txt` | Cloudflare WARP's service domain (device registration). Covered **always**, whatever the bypass scope |
  | your own files | Any list you create. **Not touched** when the built-in ones refresh |

  > ⚠️ Do not add your own domains straight into the built-in lists: they are refreshed on every launch
  > and your edits may be overwritten. Create a separate list, or use "Свои цели" (My targets).
- **The active host list** is substituted into a strategy by the `{HOSTLIST}` token. Named lists use
  `{HOSTLIST:name}` (the file `lists\name.txt`).
- **My targets** (a button on Diagnostics) adds a domain to a separate aggregated list; it is taken into
  account during selection and generation and is bypassed even in "lists only" mode. When you add one,
  subdomains and the same brand in other zones are accounted for (for example, `yandex.ru` →
  `yandex.kz`/`ya.ru`).
- **IP-based bypass (ipset).** If a resource is cut off by IP address, a domain-based bypass does not
  help. On the Telegram/Diagnostics tab there is **"Собрать IP-список Discord"** (Build the Discord IP
  list): the program resolves the Discord domains through `mdig.exe | ip2net.exe` and collects the
  current subnets (CIDR) into `lists\ipset-discord.txt` (no administrator rights needed — only DNS). It
  is attached in a strategy with the `{IPSET}` or `{IPSET:name}` token.

---

## 9. Strategy tokens

Strategies are stored with tokens, which the engine service (`EngineService.BuildArguments`) expands at
launch into real `winws2` arguments:

| Token | Expands to |
|---|---|
| `{FILES}` | the `engine\files` path (blobs: `--blob=x:@{FILES}\fake\stun.bin`) |
| `{WF}` | the `engine\windivert.filter` path (`--wf-raw-part=@{WF}\windivert_part.*.txt`) |
| `{WF_TCP}` | `--wf-tcp-out=80,443-65535` (the TCP capture width) |
| `{WF_UDP}` | `--wf-udp-out=…` (443 plus STUN plus Discord voice; the game filter widens it to `443-65535`) |
| `{HOSTLIST}` | `--hostlist=<active list>` or nothing |
| `{HOSTLIST:name}` | `--hostlist=lists\name.txt` (or nothing if the file is missing) |
| `{EXCLUDE:name}` | `--hostlist-exclude=lists\name.txt` (protecting banks and government services from the catch-alls) |
| `{IPSET}` | `--ipset=<ipset-discord.txt>` if the IP list has been built, otherwise nothing |
| `{IPSET:name}` | `--ipset=lists\ipset-name.txt` (or nothing) |

An empty token (for example `{HOSTLIST}` with no active list) is simply dropped from the command.

---

## 10. Writing your own strategies

A strategy can be created or edited by hand (it is saved in `presets.json`). This is the `winws2`
argument language. What follows are practical rules verified against the local engine; the complete verb
reference is in the [official manual](https://github.com/bol-van/zapret2/blob/master/docs/manual.en.md).

**Command structure:**

- A profile begins with `--new`. The first segment (before the first `--new`) carries the global
  configuration (`--wf-*`, `--blob=`, `--ipcache-*`) plus the first profile.
- Profile filters: `--filter-tcp=`/`--filter-udp=` (ports), `--filter-l7=tls,quic,http,discord,stun`
  (protocol), `{HOSTLIST}`/`{IPSET}` (who to apply to). The order of these flags does not matter.
- **Order matters for the "sticky" flags:** `--payload=`, `--in-range=`, `--out-range=` apply from where
  they are written until they are overridden — put them **before** the `--lua-desync=` they affect.
- `--wf-tcp-out`/`--wf-udp-out` — ports comma-separated in **one** flag (repeating it overwrites).

**Desync verbs** (`--lua-desync=`), confirmed present in the engine:

- `fake` — insert a fake packet (`blob=<name>`). Fooling applies **to the fake only**, so the real packet
  arrives clean. Good for the gateway.
- `multisplit` / `multidisorder` / `multidisorder_legacy` — split (and reorder) the real packet. Fooling
  applies **to ALL segments**, so you **must not** put `tcp_ack`/`badseq`/a broken ttl here (it breaks the
  real connection); only `pos`/`seqovl` (plus optionally `ip_id`, `tcp_ts_up`).
- `hostfakesplit` — a cut on the SNI marker plus a fake host of the same length; it adapts to the
  ClientHello size, which makes it friendly to the Discord gateway. Fooling is mandatory.
- `fakedsplit` / `fakeddisorder` — a split with a fake; fooling on the fakes only.
- `syndata` — data in the SYN. No destructive fooling (`tcp_seq`/`tcp_ack`/`badsum` break the handshake).
- `tcpseg` — one segment with `seqovl`; it does **not** remove the original → add
  `--lua-desync=drop` after it.
- `wssize` / `wsize` — shrink the TCP window.
- `drop` / `pass` — discard or let through.
- **Orchestrators** (`circular`, `repeater`, `stopif`, `condition`) live in `zapret-auto.lua` (the engine
  loads it automatically). `circular` changes strategy on failure: `--lua-desync=circular:fails=N`
  followed by each instance tagged `strategy=K` (K from 1, without gaps); it requires `--in-range=` (a
  cache of incoming RSTs). A `final` mark on an instance stops the rotation.

**Fooling — the critical rules (otherwise you get "green but not working"):**

- **`tcp_ts` must be NEGATIVE** (`tcp_ts=-1000`). PAWS discards the packet with the older (smaller)
  TSval, so the fake has to be the "older" one. A positive `tcp_ts` is a bug: the fake survives and PAWS
  drops the **real** packet instead.
- **`tcp_ts`/`tcp_ts_up` are no-ops without TCP timestamps enabled.** Windows keeps them in `allowed`
  (not `enabled`) by default, so Zapret2UI enables them itself for the session (see
  [§13](#13-tcp-timestamps)).
- **`badsum` is unreliable behind a home NAT** — the router drops a packet with a broken checksum before
  the DPI sees it. On a desktop behind NAT use `tcp_md5` (which adds an MD5 option and is NAT-safe) or
  `tcp_seq` (badseq).
- **`multisplit`/`multidisorder` with `pos=1` alone is a complete no-op** (the engine strips `pos=1`).
  A real second marker is required: `pos=1,midsld`.
- **`seqovl` means different things:** in `multisplit` it is a **number of bytes** (how many bytes of the
  pattern to prepend); in `multidisorder` it is a **position marker**, which must be smaller than the
  first cut point.

**Position markers:** `method, host, endhost, sld, midsld, endsld, sniext, extlen` plus offsets
(`sld+1`, `midsld-2`).

**Blobs (important!).** The package ships `tls_clienthello_www_google_com.bin` and others. In strategies,
use the names declared in the globals: **`tls_google`**, **`tls_vk`**, **`quic_google`**, **`quic_vk`**.
Custom blobs such as `tls5`/`tls7`/`tls1` from third-party builds do **not** exist here — map any `tlsN`
to `tls_google`. They are declared like this:
`--blob=tls_google:@{FILES}\fake\tls_clienthello_www_google_com.bin`.

**A small example** (one Discord TLS profile with fake plus split, schematically):

```
{WF_TCP} {WF_UDP}
--blob=tls_google:@{FILES}\fake\tls_clienthello_www_google_com.bin
--filter-tcp=443-65535 --filter-l7=tls {HOSTLIST:discord}
  --lua-desync=fake:blob=tls_google:tcp_md5:tcp_seq=-10000:repeats=6
  --lua-desync=multisplit:pos=1,midsld:seqovl=681:seqovl_pattern=tls_google
--new
--filter-udp=443-65535 --filter-l7=quic {HOSTLIST:discord} --payload=quic_initial
  --lua-desync=fake:blob=quic_google:repeats=11
```

You can inspect the whole line without launching the engine using the command preview in the interface.
Remember that `winws2` runs as administrator, so the only real test is to run it.

---

## 11. The built-in Telegram proxy

Telegram is blocked differently from websites — often **by IP**, and an ordinary desync does not help.
So the program has **a separate built-in proxy**.

- **What it does.** It raises a local MTProto proxy on `127.0.0.1:1443`; every Telegram connection is
  carried to its data centres over **WebSocket-TLS**, and through **domains behind Cloudflare** when the
  direct path is blocked. That is how the connection survives IP-based blocking.
- **Rights.** Administrator is **not needed** (a local listener plus outgoing TLS). It works
  independently of the main bypass button, and even when the window is minimised to the tray.
- **How to turn it on.** The Telegram switch (on Home or on the Telegram tab) → "Открыть в Telegram"
  (Open in Telegram), and the proxy registers itself. By hand: Telegram → Settings → Data and Storage →
  Proxy → Add → MTProto, then fill in the address, port and secret (there is a copy button next to
  them).
- **Port and secret.** The default port is `1443` (if it is taken, the nearest free one is used;
  changing the port restarts the proxy). The secret (32 hex characters) is saved so the `tg://proxy`
  link does not change between launches. The link is
  `tg://proxy?server=127.0.0.1&port=<port>&secret=dd<secret>`.
- **Ordinary Telegram only.** The client has a **test-network** mode — a separate Telegram
  infrastructure for developers (the client marks such data centres by adding 10000 to the number). The
  proxy only leads to the ordinary data centres, so it recognises such a connection and refuses it with a
  line in the journal suggesting you turn test mode off. Without that check it would silently go to the
  ordinary DC2 and hang on "connecting" forever, with nothing in the journal to explain it.

**How it differs from Flowseal/tg-ws-proxy.** The mechanism is the same, but:

- The original is a separate **Python** program built into a standalone `.exe` (PyInstaller).
- Here it is a **native port of the protocol in C#**, built straight into the application: no second
  process, no Python runtime, no separate binary.
- The `dd` transport (obfuscated MTProto) is implemented — the client is local (loopback), so FakeTLS
  (`ee`) is unnecessary and has been left out. The working path is preserved: resolution through DoH and
  domains behind Cloudflare (fronting) with a pool of fronts and temporary benching of the bad ones. The
  code is MIT, with thanks to Flowseal.
- **Chat and media travel by different routes.** Telegram opens its file connections **separately from
  the chat and several at a time**. The original sends both the same way, so a download and the chat
  pile into one node — hence the familiar complaint that "messages arrive instantly but photos and
  videos never load".

  Here the route is chosen by **lane**. Chat takes Telegram's direct node: it opens fast, with no
  intermediary, and messages are small. Media prefers the **Cloudflare-fronted domains**, whose
  addresses carry bulk at full speed — and parallel transfers spread across different nodes, each lane
  holding its own preferred node and its own cooldown list. Each route also **backs up the other**: if
  one side is unavailable the connection takes the second, and a share of the attempts is reserved for
  exactly that.

  It works this way because the direct route lands on **Telegram's own addresses** — the ones more often
  rate-limited than blocked outright. A rate limit does not hinder a handshake, so the connection opens
  as if nothing were wrong: the chat flies while a download on that very same route barely crawls.

  Verified by probe: the fronts have **no media edge of their own** (`kws{dc}-1.<domain>` does not
  resolve) — Telegram identifies media by the negative data-centre number in the relay init, not by the
  hostname. Both lanes therefore share one hostname, and can only be separated at route selection.
- **Large messages are reassembled.** The channel may split a single message across several frames. Only
  large ones get split — that is, files, not chat. Losing a continuation desynchronises the cipher
  stream permanently: from that point the client reads garbage and drops the connection, which shows up
  as "media loads sometimes and sometimes not". Frames are joined back together, and control packets
  arriving between them do not disturb the assembly.
- **The journal shows volume and speed.** With the verbose journal on, every connection closes with a
  line carrying its lane ("chat" or "media"), its route, the bytes in each direction and the average
  rate. A connection that opened, relayed a couple of kilobytes and died no longer looks healthy.

---

## 12. WARP and changing your address

The bypass and WARP solve **different** problems, and that is the main thing to understand about this
tab.

The bypass rewrites packets. It can push a connection through a block — the case where your ISP will
not let you reach a site at all. But the address you arrive from stays yours.

WARP does the opposite: it does not break a block, it substitutes the address. With the proxy on,
traffic leaves through Cloudflare and a site sees its address instead. That helps where it is **your
address** that is blocked — when a service has shut out an entire ISP subnet, say.

> **This will not lift geo-blocks.** Free WARP is anycast: you land on the nearest Cloudflare node, not
> one you chose. From Russia the exit is Russian. Measured: every run came out on `104.28.x.x`, country
> RU, node DME; an independent geo database labels those addresses `Cloudflare WARP` and flags them as a
> proxy. No setting or entry point changes this — it is a Cloudflare limitation, not one of this
> program.

### Turning it on

1. **Start the bypass.** Registration is an ordinary HTTPS request to `api.cloudflareclient.com`, and
   that name is cut by SNI. The engine covers it unconditionally, whatever the scope — but only while it
   is running.
2. **WARP** tab → "Create device". The keys are made on your computer; only the public key leaves. Done
   once.
3. The **"Proxy on"** switch — there, or on Home in the card under Telegram.

The program does not take success on trust: it asks Cloudflare, **through the proxy itself**, where it
sees you, and only then reports that it works. The status line then shows the exit address and its
country.

### Where to point it

The proxy intercepts nothing and changes nothing about the system — only what you point at it goes
through it. The address is shown on the tab with a "Copy" button beside it. By default
`127.0.0.1:1080`, protocol **SOCKS5**.

| Where | How |
|---|---|
| Firefox | Settings → General → Network Settings → "Manual proxy configuration" → SOCKS host `127.0.0.1`, port `1080`, SOCKS v5 |
| Chrome, Edge | No proxy settings of their own; they take the system ones. Easier to use the launch flag `--proxy-server="socks5://127.0.0.1:1080"` or a switcher extension |
| Telegram | Settings → Data and Storage → Proxy → a SOCKS5 entry with the same address and port |

The proxy listens on `127.0.0.1` only — nothing on your local network can reach it. That is deliberate:
the client's own default is to listen on every address, which on a shared network would be an open
route to the internet under your account.

### What happens inside

WARP speaks two transports. The first is plain WireGuard. The second is **MASQUE**, Cloudflare's own
design: an IP tunnel over HTTP/3, falling back to HTTP/2 over ordinary TCP when QUIC does not get
through. On port 443 it is indistinguishable from normal web traffic.

The program uses MASQUE, and not as a matter of taste. WireGuard to WARP is cut **at the stream level**
on Russian networks: the handshake is let through and the data after it is dropped. A desync cannot mend
that — it can only disguise a connection's first packet, and there is nothing to hide a continuous
stream behind.

The way to connect is worked out automatically and remembered: HTTP/2 over TCP on 443 first, then 4443,
8443, 500, then QUIC with a capped initial packet. Measured on a Russian network: **with the bypass
running, 443 connects first try; with it stopped, 443 and 8443 are cut moments after connecting and only
4443 survives.** One more reason to leave the bypass on.

The entry-point addresses (`162.159.198.1` and `162.159.198.2`) are covered by the engine **always**,
whatever the bypass scope — through a separate profile driven by `lists\ipset-masque.txt`. That profile
alone turned out not to be enough: on a Russian network the connection only came up when MASQUE was
handled by the whole strategy rather than by one narrow profile aimed at it. So **while the proxy is on,
the bypass scope is widened to every site**, and it goes back to your setting when you switch it off.

> **Worth knowing before you flip the switch.** While WARP is on, the bypass applies to all TLS/QUIC
> except the exclusion list — a game or an application that is not excluded may start misbehaving
> because of it. The "Bypass all sites" switch itself does not change: the program does not rewrite
> your choice, it overrides it temporarily, and the card in Настройки (Settings) says so outright.

### Where things live

Everything sits in `%LOCALAPPDATA%\Zapret2UI\masque`: the unpacked `usque.exe` client and
`config.json` with the device key, its licence and token. The client runs as an ordinary child process
under your account with no elevation; a proxy left behind by a crash is cleared on the next start.

### If it does not work

| What you see | What to do |
|---|---|
| The device could not be created | Start the bypass and try again: `api.cloudflareclient.com` is cut by SNI. If it still fails, Cloudflare moves its client API version from time to time — check for a newer build |
| "No way of reaching Cloudflare worked" | Every transport and port was tried. Make sure the bypass is running and no other VPN is up |
| "Port is already in use by another program" | Something else is on `1080` — put a free port in Options |
| "Cloudflare answers but says the traffic is not going through WARP" | The request left outside the proxy; usually another tunnel is capturing the routes |
| The proxy is on but a site still says "not available in your region" | Expected: free WARP exits in Russia |
| The browser ignores the proxy | Check the address is entered as SOCKS5 and the port matches the one shown |
| Everything got slower | Expected: a detour through Cloudflare. Keep the proxy on only when you need it |

---

## 13. TCP timestamps

Some strategies (ts fooling: `tcp_ts`/`tcp_ts_up`, which ALT10 and ALT11 rest on) only work if the
outgoing TCP packet carries the timestamp option. Windows keeps it in the `allowed` state by default,
which **does not guarantee** a timestamp on client connections — and then ts fooling silently does
nothing, the fake does not die, and it corrupts the real connection.

So when the **bypass starts** Zapret2UI runs `netsh interface tcp set global timestamps=enabled` itself,
and restores the previous value when it **stops**. This happens automatically, under the administrator
rights it already has; there is nothing to change by hand. In the journal it shows as lines saying TCP
timestamps were enabled for the session and, later, restored to their original state.

---

## 14. Per-network memory

The program ties a working strategy it has found to a **fingerprint of the current network** (the
gateway/router address) and stores it in `settings.json` (`NetworkStrategies`). When you return to a
familiar network, it offers or enables that particular variant rather than the general default.
Everything is **local**: nothing is sent to the internet and no IP addresses are stored. On Home you see
it as the line "Стратегия для этой сети: …" (Strategy for this network).

---

## 15. Settings: the full list

The values are stored in `settings.json` (`AppSettings`).

| Setting | Key | Default | What it does |
|---|---|---|---|
| Simple mode | `SimpleMode` | `true` | Simple (one button) versus Advanced (tabs). |
| Interface language | `Language` | `ru` | Russian or English. The **RU \| EN** toggle on Home and in Settings; applied once the program restarts. |
| Active strategy | `ActivePresetName` | — | The name of the selected strategy. |
| Active host list | `ActiveHostlist` | — | The name of the active domain list. |
| Auto-update the engine | `AutoUpdateEngine` | `true` | Quietly update `winws2` from releases. |
| Start with Windows | `Autostart` | `false` | Start at logon (through `schtasks`, elevated). |
| …and start the bypass | `AutostartEngine` | `false` | Additionally start the bypass at launch. |
| Minimise to tray | `MinimizeToTray` | `true` | The close button hides to the tray instead of quitting. |
| Start in the tray | `StartMinimized` | `false` | Start already minimised. |
| Auto-repair | `AutoHeal` | `false` | Watch availability and re-select on failure. |
| Game filter | `GameFilter` | `false` | Widen capture to all high ports. |
| Bypass all sites | `BypassAllSites` | `false` | All sites versus the lists only. While the WARP proxy is on the bypass covers every site regardless of this value — the setting itself does not change. |
| Disable QUIC | `DisableQuic` | `false` | Drop QUIC → fall back to TCP/H2. |
| Cover the Telegram proxy | `TgProxyCoverage` | `false` | The engine additionally covers the built-in proxy's own 443 connections (for mobile DPI). |
| Verbose log | `DebugLog` | `false` | The **--debug** chip on the Journal tab: `winws2` records which connections the techniques were applied to, and why. Turning it on restarts the bypass. |
| Interface scale | `UiScale` | `1.0` | Extra UI zoom on top of the system DPI. The buttons give 100–200 %; a value written into the file is accepted up to 2.5. |
| Notifications | `NotificationsEnabled` | `true` | Show toasts in the corner. |
| Notification sound | `NotificationSound` | `true` | A quiet chime with the toast. |
| Telegram proxy port | `TgProxyPort` | `1443` | The proxy's local port. |
| Proxy secret | `TgProxySecret` | — | The persistent MTProto secret. |
| Start the proxy automatically | `TgProxyAutostart` | `false` | Start the proxy at launch. |
| WARP proxy port | `MasqueListenPort` | `1080` | Local port of the WARP SOCKS5 proxy (see [§12](#12-warp-and-changing-your-address)). |
| WARP transport | `MasqueHttp2`, `MasqueConnectPort` | `true`, `443` | Whatever connected last time. Worked out automatically; no need to change it by hand. |
| Per-network memory | `NetworkStrategies` | `{}` | Network → strategy (local only). |

Besides the above, the file keeps two housekeeping marks for the interface: whether the support
block is collapsed, and whether the first-run walkthrough has been shown. Neither is edited by hand.

The **"Добавить в исключения"** (Add to exclusions) button registers the program and the engine folder
with Windows Defender and the firewall in one click (the antivirus is a common cause of "it does not
work").

After each exclusion the program **re-reads the list from Defender and makes sure the entry really
appeared**. This is not a formality: with Tamper Protection on (enabled by default in Windows 11) or
under a third-party antivirus, the add command can report success while the exclusion is quietly dropped
— and the engine keeps disappearing. If the entry was not confirmed, the report shows a **✗** with an
explanation rather than a false tick. In that case add the `%LOCALAPPDATA%\Zapret2UI` folder to the
exclusions by hand through Windows Security → Virus and threat protection → Exclusions.

### 15.1. Backup

The **"Save to file"** and **"Restore from file"** buttons at the bottom of Settings. A single `.z2bak`
file holds your settings (`settings.json`), your strategies (`presets.json`) and the `lists\` folder.
The engine is **not** included — it weighs over a hundred megabytes and downloads itself anyway.

Useful in three cases: reinstalling Windows, moving a configured program to another computer, and as
insurance before a reset. Restoring replaces the current settings, strategies and lists with the
contents of the file, after which the program **restarts** — otherwise it would write the old values,
still held in memory, back over the restore.

### 15.2. Settings reset

The **"Reset settings"** button returns everything in the table above to its defaults: it removes
autostart (including the scheduled task), turns off auto-repair, the game filter, QUIC handling and the
proxy coverage, and restores the scale and the port. A running bypass and proxy are stopped.

**What is deliberately kept:** your strategies and host lists (they live in separate files and are not
touched at all), the interface language and the Simple/Advanced mode (resetting them would yank you out
of the current screen), the current strategy and list selection, the **Telegram-proxy secret** (so a
link already configured in the client keeps working) and the per-network memory.

### 15.3. Log files

Every engine start writes its own `logs\engine-*.log`. These used to pile up without limit; now the
**last 20** are kept when the program starts and the rest are deleted. The **"Clear logs"** button
removes them all at once. The `startup.log` and `fatal.log` service files are left alone — they do not
grow in number and are needed for diagnosing failures.

---

## 16. Updates

- **The engine** `winws2` is updated from the [bol-van/zapret2](https://github.com/bol-van/zapret2)
  releases (quietly, when `AutoUpdateEngine=true`). It is installed into `engine\`. If
  `api.github.com` is blocked, the program tries ordinary `github.com`.
- **Integrity verification is mandatory.** Every binary is checked against `sha256sum.txt` from the same
  release. If the release has no manifest, or it could not be parsed, **the installation is cancelled**
  with a message rather than proceeding on trust: the engine contains an unsigned kernel-mode driver and
  runs with administrator rights.
- **The engine is not re-downloaded on every launch.** The installed version tag is compared with the
  latest release; if they match, there is no download. The progress bar is shown **only during a real
  download**; an ordinary check just prints the line "Проверка обновлений…" (Checking for updates).
- **If the engine vanished, the program says so plainly.** When `winws2.exe` is gone while the version
  stamp remains, that is an antivirus quarantine — the program never deletes the file itself. A line
  about the false positive appears in the journal along with advice to add the exclusions, after which
  the engine is downloaded again. Without the exclusions it will disappear again after every download
  (see [§15](#15-settings-the-full-list)).
- **The application** is updated separately, from the
  [Asterlike/zapret2UI](https://github.com/Asterlike/zapret2UI/releases) releases. When a newer version
  exists, a notification appears with a link to the release.

---

## 17. Files and folders on disk

Everything lives under `%LOCALAPPDATA%\Zapret2UI\` (the program never writes to Program Files):

```
%LOCALAPPDATA%\Zapret2UI\
├─ engine\                     the engine and its data
│  ├─ winws2.exe               the engine itself (needs administrator)
│  ├─ mdig.exe, ip2net.exe     resolving domains → CIDR for ipset
│  ├─ cygwin1.dll, WinDivert*  runtime and driver
│  ├─ installed_version.txt    the installed engine version
│  ├─ lua\                     zapret-lib / zapret-antidpi / zapret-auto and others
│  ├─ files\                   blobs (fake\*.bin: TLS/QUIC/STUN client hellos)
│  └─ windivert.filter\        windivert_part.*.txt (STUN, QUIC initial)
├─ lists\                      host lists and ipset
│  ├─ youtube.txt, discord.txt built-in domain lists
│  ├─ exclude.txt              exclusions (banks / government services)
│  ├─ ipset-discord.txt        collected CIDR subnets (after "Build the IP list")
│  └─ ipset-masque.txt         WARP entry points; always covered by the engine (see §12)
├─ logs\                       engine-YYYYMMDD-HHMMSS.log (engine output)
├─ masque\                     the built-in WARP client (see §12)
│  ├─ usque.exe               the MASQUE client, started with no window
│  └─ config.json             the registered device: key, licence, token
├─ tmp\                        temporary downloads
├─ presets.json               your strategies
└─ settings.json              settings (see §15)
```

`settings.json` is written atomically (through a temp file), so a failure during writing cannot corrupt
your settings; if the file does get damaged, it is kept as `.bak` and the settings are reset to their
defaults.

---

## 18. Troubleshooting and error codes

**What to do when "it does not work":**

1. Go through the strategies by hand, from the top down (see [§5](#5-built-in-strategies)).
2. "Подобрать лучшую" (Select the best), then "Сгенерировать" (Generate) on Diagnostics.
3. Settings → **"Добавить в исключения"** (the antivirus is the most common cause).
4. Settings → disable **QUIC / HTTP-3**.
5. Check the **Журнал** (Journal) — the reason is there if the engine did not start.
6. IP-based blocking → a VPN (or the built-in proxy for Telegram).

**Common situations:**

- **The antivirus deletes `winws2.exe` / "engine not found".** Add the exclusions, then update or
  re-download the engine (Settings → engine update), or restart the program.
- **"Failed to start".** The reason is in the Journal. Most often: not started as administrator, the
  antivirus, or an engine that has not finished downloading.
- **It worked and then stopped.** The provider updated its filters → turn auto-repair on or re-select.
- **Discord: text works, voice is silent.** The `Discord — голос (QUIC-фейк)` strategy, or ALT10/ALT11.
  Voice runs over UDP — on the most stubborn networks the provider throttles it separately, and then a
  dedicated proxy or VPN just for voice may be needed for stable audio.
- **YouTube buffers.** Turn QUIC off.
- **All green but the site will not open.** A different blocking mechanism (ECH/IP) → another strategy,
  QUIC off, or a VPN.

**Engine exit codes (in the Journal):**

- `code 87` — invalid parameter. A common cause is an unknown verb or a missing library (for example
  `desync function 'circular' does not exist` when `zapret-auto.lua` was not loaded).
- `code -1` and other negatives — the engine was stopped or killed (normally during a regular stop or a
  strategy change).
- Other non-zero codes — read the text of the line above the code: that is the reason.

---

## 19. Building from source, and architecture

**Building** (requires the .NET 9 SDK):

```powershell
# run for development
dotnet run --project ZapretUI/ZapretUI.csproj

# release self-contained single-file exe
dotnet publish ZapretUI/ZapretUI.csproj -c Release -o publish

```

- .NET 9, `net9.0-windows`, x64, **WPF** plus WinForms (tray only), with **no third-party NuGet
  dependencies** in the shipped application.
- `-warnaserror` is the gate: the build has to come out at zero warnings, so dead code (an unused
  private field, say) breaks it.

**Architecture** (MVVM, no DI container):

- **`App` + `Startup/`** — starting up: command-line switches, the single-instance lock, the crash log.
  Two switches live here rather than in the UI: `--lang ru|en` overrides the saved language for one run
  (handy for a shortcut or for screenshots), and `--awaitpid <pid>` is internal — the new copy waits for
  the previous one to release the mutex, and only the restart after a language change uses it.
- **`Harness/`** — the headless developer modes (`--screenshot`, `--enginedump`, `--tgproxytest`,
  `--tgbridgetest`, `--masquetest`, `--masqueregion`). None of them appear in the UI: each renders or
  prints something and then shuts the process down itself.
- **`Views/`** — the windows and dialogs: the main window with its own chrome, the site check, the
  confirmation prompt, the environment check, the corner notifications.
- **`ViewModels/`** — one `MainViewModel` spread over partial files: `.Engine`, `.Strategies`,
  `.Diagnostics`, `.AutoSelect`, `.Telegram`, `.Masque`, `.Settings`, `.Updates`, `.Navigation`,
  `.Maintenance`.
- **`Services/`** — grouped by subject:

| Folder | What is inside |
|---|---|
| `Engine/` | `EngineService` (the `winws2` process, token expansion), `HostlistService`, `IpsetService`, `ProbeEngineRunner` |
| `Strategies/` | `PresetService`, `ComboStrategyCatalog`, `StrategyGeneratorService`, `AutoSelectService` |
| `Network/` | `NetProbe`, `DiagnosticsService`, `MonitorService` (the auto-repair watchdog), `TargetService`, `DohResolver`, `NetworkFingerprint` |
| `Telegram/` | `TelegramProxyService` plus the protocol: `TgProxyProto`, `AesCtr`, `MsgSplitter`, `CfProxyBalancer`, `TgWebSocket` |
| `Warp/` | `MasqueService`, `MasqueRuntime`, `WarpTrace`, `WarpResult` |
| `Platform/` | `AutostartService`, `ConflictScanService`, `ExclusionService`, `TcpTimestampsService` |
| `Infrastructure/` | `AppPaths`, `SettingsService`, `BackupService`, `LogMaintenance`, `UpdaterService` |

---

## 20. Links and credits

- [The documentation site](https://asterlike.github.io/zapret2UI/en/) — the same material with search,
  navigation and a line-by-line breakdown of the strategies.
- [bol-van/zapret2](https://github.com/bol-van/zapret2) — the `winws2` engine and the
  [official manual](https://github.com/bol-van/zapret2/blob/master/docs/manual.en.md).
- [Flowseal/zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube) — working
  strategies and [tg-ws-proxy](https://github.com/Flowseal/tg-ws-proxy).
- [RaccoonLaptop/ZapretUI](https://github.com/RaccoonLaptop/ZapretUI) — the project that started the idea
  of making a graphical shell.
- [hyperion-cs/dpi-checkers](https://github.com/hyperion-cs/dpi-checkers) — the volume-limit detection
  method used in the DPI check (Apache-2.0).
- [Asterlike/zapret2UI](https://github.com/Asterlike/zapret2UI) — this project.
- Chat and news — the [Zapret2UI Telegram channel](https://t.me/Zapret2UI).

The full list, with what specifically came from where, is on the site:
[Credits](https://asterlike.github.io/zapret2UI/en/credits.html).

The project is licensed under MIT (`LICENSE`). The `winws2` engine ships under its own licence.
