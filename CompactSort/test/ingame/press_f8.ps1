param(
    [int]$TimeoutSeconds = 20,
    [string]$LogPath = ""
)

$ErrorActionPreference = "Stop"

if (-not $LogPath) {
    $LogPath = Join-Path $env:USERPROFILE "AppData\LocalLow\Questing Goose Studio\Probably Stolen\Player.log"
}
if (-not (Test-Path $LogPath)) {
    Write-Host "Player.log not found: $LogPath"
    exit 2
}

$process = Get-Process |
    Where-Object { $_.MainWindowTitle -like "*Probably Stolen*" } |
    Select-Object -First 1
if (-not $process) {
    Write-Host "Probably Stolen is not running (or the window title changed)."
    exit 2
}

$before = (Get-Item $LogPath).Length
Add-Type -AssemblyName System.Windows.Forms
$shell = New-Object -ComObject WScript.Shell
if (-not $shell.AppActivate($process.Id)) {
    Write-Host "Could not activate the game window."
    exit 2
}
Start-Sleep -Milliseconds 250
[System.Windows.Forms.SendKeys]::SendWait("{F8}")

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$text = ""
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
    $stream = [System.IO.File]::Open($LogPath, "Open", "Read", "ReadWrite")
    try {
        if ($stream.Length -gt $before) {
            $stream.Seek($before, "Begin") | Out-Null
            $reader = New-Object System.IO.StreamReader($stream)
            $text = $reader.ReadToEnd()
        }
    }
    finally {
        $stream.Dispose()
    }
    if ($text -match "\[CompactSort\]\[TEST\] SUMMARY pass=(\d+) fail=(\d+)") {
        $passed = [int]$Matches[1]
        $failed = [int]$Matches[2]
        Write-Host "SELFTEST SUMMARY pass=$passed fail=$failed"
        if ($failed -gt 0) { exit 1 }
        exit 0
    }
}

Write-Host "Timed out waiting for [CompactSort][TEST] SUMMARY."
exit 2
