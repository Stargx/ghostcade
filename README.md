# Attractor

**Ambient arcade player.** Attractor cycles your MAME collection through each
game's own *attract mode* — the demo loop a cabinet plays when nobody's
playing — one game every few minutes, embedded in a neon cabinet on your
desktop, while you work.

It is **not** a front-end or a launcher. You don't pick games and you don't
play them — Attractor just *runs* them in attract mode and rotates, like having
a wall of arcade cabinets idling in the corner of the room.

![Attractor — full layout](docs/screenshots/full.png)

On shorter screens it switches to a slim layout that drops the side panel and
scales the chrome down:

![Attractor — slim layout](docs/screenshots/slim.png)

---

## What it does

- Picks a game, launches it in MAME with no coins inserted, and lets the
  attract/demo loop play for a few minutes, then moves on to the next.
- **Shuffle with no repeats** until your whole collection has had a turn —
  and it remembers where it's up to across restarts.
- Embeds the real MAME window inside a neon arcade cabinet, with the game's
  **marquee art** up top and (on bigger screens) a side panel showing the
  title, year, manufacturer and a snippet of history.
- Stays out of your way: it never steals keyboard focus, and you can mute just
  the emulator without muting anything else.

## What it is *not*

- Not a front-end / game launcher (it doesn't replace LaunchBox, Attract-Mode,
  etc. — it's the opposite idea: watch, don't play).
- Not an emulator. It drives **your** MAME.
- It ships **no ROMs, no MAME, and no game artwork**, and never downloads any.

## Requirements

- Windows 10 / 11 (x64)
- **Your own MAME** (anything from v0.147 to current) and **your own ROMs**,
  set up and working in MAME already.
- The .NET 10 Desktop Runtime — the installer offers to fetch it if it's
  missing, or grab the self-contained build which needs nothing extra.

## Install

**Installer** (recommended): download `Attractor-x.y.z-Setup.exe` from the
[latest release](https://github.com/Stargx/attractor/releases), run it (it's a
per-user install, no admin needed), and it'll set up the runtime if required.

**Portable**: download `Attractor-x.y.z-portable.zip`, unzip anywhere writable,
run `Attractor.exe`. Self-contained — no runtime install needed.

> Unsigned builds will show a Windows SmartScreen prompt the first time
> ("More info → Run anyway"). Code signing is on the post-v1 list.

## First run

A short wizard walks you through it:

1. **Point it at your `mame.exe`** (any version 0.147+). It does a quick check
   that it's really MAME.
2. **Confirm your artwork folders** — it auto-finds `marquees\` and `snap\`
   next to MAME. Skip if you don't have them (you'll get title text instead).
3. **Scan** — a one-time pass that reads MAME's machine list and verifies which
   ROM sets you actually have. Over a network share this can take a few
   minutes; it's cached, so every later start is instant.

That's it — rotation starts automatically.

## Controls

On-screen buttons, plus global hotkeys that work even when another app has
focus:

| Button | Hotkey | Action |
|---|---|---|
| ⏮ Prev | `Ctrl+Alt+Left` | Go back to the previous game |
| ⏸ Hold | `Ctrl+Alt+Down` | Pause rotation — current game keeps demoing |
| ⏭ Skip | `Ctrl+Alt+Right` | Jump to the next game |
| 🚫 Ban | `Ctrl+Alt+B` | Never show this game again |
| ⭐ Favourite | `Ctrl+Alt+F` | Tag as a favourite |
| 🔊 Mute | `Ctrl+Alt+M` | Mute just MAME (not the rest of your system) |

Hotkeys are remappable in `config.json`.

## Configuration

Everything lives in a portable `data\` folder **next to the app** (no hunting
through `%AppData%`). Use **File → Open config folder** to find it. Settings are
in `config.json`; see [docs/config.md](docs/config.md) for the full reference.
Two hand-editable lists live there too:

- `banned.txt` — one short name per line; remove a line to un-ban.
- `favorites.txt` — your tagged favourites.

`File → Rescan ROMs` rebuilds the catalog after you add games.

## FAQ

**Why does the game briefly restart every ~5 minutes?**
MAME only suppresses its "this game has problems" warning screens when a session
runs under 300 seconds, so Attractor runs each game in ~5-minute chunks. Longer
dwell times relaunch the same game between chunks — a brief blink. It's the
price of a clean, warning-free picture.

**Why did a game I have never appear / get skipped?**
A full no-repeat cycle of a big collection takes many hours of running, so over
a single session you'll only see a slice — but it remembers its place, so given
time it works through everything. Games whose driver MAME flags as
non-working, plus BIOS/device sets, are filtered out.

**It opened tiny / on the wrong layout.**
It remembers its last window size. Resize it once and it sticks. Below 1080px
tall it uses the slim layout on purpose.

**Does it capture my keyboard / mouse?**
No. The emulator is launched without grabbing focus or the mouse, so you can
keep working with it running.

## Build from source

```
git clone https://github.com/Stargx/attractor
cd attractor
dotnet build Attractor.slnx -c Release
dotnet test Attractor.slnx -c Release
```

Solution layout: `Attractor.Core` (engine + MAME interaction, no UI),
`Attractor.App` (WPF), `Attractor.Core.Tests` (xunit), `Attractor.Smoke`
(headless harness). The original PowerShell prototype is preserved under
`prototype/`.

## Credits & licence

Attractor is MIT licensed (see [LICENSE](LICENSE)). Bundled fonts —
[Press Start 2P](https://github.com/google/fonts/tree/main/ofl/pressstart2p)
and [DSEG](https://github.com/keshikan/DSEG) — are under the SIL Open Font
License (see `src/Attractor.App/assets/fonts`).

MAME is a trademark of its respective owners. Attractor is not affiliated with
or endorsed by the MAME team, and includes no MAME code, ROMs, or game artwork.
