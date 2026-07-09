# Embedding decision record (M1 spike)

**Decision: `GlueEmbedder` (borderless owned top-level) is the default.**
`ReparentEmbedder` (SetParent) ships dormant behind `window.embedMode: "reparent"`.

## Why glue

MAME stays a top-level window — no `SetParent`, so no cross-process input-queue
attachment: a MAME stalled on a slow network ROM load can never freeze the
app's UI. The window is made chromeless (`WS_CAPTION|WS_THICKFRAME|WS_SYSMENU`
stripped), non-activatable (`WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW` — also removes
it from alt-tab), owned by the app window (`GWL_HWNDPARENT` — rides our
z-order, hides on minimize), and continuously positioned over the host region.

Launch uses raw `CreateProcessW` because `ProcessStartInfo` cannot express:
- The startup show command. Attract chunks launch **hidden** (`SW_HIDE`) → **no
  focus steal** on launch. `SW_SHOWNOACTIVATE` was not enough: it only governs
  MAME's first `ShowWindow`, not MAME's own `SetForegroundWindow` during video
  init, which steals focus wherever `ForegroundLockTimeout == 0`. A hidden window
  can't be foregrounded, so that grab no-ops. The locator finds the hidden window
  (it does **not** gate on `IsWindowVisible`), the embedder styles it non-activating
  + owned while hidden, then reveals it with `SW_SHOWNA` as its last act.
  `ForegroundGuard` (an `EVENT_SYSTEM_FOREGROUND` hook) is the backstop for any
  re-grab after reveal. A **play session** keeps `SW_SHOWNORMAL` (it should take focus).
- `CREATE_SUSPENDED` → the process joins a kill-on-close job object before its
  first instruction; `CREATE_NO_WINDOW` stops console-subsystem MAME builds
  (0.147) popping a console from a GUI host.

## Spike results (2026-06-11, automated: `Attractor.exe --spike <mame> <game|-> <log>`)

| Check | MAME 0.147 (galaga, SMB share) | MAME 0.288 (system UI, local) |
|---|---|---|
| Window found | 1.0 s | 11.0 s (first-run plugin cache; subsequent runs fast) |
| Native client size | 224×299 | 640×480 |
| Embed rect == aspect-fit rect | exact (±0 px) | exact (±0 px) |
| Focus stays with host on launch | PASS | PASS |
| Glue follows app-window move | exact (±0 px) | exact (±0 px) |
| Kill → process exits | PASS | PASS |
| Hard-kill host → MAME dies (job object) | — | PASS (`Attractor.Smoke jobtest`) |

Window class is `"MAME"` on both generations (verified against 0.147 binary
and mamedev master source); locator matches pid + class + a non-zero client rect
(it no longer requires the window to be visible, since attract chunks are found
while still hidden — see the launch-hidden note above).

## Outstanding (human-eyes checks, fold into M3 verification)

- Alt-tab shows exactly one entry (toolwindow flag should guarantee).
- Click into game region cannot focus MAME / Esc cannot quit it (NOACTIVATE).
- Minimize/restore and monitor-to-monitor drag behavior.
- 150 % DPI monitor placement (PMv2 manifest + physical-pixel math).
- WPF modal dialogs vs owned-window z-order.
