# attract.ps1 - MAME attract-mode rotation
# Cycles through your collection: each game runs in attract mode for a few
# minutes, then MAME exits and the next game launches. Random order, no repeats
# until the whole collection has cycled once.
#
# Usage:
#   .\attract.ps1                 # 5 min/game, windowed at 4x native, marquee overlay
#   .\attract.ps1 -Seconds 420    # 7 min per game
#   .\attract.ps1 -Scale 3        # game window at 3x native size (default 4x)
#   .\attract.ps1 -Fullscreen     # full screen instead of windowed
#   .\attract.ps1 -Edge Left      # put the marquee/title overlay on a different edge
#   .\attract.ps1 -NoOverlay     # rotation only, no marquee overlay window
#   .\attract.ps1 -Reverify       # rebuild the known-good playlist (+ titles) first
#
# Drag the game window wherever you like - the position is saved
# (window-pos.txt) and every later game opens in the same spot.
# Stop with Ctrl+C.

param(
    [int]$Seconds = 300,
    [ValidateRange(0.5, 10)]
    [double]$Scale = 4,                  # game window size = Scale x native resolution
    [switch]$Reverify,
    [switch]$Fullscreen,                 # default is windowed; pass -Fullscreen to go full screen
    [switch]$NoOverlay,                  # skip the floating marquee/title overlay
    [ValidateSet('Top','Bottom','Left','Right')]
    [string]$Edge = 'Top',               # where the overlay panel sits
    [int]$Monitor = 0                    # which screen the overlay anchors to (0 = primary)
)

$ErrorActionPreference = 'Stop'

$MameDir    = 'C:\mame'
$Mame       = Join-Path $MameDir 'mame.exe'
$Playlist   = Join-Path $PSScriptRoot 'playlist.txt'
$TitlesTxt  = Join-Path $PSScriptRoot 'titles.txt'
$CurrentTxt = Join-Path $PSScriptRoot 'current.txt'
$OverlayPs1 = Join-Path $PSScriptRoot 'overlay.ps1'
$WindowPosTxt = Join-Path $PSScriptRoot 'window-pos.txt'

if (-not (Test-Path $Mame)) { throw "mame.exe not found at $Mame" }
Set-Location $MameDir

# Extract short-name -> full title for the playlist games, streamed from the
# big gamelist.xml so the overlay can show "Galaga" instead of "galaga".
# Also prunes the playlist: drops BIOS sets and games whose driver status is
# 'preliminary' (not working) - they'd show a warning screen and then garbage
# rather than a real attract loop.
function Build-Titles {
    $xml = Join-Path $MameDir 'gamelist.xml'
    if (-not (Test-Path $xml)) { Write-Host "gamelist.xml not found; overlay will show short names." -ForegroundColor DarkYellow; return }
    Write-Host "Extracting game titles + driver status..." -ForegroundColor Cyan
    $want = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($g in (Get-Content $Playlist)) { [void]$want.Add($g) }
    $settings = New-Object System.Xml.XmlReaderSettings
    $settings.DtdProcessing = [System.Xml.DtdProcessing]::Ignore
    $reader = [System.Xml.XmlReader]::Create($xml, $settings)
    $titles = New-Object System.Collections.Generic.List[string]
    $keep   = New-Object System.Collections.Generic.List[string]
    $dropped = New-Object System.Collections.Generic.List[string]
    $cur = $null; $desc = $null; $bios = $null; $status = $null
    $finalize = {
        if ($cur -and $want.Contains($cur)) {
            if ($desc) { $titles.Add("$cur`t$desc") }
            if ($bios -eq 'yes' -or $status -eq 'preliminary') { $dropped.Add($cur) } else { $keep.Add($cur) }
        }
    }
    try {
        while ($reader.Read()) {
            if ($reader.NodeType -ne 'Element') { continue }
            switch ($reader.Name) {
                'game'        { & $finalize; $cur = $reader.GetAttribute('name'); $bios = $reader.GetAttribute('isbios'); $status = $null; $desc = $null }
                'description' { if ($cur) { $desc = $reader.ReadElementContentAsString() } }
                'driver'      { if ($cur) { $status = $reader.GetAttribute('status') } }
            }
        }
        & $finalize
    } finally { $reader.Close() }
    [System.IO.File]::WriteAllLines($TitlesTxt, $titles)
    Write-Host "Titles saved: $($titles.Count) entries." -ForegroundColor Green
    if ($dropped.Count -gt 0) {
        [System.IO.File]::WriteAllLines($Playlist, $keep)
        Write-Host "Pruned $($dropped.Count) BIOS/not-working sets from playlist: $($dropped -join ', ')" -ForegroundColor DarkYellow
    }
}

# 1. Build (or load) the known-good playlist ---------------------------------
if ($Reverify -or -not (Test-Path $Playlist)) {
    Write-Host "Verifying ROMs (one-time, this takes a few minutes)..." -ForegroundColor Cyan
    & $Mame -verifyroms 2>&1 |
        Select-String -Pattern '^romset (\S+).*(is good|is best available)$' |
        ForEach-Object { $_.Matches[0].Groups[1].Value } |
        Sort-Object -Unique |
        Set-Content $Playlist
    Write-Host "Playlist saved: $Playlist" -ForegroundColor Green
    Build-Titles
}
if (-not (Test-Path $TitlesTxt)) { Build-Titles }

$games = @(Get-Content $Playlist | Where-Object { $_ -ne '' })
if ($games.Count -eq 0) { throw "No games in playlist. Run with -Reverify." }
Write-Host "$($games.Count) working games loaded. ~$([math]::Round($Seconds/60,1)) min each. Ctrl+C to stop." -ForegroundColor Green

# Common MAME args: skip info screen, don't grab the mouse, run in a window
$mameArgs = @('-skip_gameinfo', '-nomouse')
if (-not $Fullscreen) { $mameArgs += @('-window', '-nomaximize') }

# MAME suppresses ALL startup screens - including the "this game doesn't work
# properly, press a key" warning - when -seconds_to_run is under 300 (the
# source condition is `str < 60*5`). So each game's dwell time runs as chunks
# of <=299 emulated seconds; longer dwells relaunch the same game per chunk
# (a brief reboot blip roughly every 5 minutes).
$chunks = @()
$rem = $Seconds
while ($rem -gt 0) {
    $c = [Math]::Min(299, $rem)
    $rem -= $c
    if ($c -ge 60 -or $chunks.Count -eq 0) { $chunks += $c }   # drop a tiny trailing chunk
}

# Win32 helpers: MAME 0.147 always opens windowed games at 1x native size near
# the monitor's top-left corner and never saves the window position, so both
# are corrected from the outside after each launch.
if (-not ([System.Management.Automation.PSTypeName]'MameWin').Type) {
    Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class MameWin {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet=CharSet.Auto)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    // the game window is the process's visible top-level window of class "MAME"
    public static IntPtr FindGameWindow(uint pid) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != pid || !IsWindowVisible(h)) return true;
            var sb = new StringBuilder(64); GetClassName(h, sb, 64);
            if (sb.ToString() == "MAME") { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
"@
}
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$script:mameProc = $null
$script:winPos = $null
if (Test-Path $WindowPosTxt) {
    $parts = (Get-Content $WindowPosTxt -TotalCount 1) -split ','
    if ($parts.Count -eq 2) { $script:winPos = @([int]$parts[0], [int]$parts[1]) }
}

# Resize the fresh MAME window to Scale x its native client size (shrunk to
# fit the monitor if needed) and move it to where the user last dragged it.
function Set-MameWindow([IntPtr]$hwnd) {
    $wr = New-Object MameWin+RECT; $cr = New-Object MameWin+RECT
    if (-not [MameWin]::GetWindowRect($hwnd, [ref]$wr)) { return }
    if (-not [MameWin]::GetClientRect($hwnd, [ref]$cr)) { return }
    if ($cr.Right -le 0 -or $cr.Bottom -le 0) { return }
    $chromeW = ($wr.Right - $wr.Left) - $cr.Right
    $chromeH = ($wr.Bottom - $wr.Top) - $cr.Bottom
    $x = $wr.Left; $y = $wr.Top
    if ($script:winPos) { $x = $script:winPos[0]; $y = $script:winPos[1] }
    $wa = [System.Windows.Forms.Screen]::FromPoint((New-Object System.Drawing.Point($x, $y))).WorkingArea
    $s = [Math]::Min($Scale, [Math]::Min(($wa.Width - $chromeW) / $cr.Right, ($wa.Height - $chromeH) / $cr.Bottom))
    if ($s -lt 1) { $s = 1 }
    $w = [int]($cr.Right * $s) + $chromeW
    $h = [int]($cr.Bottom * $s) + $chromeH
    if ($x + $w -gt $wa.Right)  { $x = $wa.Right - $w }
    if ($y + $h -gt $wa.Bottom) { $y = $wa.Bottom - $h }
    if ($x -lt $wa.Left) { $x = $wa.Left }
    if ($y -lt $wa.Top)  { $y = $wa.Top }
    [void][MameWin]::SetWindowPos($hwnd, [IntPtr]::Zero, $x, $y, $w, $h, 0x0014)  # NOZORDER|NOACTIVATE
}

# Remember the window's top-left whenever the user drags it
function Save-WindowPos([IntPtr]$hwnd) {
    $wr = New-Object MameWin+RECT
    if (-not [MameWin]::GetWindowRect($hwnd, [ref]$wr)) { return }
    if ($wr.Left -le -32000) { return }   # minimized
    if ($script:winPos -and $script:winPos[0] -eq $wr.Left -and $script:winPos[1] -eq $wr.Top) { return }
    $script:winPos = @($wr.Left, $wr.Top)
    Set-Content $WindowPosTxt "$($wr.Left),$($wr.Top)"
}

# Run one MAME session, then size/place its window and babysit it until exit
function Invoke-MameRun([string]$game, [int]$runSeconds) {
    $argList = @($game) + $mameArgs + @('-seconds_to_run', $runSeconds)
    $p = Start-Process -FilePath $Mame -ArgumentList $argList -WorkingDirectory $MameDir -PassThru -NoNewWindow
    $script:mameProc = $p
    if ($Fullscreen) { $p.WaitForExit(); return }
    $hwnd = [IntPtr]::Zero
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline -and -not $p.HasExited -and $hwnd -eq [IntPtr]::Zero) {
        $hwnd = [MameWin]::FindGameWindow([uint32]$p.Id)
        if ($hwnd -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 200 }
    }
    if ($hwnd -eq [IntPtr]::Zero) { $p.WaitForExit(); return }
    Start-Sleep -Milliseconds 500     # let MAME settle at its native 1x size
    Set-MameWindow $hwnd
    while (-not $p.HasExited) {
        Save-WindowPos $hwnd
        Start-Sleep -Milliseconds 1500
    }
}

# 2. Launch the floating marquee/title overlay (separate process) ------------
$overlay = $null
if (-not $NoOverlay -and (Test-Path $OverlayPs1)) {
    Remove-Item $CurrentTxt -ErrorAction SilentlyContinue
    $overlay = Start-Process -FilePath 'powershell.exe' -PassThru -WindowStyle Hidden -ArgumentList @(
        '-STA','-NoProfile','-ExecutionPolicy','Bypass','-File', $OverlayPs1,
        '-MameDir', $MameDir, '-StateDir', $PSScriptRoot, '-Edge', $Edge, '-Monitor', $Monitor
    )
}

# 3. Rotate: random, no repeats until the whole list cycles ------------------
try {
    $queue = [System.Collections.ArrayList]@()
    while ($true) {
        if ($queue.Count -eq 0) {
            $shuffled = $games | Sort-Object { Get-Random }
            $queue = [System.Collections.ArrayList]@($shuffled)
            Write-Host "--- Reshuffled $($queue.Count) games ---" -ForegroundColor DarkGray
        }
        $game = $queue[0]
        $queue.RemoveAt(0)

        # Tell the overlay which game is up, then launch it
        [System.IO.File]::WriteAllText($CurrentTxt, $game)
        Write-Host ("{0}  ->  {1}" -f (Get-Date -Format 'HH:mm:ss'), $game) -ForegroundColor Yellow
        foreach ($c in $chunks) { Invoke-MameRun $game $c }
    }
}
finally {
    if ($overlay -and -not $overlay.HasExited) { $overlay.Kill() }
    if ($script:mameProc -and -not $script:mameProc.HasExited) { $script:mameProc.Kill() }
}
