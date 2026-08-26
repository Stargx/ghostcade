# config.json reference

Ghostcade stores its settings in `data\config.json`, in a portable `data\`
folder next to `Ghostcade.exe`. (If the install location is read-only, it falls
back to `%AppData%\Attractor`.) Open it via **File → Open config folder**.

The file is written on first run and tolerates `// comments` and trailing
commas. Missing keys are filled with defaults; a file from a *newer* version of
Ghostcade is refused rather than overwritten.

```jsonc
{
  "version": 1,

  "mame": {
    // Path to your mame.exe (any version 0.147+).
    "exePath": "C:\\mame\\mame.exe",
    // Extra args appended to every launch — your escape hatch, e.g.
    // ["-rompath", "D:\\roms"] or ["-video", "gdi"].
    "extraArgs": [],
    // dB attenuation passed to MAME (-volume) at launch. 0 = full, negative = quieter.
    "volumeAttenuation": 0,
    // Live volume of MAME's audio session via the Windows per-app mixer, 0.0–1.0.
    // Independent of the system master volume (and of volumeAttenuation). Adjust at
    // runtime with the Volume Up/Down hotkeys; re-applied to each ~5-min chunk and
    // saved back here.
    "volume": 1.0,
    // Launch dialect. The wizard writes "seconds" (modern MAME, -seconds_to_run)
    // so normal launches skip the version probe; "auto" (or any other value)
    // re-probes the exe at startup and gates unsupported builds. You shouldn't
    // need to touch this.
    "timingMode": "seconds",
    // MAME minor version the wizard detected (e.g. 288), for diagnostics only.
    "detectedVersionMinor": null,
    // Path to a catver.ini for the genre filter (the fan-maintained category file
    // from progettosnaps.net — not shipped with Ghostcade). Relative paths resolve
    // against the MAME folder. null = look for catver.ini next to mame.exe, then
    // in its folders\ subfolder. Without one, the Genre menu shows a disabled explainer.
    "catverPath": null
  },

  "art": {
    // Folders searched for <shortname>.png. Relative paths resolve against
    // the MAME directory. First match wins.
    "marqueeDirs": ["marquees"],
    "snapDirs": ["snap"]
  },

  "rotation": {
    // Seconds per game. Runs as <=299s chunks (see the FAQ on the ~5-min blink).
    // Also settable live from the Time out menu (1/2/3/5/10/15 min); a change there
    // is saved here and applies from the next game.
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
    // Take the controls: swap the attract demo for a real, focused, uncapped
    // MAME session; the rotation resumes when you quit MAME.
    "play": "Ctrl+Alt+P",
    "mute": "Ctrl+Alt+M",
    "volumeUp": "Ctrl+Alt+OemPlus",
    "volumeDown": "Ctrl+Alt+OemMinus"
  },

  "window": {
    // "glue" (default) keeps the MAME window top-level and owned by the app.
    // "reparent" is a fallback if a MAME build misbehaves when embedded.
    "embedMode": "glue"
  },

  "filter": {
    // Restrict the rotation (File → Filter). The categories combine with AND;
    // empty lists + favouritesOnly false = no filter. Also settable live from
    // the menu, which saves your choice back here.
    // Decade start years to include (1980 = the 1980s); empty = all years.
    "decades": [],
    // Manufacturer names to include, exactly as MAME reports them; empty = all.
    "manufacturers": [],
    // Genre tags to include (catver.ini categories or subcategories, e.g. "Shooter"
    // or "2.5D" — a "Genre / Subgenre" line contributes both); empty = all. A game
    // matches if any of its own tags is listed here. Needs a catver.ini (see
    // mame.catverPath above); games missing from it only rotate while this list is empty.
    "genres": [],
    // Play only games you've favourited (favorites.txt / the Favourite button).
    "favoritesOnly": false
  },

  "sound": {
    // The cabinet's own UI sound effects: a startup jingle, a coin on Skip, and
    // a click on the other buttons. Independent of the Mute button (which only
    // mutes the emulated game). Set false for silent operation.
    "enabled": true,
    // SFX volume, 0.0 (silent) to 1.0 (full).
    "volume": 0.8
  }
}
```

## Hotkey strings

`Modifier+Modifier+Key`, e.g. `Ctrl+Alt+Right`, `Shift+F9`, `Win+Pause`.
Modifiers: `Ctrl`, `Alt`, `Shift`, `Win`. The key is any single key name
(letters, digits, `Left`/`Right`/`Up`/`Down`, `F1`–`F12`, etc.). `OemPlus` and
`OemMinus` are the `=`/`+` and `-`/`_` keys — the defaults for Volume Up/Down. If a
chord is already taken by another app, Ghostcade notes it in the status line and
carries on without that one.

The Volume Up/Down hotkeys adjust `mame.volume` (above) in 10% steps — MAME's own
mixer slider, separate from the Windows system volume.

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
