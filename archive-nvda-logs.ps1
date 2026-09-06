<#
.SYNOPSIS
  Preserve NVDA's speech logs before NVDA recycles them.

.DESCRIPTION
  NVDA keeps exactly two logs: %TEMP%\nvda.log for the running session and
  nvda-old.log for the one before it. **Every restart rotates them**, and the
  third-oldest session is simply gone. Two restarts in an evening - which
  happened on 2026-09-05 while getting the log level right - and the first
  session is unrecoverable.

  That matters because of seal step 3e: a speech transcript is now standing
  practice for any session that presses keys, and it is the only instrument that
  can see what NVDA says on its OWN account (window titles, focus changes,
  title rewrites) rather than what we asked it to say.

  ARCHIVE NAMES ARE KEYED ON THE SESSION, NOT THE CLOCK. NVDA's log opens with
  a header carrying the start time and the process id:

      INFO - __main__ (19:56:52.913) - MainThread (14660):
      Starting NVDA version 2026.2 AMD64

  so a session becomes nvda-<date>-<start>-pid<pid>.log. Archiving the same
  live session repeatedly therefore REFRESHES one file rather than making
  duplicates, which is what lets -Watch run on a short interval without turning
  the archive into a heap.

  PRIVACY IS THE REASON FOR THE DESTINATION. The log holds everything NVDA
  spoke in every application - window titles, mail, whatever was on screen -
  so it is personal data by construction, not by accident. It goes to
  JJFlex-private (and optionally the NAS). NEVER into the repo, which is public.

.PARAMETER Watch
  Poll instead of running once. Use during a test session so a restart cannot
  strand the outgoing log.

.PARAMETER IntervalSeconds
  Polling interval for -Watch. Default 30.

.PARAMETER ToNas
  Also copy into the NAS historical tree.

.EXAMPLE
  & "C:\dev\JJFlex-NG\archive-nvda-logs.ps1"
  One shot. This is what the seal runs.

.EXAMPLE
  & "C:\dev\JJFlex-NG\archive-nvda-logs.ps1" -Watch
  Run alongside a test session.
#>
[CmdletBinding()]
param(
    [switch] $Watch,
    [int]    $IntervalSeconds = 30,
    [switch] $ToNas
)

$ErrorActionPreference = 'Stop'

$Dest    = 'C:\Users\nrome\JJFlex-private\nvda-logs'
$NasDest = '\\nas.macaw-jazz.ts.net\jjflex\historical\nvda-logs'
$Sources = @("$env:TEMP\nvda.log", "$env:TEMP\nvda-old.log")

if (-not (Test-Path $Dest)) { New-Item -ItemType Directory -Path $Dest -Force | Out-Null }

# Read the session identity out of the log's own header. Returns $null when the
# file cannot be identified, which is the signal to skip it rather than invent
# a name - an archive keyed on the wrong session is worse than none.
function Get-SessionKey([string] $path) {
    try {
        $head = Get-Content $path -TotalCount 4 -ErrorAction Stop
    } catch { return $null }
    foreach ($line in $head) {
        if ($line -match '\((\d{2}):(\d{2}):(\d{2})\.\d+\)\s*-\s*\w+\s*\((\d+)\)') {
            return @{
                Start = "$($Matches[1])$($Matches[2])$($Matches[3])"
                Pid   = $Matches[4]
            }
        }
    }
    return $null
}

function Save-One([string] $path) {
    if (-not (Test-Path $path)) { return $null }
    $item = Get-Item $path
    if ($item.Length -eq 0) { return $null }

    $key = Get-SessionKey $path
    if (-not $key) {
        Write-Host "  skipped $($item.Name): no session header found"
        return $null
    }

    # The DATE comes from the file, the TIME from the header: the header carries
    # no date, and a session that crosses midnight would otherwise be filed
    # under the wrong day. Machine time throughout - see #549, the operator's
    # clock is not always this one's.
    $date = $item.LastWriteTime.ToString('yyyyMMdd')
    $name = "nvda-$date-$($key.Start)-pid$($key.Pid).log"
    $out  = Join-Path $Dest $name

    # Same session seen again: refresh only if it has actually grown. NVDA holds
    # the file open, so copy through a read-share stream rather than Copy-Item.
    if ((Test-Path $out) -and ((Get-Item $out).Length -ge $item.Length)) { return $null }

    try {
        $in  = [IO.File]::Open($path, 'Open', 'Read', 'ReadWrite')
        $buf = New-Object byte[] $in.Length
        [void]$in.Read($buf, 0, $buf.Length)
        $in.Close()
        [IO.File]::WriteAllBytes($out, $buf)
    } catch {
        Write-Host "  could not read $($item.Name): $($_.Exception.Message)"
        return $null
    }

    $speech = (Select-String -Path $out -Pattern 'Speaking' -SimpleMatch | Measure-Object).Count
    Write-Host ("  saved {0}  ({1} KB, {2} utterances)" -f $name, [math]::Round($item.Length/1KB,1), $speech)
    if ($speech -eq 0) {
        Write-Host "    NOTE: no speech in this one - NVDA was not at IO level. See seal step 3e."
    }
    return $out
}

function Save-All {
    $saved = @()
    foreach ($src in $Sources) {
        $r = Save-One $src
        if ($r) { $saved += $r }
    }
    if ($ToNas -and $saved.Count -gt 0) {
        if (-not (Test-Path $NasDest)) { New-Item -ItemType Directory -Path $NasDest -Force | Out-Null }
        foreach ($f in $saved) {
            Copy-Item $f $NasDest -Force
            # Read it back rather than trusting the copy - the #230 rule.
            $there = Join-Path $NasDest (Split-Path $f -Leaf)
            $ok = (Test-Path $there) -and ((Get-Item $there).Length -eq (Get-Item $f).Length)
            Write-Host ("    NAS: {0}" -f $(if ($ok) { 'verified' } else { 'COPY FAILED' }))
        }
    }
    return $saved.Count
}

Write-Host "NVDA log archive -> $Dest"
if ($Watch) {
    Write-Host "watching every $IntervalSeconds s. Ctrl+C to stop."
    while ($true) {
        $n = Save-All
        Start-Sleep -Seconds $IntervalSeconds
    }
} else {
    $n = Save-All
    if ($n -eq 0) { Write-Host "  nothing new to archive" }
    Write-Host "done"
}
