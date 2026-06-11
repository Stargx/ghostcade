# overlay.ps1 - floating marquee/title overlay for the attract rotation.
# A borderless, always-on-top, CLICK-THROUGH window that shows the current
# game's marquee art + full title. It never steals focus or the mouse, so it
# just floats over the windowed MAME game. Driven by current.txt, which
# attract.ps1 updates each cycle.
#
# Normally you don't run this directly - attract.ps1 launches it. To test it
# standalone:  powershell -STA -File overlay.ps1 -Edge Top
#
# Close it by closing attract.ps1 (Ctrl+C) or killing this window's process.

param(
    [string]$MameDir  = 'C:\mame',
    [string]$StateDir = $PSScriptRoot,
    [ValidateSet('Top','Bottom','Left','Right')]
    [string]$Edge = 'Top',
    [int]$Monitor = 0,          # which screen to anchor to (0 = primary)
    [int]$PanelW = 560,
    [int]$PanelH = 200
)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# Win32 helpers to make the window click-through and non-activating ----------
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class NativeOverlay {
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    public const int GWL_EXSTYLE      = -20;
    public const int WS_EX_LAYERED     = 0x00080000;
    public const int WS_EX_TRANSPARENT = 0x00000020; // mouse passes through
    public const int WS_EX_NOACTIVATE  = 0x08000000; // never takes focus
    public const int WS_EX_TOOLWINDOW  = 0x00000080; // no taskbar/alt-tab
}
"@

$marqueeDir = Join-Path $MameDir 'marquees'
$snapDir    = Join-Path $MameDir 'snap'
$currentTxt = Join-Path $StateDir 'current.txt'
$titlesTxt  = Join-Path $StateDir 'titles.txt'

# Load short-name -> full title map (optional; falls back to short name) -----
$titles = @{}
if (Test-Path $titlesTxt) {
    foreach ($line in [System.IO.File]::ReadAllLines($titlesTxt)) {
        $i = $line.IndexOf("`t")
        if ($i -gt 0) { $titles[$line.Substring(0,$i)] = $line.Substring($i+1) }
    }
}

# Build the window -----------------------------------------------------------
$form               = New-Object System.Windows.Forms.Form
$form.FormBorderStyle = 'None'
$form.TopMost        = $true
$form.ShowInTaskbar  = $false
$form.StartPosition  = 'Manual'
$form.BackColor      = [System.Drawing.Color]::FromArgb(12,12,16)
$form.Opacity        = 0.92
$form.Size           = New-Object System.Drawing.Size($PanelW, $PanelH)

# Anchor to the chosen monitor + edge
$screens = [System.Windows.Forms.Screen]::AllScreens
if ($Monitor -lt 0 -or $Monitor -ge $screens.Count) { $Monitor = 0 }
$wa = $screens[$Monitor].WorkingArea
$margin = 24
switch ($Edge) {
    'Top'    { $x = $wa.X + [int](($wa.Width  - $PanelW)/2); $y = $wa.Y + $margin }
    'Bottom' { $x = $wa.X + [int](($wa.Width  - $PanelW)/2); $y = $wa.Bottom - $PanelH - $margin }
    'Left'   { $x = $wa.X + $margin;                          $y = $wa.Y + [int](($wa.Height - $PanelH)/2) }
    'Right'  { $x = $wa.Right - $PanelW - $margin;            $y = $wa.Y + [int](($wa.Height - $PanelH)/2) }
}
$form.Location = New-Object System.Drawing.Point($x, $y)

$pic            = New-Object System.Windows.Forms.PictureBox
$pic.SizeMode   = 'Zoom'
$pic.Dock       = 'Fill'
$pic.BackColor  = [System.Drawing.Color]::Transparent
$form.Controls.Add($pic)

$label              = New-Object System.Windows.Forms.Label
$label.Dock         = 'Bottom'
$label.Height       = 34
$label.TextAlign    = 'MiddleCenter'
$label.ForeColor    = [System.Drawing.Color]::Gainsboro
$label.BackColor    = [System.Drawing.Color]::FromArgb(20,20,26)
$label.Font         = New-Object System.Drawing.Font('Segoe UI', 11, [System.Drawing.FontStyle]::Bold)
$form.Controls.Add($label)

# Apply click-through / no-activate once the handle exists
$form.Add_Shown({
    $h  = $form.Handle
    $ex = [NativeOverlay]::GetWindowLong($h, [NativeOverlay]::GWL_EXSTYLE)
    $ex = $ex -bor [NativeOverlay]::WS_EX_LAYERED -bor [NativeOverlay]::WS_EX_TRANSPARENT `
              -bor [NativeOverlay]::WS_EX_NOACTIVATE -bor [NativeOverlay]::WS_EX_TOOLWINDOW
    [void][NativeOverlay]::SetWindowLong($h, [NativeOverlay]::GWL_EXSTYLE, $ex)
})

# Helper: load an image without locking the file on the share
function Load-Image([string]$path) {
    if (-not (Test-Path $path)) { return $null }
    try {
        $bytes = [System.IO.File]::ReadAllBytes($path)
        $ms = New-Object System.IO.MemoryStream(,$bytes)
        return [System.Drawing.Image]::FromStream($ms)
    } catch { return $null }
}

$script:lastGame = $null

# Poll current.txt and update the panel when the game changes ----------------
$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 500
$timer.Add_Tick({
    if (-not (Test-Path $currentTxt)) { return }
    $game = $null
    try { $game = ([System.IO.File]::ReadAllText($currentTxt)).Trim() } catch { return }
    if (-not $game -or $game -eq $script:lastGame) { return }
    $script:lastGame = $game

    # Title text (full name if known, else short name)
    $title = $game
    if ($titles.ContainsKey($game)) { $title = $titles[$game] }
    $label.Text = $title

    # Marquee art, falling back to a title snapshot, then nothing
    $img = Load-Image (Join-Path $marqueeDir "$game.png")
    if (-not $img) { $img = Load-Image (Join-Path $snapDir "$game.png") }
    $old = $pic.Image
    $pic.Image = $img
    if ($old) { $old.Dispose() }
})
$timer.Start()

[System.Windows.Forms.Application]::Run($form)
