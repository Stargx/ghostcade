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
- `SW_SHOWNOACTIVATE` startup show command → **no focus steal** on launch
  (MAME honors the STARTUPINFO show field — proven by SW_HIDE suppressing its
  window entirely, which is also why `-WindowStyle Hidden`-style launches must
  never be used).
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
and mamedev master source); locator matches pid + visible + class.

## Outstanding (human-eyes checks, fold into M3 verification)

- Alt-tab shows exactly one entry (toolwindow flag should guarantee).
- Click into game region cannot focus MAME / Esc cannot quit it (NOACTIVATE).
- Minimize/restore and monitor-to-monitor drag behavior.
- 150 % DPI monitor placement (PMv2 manifest + physical-pixel math).
- WPF modal dialogs vs owned-window z-order.
