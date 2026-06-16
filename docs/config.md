# config.json reference

Attractor stores its settings in `data\config.json`, in a portable `data\`
folder next to `Attractor.exe`. (If the install location is read-only, it falls
back to `%AppData%\Attractor`.) Open it via **File → Open config folder**.

The file is written on first run and tolerates `// comments` and trailing
commas. Missing keys are filled with defaults; a file from a *newer* version of
Attractor is refused rather than overwritten.

```jsonc
{
  "version": 1,

  "mame": {
    // Path to your mame.exe (any version 0.147+).
    "exePath": "C:\\mame\\mame.exe",
    // Extra args appended to every launch — your escape hatch, e.g.
    // ["-rompath", "D:\\roms"] or ["-video", "gdi"].
    "extraArgs": [],
    // dB attenuation passed to MAME (-volume). 0 = full, negative = quieter.
    "volumeAttenuation": 0
  },

  "art": {
    // Folders searched for <shortname>.png. Relative paths resolve against
    // the MAME directory. First match wins.
    "marqueeDirs": ["marquees"],
    "snapDirs": ["snap"]
  },

  "rotation": {
    // Seconds per game. Runs as <=299s chunks (see the FAQ on the ~5-min blink).
    "dwellSeconds": 300,
    "order": "shuffle"
  },

  "hotkeys": {
    "enabled": true,
    "previous": "Ctrl+Alt+Left",
    "skip": "Ctrl+Alt+Right",
    "hold": "Ctrl+Alt+Down",
    "ban": "Ctrl+Alt+B",
    "favorite": "Ctrl+Alt+F",
    "mute": "Ctrl+Alt+M"
  },

  "window": {
    // "glue" (default) keeps the MAME window top-level and owned by the app.
    // "reparent" is a fallback if a MAME build misbehaves when embedded.
    "embedMode": "glue"
  }
}
```

## Hotkey strings

`Modifier+Modifier+Key`, e.g. `Ctrl+Alt+Right`, `Shift+F9`, `Win+Pause`.
Modifiers: `Ctrl`, `Alt`, `Shift`, `Win`. The key is any single key name
(letters, digits, `Left`/`Right`/`Up`/`Down`, `F1`–`F12`, etc.). If a chord is
already taken by another app, Attractor notes it in the status line and carries
on without that one.

## Other files in `data\`

| File | What it is |
|---|---|
| `machines-cache.json` | Cached MAME machine list (rebuilt on Rescan / MAME change) |
| `verify-cache.json` | Cached `-verifyroms` results |
| `rotation-state.json` | Where the shuffle cycle is up to (so it resumes) |
| `banned.txt` | One short name per line — hand-editable |
| `favorites.txt` | Space-aligned columns `shortname` · `title` · `rom folder` (so you can find and play favourites later); only the first column is read back, and plain one-name-per-line entries still work |
| `placement.json` | Last window position/size |
| `logs\attractor-YYYYMMDD.log` | Daily rolling log (7 days kept) |

Deleting any cache file just triggers a rebuild on next launch.
