<#
.SYNOPSIS
  Report the Codex account's rate-limit state, without spending a token on it.

.DESCRIPTION
  Codex the model CANNOT see its own quota. `/status` is a client command, not
  something the model can invoke, and asking it to watch its own usage produces
  a safeguard that cannot fire. The quota lives in the CLIENT, and the client
  exposes it over the app server's JSON-RPC interface.

  This script speaks that interface directly: it spawns `codex app-server`,
  sends `initialize` and then `account/rateLimits/read`, prints the answer, and
  exits. It starts no thread, sends no prompt, and consumes no usage.

  Verified 2026-09-19 against Codex 0.154.0 by reproducing a reading Noel had
  just taken with `/status` in the TUI: 5-hour 75% used resetting 17:46, weekly
  13% used resetting Wed 06:48. Both matched exactly. Re-checked the same day on
  0.155.1, which the version glob below picked up on its own.

.PARAMETER Json
  Emit the raw response object as JSON instead of prose. For a wrapper that
  wants to make its own decisions.

.PARAMETER GateAtPercent
  Exit with code 2 when the 5-hour window is at or above this percentage USED.
  Intended for a wrapper that should stop handing Codex work before the limit
  lands. Exit 0 means below the gate; exit 1 means the reading itself failed,
  which a caller MUST NOT read as "plenty of headroom".

.PARAMETER TimeoutSeconds
  How long to wait for the app server to answer. Default 20.

.EXAMPLE
  & "C:\dev\JJFlex-NG\codex-usage.ps1"

.EXAMPLE
  & "C:\dev\JJFlex-NG\codex-usage.ps1" -GateAtPercent 95
  if ($LASTEXITCODE -eq 2) { "too close to the limit to start a brief" }
#>
[CmdletBinding()]
param(
    [switch] $Json,
    [ValidateRange(1, 100)]
    [int] $GateAtPercent = 0,
    [int] $TimeoutSeconds = 20
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-CodexExe {
    $onPath = Get-Command codex -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    # Standalone install. The release folder carries the version, so glob it
    # rather than pinning -- an upgrade must not silently break this script.
    $root = Join-Path $env:USERPROFILE '.codex\packages\standalone\releases'
    if (Test-Path $root) {
        $exe = Get-ChildItem -Path $root -Filter codex.exe -Recurse -ErrorAction SilentlyContinue |
               Sort-Object LastWriteTime -Descending |
               Select-Object -First 1
        if ($exe) { return $exe.FullName }
    }
    return $null
}

function Format-Stamp([object] $unixSeconds) {
    if ($null -eq $unixSeconds) { return 'unknown' }
    $local = [DateTimeOffset]::FromUnixTimeSeconds([int64]$unixSeconds).ToLocalTime()
    $delta = $local - (Get-Date)
    $when = $local.ToString('ddd yyyy-MM-dd HH:mm')
    if ($delta.TotalSeconds -lt 0) { return "$when (already past)" }
    if ($delta.TotalHours -lt 1)  { return "$when, in $([math]::Round($delta.TotalMinutes)) minutes" }
    if ($delta.TotalHours -lt 48) { return "$when, in $([math]::Round($delta.TotalHours, 1)) hours" }
    return "$when, in $([math]::Round($delta.TotalDays, 1)) days"
}

function Describe-Window([object] $window, [string] $label) {
    if ($null -eq $window) { return "${label}: not reported." }

    # The API reports percent USED. The TUI's /status reports percent LEFT.
    # Say which this is, every time -- near the middle of a window the two
    # readings look equally plausible and differ by fifty points.
    $used = [int]$window.usedPercent
    $left = 100 - $used
    $mins = if ($window.PSObject.Properties.Name -contains 'windowDurationMins' -and $window.windowDurationMins) {
                [int64]$window.windowDurationMins
            } else { $null }

    # Name the window from the duration the server reported, not from a
    # constant here. "5-hour" and "weekly" are true today and are not ours to
    # promise; a hardcoded label would keep saying them after a change.
    $name = if ($null -eq $mins) { $label }
            elseif ($mins -ge 1440) { "$([math]::Round($mins / 1440.0, 1))-day window" }
            else { "$([math]::Round($mins / 60.0, 1))-hour window" }

    $resets = if ($window.PSObject.Properties.Name -contains 'resetsAt') { Format-Stamp $window.resetsAt } else { 'unknown' }
    return "${name}: $used percent used, $left percent left. Resets $resets."
}

$codex = Resolve-CodexExe
if (-not $codex) {
    Write-Error 'Could not find codex.exe on PATH or under ~\.codex\packages\standalone\releases.'
    exit 1
}

$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.FileName               = $codex
$psi.Arguments              = 'app-server'
$psi.RedirectStandardInput  = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError  = $true
$psi.UseShellExecute        = $false
$psi.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false)
$psi.StandardInputEncoding  = [System.Text.UTF8Encoding]::new($false)

$proc = [System.Diagnostics.Process]::Start($psi)
$result = $null
$failure = $null

try {
    # Line-delimited JSON-RPC. NOT the LSP Content-Length framing -- verified
    # 2026-09-19; sending headers here just hangs.
    $init = @{
        jsonrpc = '2.0'; id = 1; method = 'initialize'
        params  = @{ clientInfo = @{ name = 'jjflex-codex-usage'; version = '1.0.0' } }
    } | ConvertTo-Json -Depth 6 -Compress
    $proc.StandardInput.WriteLine($init)

    $ask = @{
        jsonrpc = '2.0'; id = 2; method = 'account/rateLimits/read'; params = @{}
    } | ConvertTo-Json -Depth 6 -Compress
    $proc.StandardInput.WriteLine($ask)
    $proc.StandardInput.Flush()

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $readTask = $proc.StandardOutput.ReadLineAsync()
        $remaining = [int](($deadline - (Get-Date)).TotalMilliseconds)
        if ($remaining -le 0) { break }
        if (-not $readTask.Wait($remaining)) { break }

        $line = $readTask.Result
        if ($null -eq $line) { break }          # stream closed
        if (-not $line.Trim()) { continue }

        try { $msg = $line | ConvertFrom-Json } catch { continue }

        # The server also pushes unsolicited notifications (remote-control
        # status, and account/rateLimits/updated). Ignore anything that is not
        # the answer to id 2.
        if ($msg.PSObject.Properties.Name -notcontains 'id') { continue }
        if ($msg.id -ne 2) { continue }

        if ($msg.PSObject.Properties.Name -contains 'error') {
            $failure = $msg.error | ConvertTo-Json -Depth 6 -Compress
        } else {
            $result = $msg.result
        }
        break
    }
}
finally {
    if (-not $proc.HasExited) { $proc.Kill() }
    $proc.Dispose()
}

if ($failure) {
    Write-Error "The app server refused the read: $failure"
    exit 1
}
if ($null -eq $result) {
    Write-Error "No answer within $TimeoutSeconds seconds. Not a reading of zero usage -- treat it as unknown."
    exit 1
}

if ($Json) {
    $result | ConvertTo-Json -Depth 12
    exit 0
}

$limits = $result.rateLimits
$plan = if ($limits.PSObject.Properties.Name -contains 'planType' -and $limits.planType) { $limits.planType } else { 'unknown' }

Write-Output "Codex usage, read $(Get-Date -Format 'ddd yyyy-MM-dd HH:mm'). Plan: $plan."
Write-Output ''
# The labels are fallbacks only, used when the server omits the duration.
Write-Output (Describe-Window $limits.primary   'Primary window')
Write-Output (Describe-Window $limits.secondary 'Secondary window')

if ($limits.PSObject.Properties.Name -contains 'rateLimitReachedType' -and $limits.rateLimitReachedType) {
    Write-Output ''
    Write-Output "LIMIT REACHED: $($limits.rateLimitReachedType)."
}
if ($result.PSObject.Properties.Name -contains 'ordinaryUsageAllowed' -and $result.ordinaryUsageAllowed -eq $false) {
    Write-Output 'Ordinary included usage is currently BLOCKED by the backend.'
}

$credits = $result.rateLimitResetCredits
if ($credits -and $credits.availableCount -gt 0) {
    Write-Output ''
    Write-Output "Reset credits available: $($credits.availableCount). Each one clears both windows."
    if ($credits.PSObject.Properties.Name -contains 'credits' -and $credits.credits) {
        foreach ($c in $credits.credits) {
            $title = if ($c.title) { $c.title } else { 'reset credit' }
            if ($c.expiresAt) {
                $hours = ([DateTimeOffset]::FromUnixTimeSeconds([int64]$c.expiresAt).ToLocalTime() - (Get-Date)).TotalHours
                $urgent = if ($hours -lt 48) { '  <-- expires soon, use it or lose it' } else { '' }
                Write-Output "  $title, expires $(Format-Stamp $c.expiresAt)$urgent"
            } else {
                Write-Output "  $title, no expiry"
            }
        }
    }
    Write-Output 'Redeem one from the Codex TUI. This script never spends one.'
}

if ($GateAtPercent -gt 0) {
    $used = [int]$limits.primary.usedPercent
    Write-Output ''
    if ($used -ge $GateAtPercent) {
        Write-Output "GATE: 5-hour window at $used percent used, at or above the $GateAtPercent gate. Do not start new work."
        exit 2
    }
    Write-Output "GATE: 5-hour window at $used percent used, below the $GateAtPercent gate. Clear to proceed."
}

exit 0
