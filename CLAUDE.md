# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What Attractor is

Attractor is an **ambient arcade player**: it cycles a user's *existing* MAME
collection through each game's built-in **attract/demo loop** (no coins, no
play), one game every few minutes, embedded inside a WPF "neon cabinet" on the
desktop while they work. It is deliberately **not** a front-end or launcher —
the opposite idea: watch, don't play.

It drives the user's own MAME binary as an external process and ships **no ROMs,
no MAME, and no game artwork**. Almost everything interesting is about driving an
external emulator reliably (versions 0.147→current) without stealing focus or
orphaning processes.

## Commands

.NET 10 SDK required (`net10.0` / `net10.0-windows`, WPF). There is no
`global.json`. The solution file is `Attractor.slnx` (XML solution format) — there
is no `.sln`.

```
dotnet build Attractor.slnx -c Release
dotnet test  Attractor.slnx -c Release          # all tests (xUnit v2)
dotnet run   --project src/Attractor.App          # launch the WPF app
```

Run a single test (the test project is the only one with tests):

```
dotnet test tests/Attractor.Core.Tests --filter "FullyQualifiedName=Attractor.Core.Tests.RotationEngineTests.Skip_kills_the_current_chunk_and_advances"
dotnet test tests/Attractor.Core.Tests --filter "Name~Hold"     # substring match
```

App CLI flags (see `App.xaml.cs`): `--setup` (force the wizard), `--rescan`
(rebuild catalog on launch), `--spike <mame.exe> <game|-> <log>` (headless embed
regression harness, exit 0=pass/1=fail/2=exception).

Headless harness against a *real* MAME (reference impl, no UI):

```
dotnet run --project tools/Attractor.Smoke -- scan    <mame.exe>
dotnet run --project tools/Attractor.Smoke -- rotate  <mame.exe> [count] [dwell]
dotnet run --project tools/Attractor.Smoke -- jobtest <mame.exe>   # proves kill-on-close
```

**Linting / style is enforced by the build, not a separate tool:**
`Directory.Build.props` sets `TreatWarningsAsErrors=true` and `Nullable=enable` —
any warning (including nullable and unused-using) fails the build. `.editorconfig`
mandates file-scoped namespaces (`csharp_style_namespace_declarations =
file_scoped:warning`), 4-space indent, CRLF, `system`-directives-first.

## Solution layout

- **`src/Attractor.Core`** — all engine + MAME interaction logic. **No UI, no
  WPF dependency.** This is where most work happens and where all the tests point.
- **`src/Attractor.App`** — the WPF presentation layer (CommunityToolkit.Mvvm).
  Composes Core services at startup; output assembly is `Attractor.exe`.
- **`tests/Attractor.Core.Tests`** — xUnit. Tests Core *only*, through fakes.
- **`tools/Attractor.Smoke`** — console harness driving real Core against real MAME.
- **`prototype/`** — the original PowerShell proof-of-concept (`attract.ps1`),
  preserved for reference; not part of the build.

## The one constraint that explains the whole design: the <300s chunk rule

MAME auto-suppresses its startup/disclaimer screens — **including the blocking
"this game has problems, press a key" warning** — only while a session runs under
**300 emulated seconds**. With nobody at the keyboard, a longer run would freeze on
that warning forever. So Attractor never runs a game in one long session: a dwell
is split into **≤299s chunks** (`ChunkPlanner`, `MaxChunkSeconds = 299`), and each
chunk is a **fresh MAME launch of the same game**. This is the ~5-minute "blink"
the README's FAQ describes, and it ripples through `ChunkPlanner`,
`RotationEngine` (the chunk loop, including Hold), and `MameLaunchSpec`
(`-seconds_to_run` / `-frames_to_run`). Do not "optimize" this into a single long
launch.

## End-to-end pipeline (read this before touching rotation/embedding)

```
CatalogBuilder ─▶ GameDatabase ─▶ RotationEngine ─▶ MameLauncher ─▶ MameWindowLocator ─▶ IMameWindowEmbedder
   (Core)          (Core)           (Core, async      (Core, raw      (Core, EnumWindows)   (Core: Glue|Reparent)
                                     state machine)     CreateProcessW)                              │
                                          │                                                          ▼
                                          └────── events on worker thread ──▶ MainViewModel (App, dispatcher-marshals) ─▶ MainWindow
```

1. **Catalog build** (`Catalog/CatalogBuilder.BuildAsync`, the single entry
   point, called from `MainViewModel` startup, `SetupWindow`, and Smoke). Runs
   `mame -listxml` (streamed via a forward-only `XmlReader` — the XML is 100+ MB,
   never a DOM) and `mame -verifyroms`, then `GameDatabase.Assemble` **joins**
   them. **A game is in the rotation pool iff** it is runnable, not BIOS, not a
   device, not a `Preliminary` driver, **and present in the verify map** — there is
   no explicit "ROM exists" check; missing/bad ROMs are dropped purely by absence
   from the verify join.
2. **Rotation** (`Rotation/RotationEngine`): one async `RunLoopAsync` owns the
   MAME child process. The host drives it only through a thread-safe
   `Channel<EngineCommand>` (Skip/Previous/Hold/Ban/Nudge/Rebag/Stop) and reacts to
   events. `ShuffleBag` gives no-repeats-until-exhausted (real Fisher–Yates, not
   `OrderBy(random)`); `PlayHistory` backs Prev/Skip; `FaultPolicy` isolates
   broken ROMs/shares.
3. **Launch + locate + embed** (`Mame/` + `Windowing/`): each chunk launches a
   fresh MAME via raw `CreateProcessW`, finds its `HWND` (window class `"MAME"`,
   stable across versions), then the embedder positions it in the cabinet.
4. **UI** (`Attractor.App`): `MainViewModel` is the single VM; engine events are
   marshalled onto the WPF dispatcher.

### Threading model (easy to get wrong)

`RotationEngine` raises **all** events (`GameChanged`/`WindowReady`/
`StateChanged`/`GameFaulted`/`HoldChanged`) **on the worker thread**.
`MainViewModel` marshals every one onto the UI thread with
`_dispatcher.BeginInvoke`. Public engine methods (and thus hotkeys) are
thread-safe because they only write to the command `Channel`. Don't touch WPF
objects from engine callbacks, and don't call engine internals from the UI.

### Two terminal-ish rotation states are deliberately distinct

`RotationState.Empty` = the pool is exhausted/over-filtered (recoverable — the
loop idles until a `Nudge` command wakes it, e.g. after the user widens the
filter or unbans). `RotationState.Faulted` = MAME/share is dying (5 faults in a
2-minute window → engine stops). Empty is **not** a fault.

## The portable `data\` folder (config + persisted state)

All runtime files live in a **`data\` folder next to the exe** (resolved by
`Configuration/AppPaths` via a `.writetest` probe), falling back to
`%APPDATA%\Attractor` only if that location is read-only (e.g. Program Files), with
a one-time migration from the legacy `%APPDATA%` location. Files: `config.json`
(JSONC — tolerates `//` comments and trailing commas; camelCase on disk),
`machines-cache.json`, `verify-cache.json`, `rotation-state.json` (the shuffle
bag, so a multi-hour cycle resumes across restarts), `banned.txt`,
`favorites.txt`, `placement.json`, `logs\attractor-YYYYMMDD.log`. Full schema in
`docs/config.md`. **Every** write that a reader/crash could interleave with goes
through `Configuration/AtomicFile` (write `.tmp`, then atomic `File.Move` rename).

## Subsystem notes — the non-obvious invariants

**Catalog caching.** Caches are invalidated by **`mame.exe` identity only**
(`MameFingerprint` = path + length + mtime), **not** by ROM content. Adding/removing
ROMs without changing the exe will *not* refresh the verify cache — use File →
Setup Wizard / `--rescan` (`forceRescan=true` bypasses both caches). A corrupt
cache is treated as "no cache" and silently rebuilds. `banned.txt` and
`favorites.txt` share one `FileTagStore` format (one shortname per line,
hand-editable); only the **first whitespace-delimited token** is read back, so
`favorites.txt` can carry extra space-padded columns (title, ROM folder) written by
`MainViewModel.EnrichFavoritesAsync`.

**MAME launch & version dialects.** "Attract mode, no coins, no focus" is **not**
MAME flags — there is no coin/attract option. It falls out of (a) never inserting a
coin, (b) the <300s session bound keeping MAME in its built-in demo loop with
warnings suppressed, and (c) `SW_SHOWNOACTIVATE` so the window appears without
grabbing focus. All version differences are isolated in `MameCapabilities` /
`MameTimingMode`: **≥0.147 uses `-seconds_to_run`; <0.147 uses `-frames_to_run`
(seconds×refresh) plus `-skip_disclaimer`** — modern MAME treats `-skip_disclaimer`
as a *fatal* unknown option, so it is emitted *only* in frames mode. An
unparseable `-help` banner defaults to the modern dialect (fail-open). The rest of
the app never branches on version.

**Why raw `CreateProcessW` (not `Process.Start`).** `ProcessStartInfo` cannot set
the startup show-command (`SW_SHOWNOACTIVATE`, needed to avoid stealing focus on
every rotation) and cannot start `CREATE_SUSPENDED` (needed so the child joins the
kill-on-close `JobObject` *before executing one instruction* — no orphan window).
Order is a hard invariant: **assign-to-job while suspended, then `ResumeThread`.**
`CREATE_NO_WINDOW` is added so console-subsystem MAME builds (0.147) don't pop a
console.

**Window embedding — Glue (default) vs Reparent (dormant).** `GlueEmbedder`
**never calls `SetParent`**: it strips the chrome, sets
`WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW`, and makes the app the **owner** via
`GWL_HWNDPARENT` (which sets the *owner*, not a child parent, despite the name). The
MAME window stays a separate top-level window riding the owner's z-order — so a MAME
stalled on a slow network ROM load **can never freeze the app's input queue** (the
cross-process `SetParent` attach hazard). `ReparentEmbedder` (real `WS_CHILD` +
`SetParent`) is shipped dormant behind `config.window.embedMode == "reparent"` as an
escape hatch. Rationale is recorded in `docs/embedding.md`. All `Windowing/`
coordinates are **physical screen pixels, never WPF DIPs**; the host rect is captured
corner-to-corner via `PointToScreen` to fold in Per-Monitor-v2 DPI *and* `Viewbox`
scaling. Aspect ratio comes from the **catalog** (`IsVertical` → 3:4 else 4:3), not
from measuring the freshly-spawned window (unreliable mid-load). Per-process mute
**and volume** (`ProcessAudio`, WASAPI per-app session — independent of the system
master volume; `config.mame.volume` 0–1, Volume Up/Down hotkeys) are re-applied
together with a retry loop on **every** `WindowReady` because each chunk is a new
process with a new audio session.

**App / MVVM wiring.** `App.xaml` has **no `StartupUri` and no `ShutdownMode`** — the
default `OnLastWindowClose` is load-bearing: the Setup-Wizard re-run is a full
teardown-and-relaunch (`RelaunchFromConfig` opens the new `MainWindow` *before* the
old one finishes closing so the window count never hits zero). Single-instance is a
named `Mutex`. Hotkeys are **global** (Win32 `RegisterHotKey` on the window's
`HwndSource`) so Skip/Ban work without leaving your editor — `HotkeyManager` must be
constructed **after `SourceInitialized`** (no `HWND` before then). Full vs slim
layout is height-driven with hysteresis (drop <1080px, restore >1120px). All
persisted-state writes (`SaveBagState`, filter, favorites) are best-effort and
swallow `IOException`/`UnauthorizedAccessException` — the data folder may be a
locked/offline network share. `SpikeWindow` is a leftover M2 embedding harness
("replaced by the real layout in M3"); the `--spike` mode reuses it.

## Testing conventions

xUnit v2 (the `.csproj` adds a global `<Using Include="Xunit"/>`, so test files
don't write `using Xunit;`). Core exposes three seams the tests fake:
`IMameLauncher`/`IMameProcess`/`IGameWindowFinder` (plus `TimeProvider`) — see
`FakeMame.cs`. The single scenario lever is `FakeLauncher.OnLaunch` (default:
auto-exit 0 after 80ms = a chunk completing; empty lambda = runs until killed;
`proc.Exit(-5)` = crash-at-launch). **Async tests never block on a flag** — they
poll via `Wait.ForAsync(cond, timeoutMs, label)` (throws `TimeoutException` instead
of hanging), and the `RotationEngineTests` `Harness` arms a 30s CTS backstop;
shared lists are mutated under `lock` because event callbacks fire on engine
threads. Determinism comes from `new Random(42)` and `FastOptions` (1s dwell,
watchdog disabled).
