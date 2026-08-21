#Requires -Version 7
<#
radiocheck.ps1 — the build test runner (#172).

"Radio check" is what an operator says on the air when they want to know
whether their station is actually getting out. Same question here: on a
build, spawn the just-built app, run the test tiers against it, tear it
down, and report — machine-readable JSON plus a human-readable summary.

Design rules this script enforces, each one bought with an incident:

- The TEST COUNT is reported prominently and compared with the previous
  run. On 2026-08-20 two Sprint 33 tracks went unmerged and the only
  symptom was a suite that got SMALLER and GREENER — the two missing
  files were test files, so every remaining test passed. A falling count
  reads as success unless something says otherwise. This script says so,
  loudly, and exits nonzero.

- "Ran and passed" is distinguished from "ran nothing". `dotnet test
  --no-build` exits 0 with nothing to test, and a zero-test run must
  never be reportable as green. A missing TRX or a total of zero is
  BROKEN INSTRUMENT, not a pass.

- The build under test is identified by path, timestamp and version, and
  flagged STALE when any source file in the working tree was saved AFTER
  the exe was built. Note the instrument: not commit timestamps, which lie
  in both directions (a docs-only commit is not a source change, and the
  normal build-then-commit order makes a good exe look older than its own
  commit). File mtimes also catch the commonest case of all — an edit that
  was saved and never built. Stale binaries have wasted entire testing
  sessions here (see the standing warning in CLAUDE.md).

- The app is ALWAYS spawned with --no-render. This is a blind operator's
  only machine: nothing the runner launches may sound, and render-off
  instances are exempt from single-instance forwarding (see
  Application.Designer.vb), so the spawn cannot poke an operator
  instance that happens to be running.

- The app transcript's session-start marker is load-bearing. A transcript
  with no marker is BROKEN INSTRUMENT, never "no output" — a recorder
  that silently records nothing looks exactly like an app that correctly
  said nothing, and that shape burned three sweep runs on 2026-08-21.
  See Radios/OutputChannelRecorder.cs for the contract.

- Foreground tiers are gated, not run. JJFlexWpf.Tests constructs real
  WPF dialogs on the interactive desktop (that is what put unwanted
  windows on the operator's screen on 2026-08-20), and jjprobe key
  sweeps inject real keystrokes. Both need the desk to be free, which is
  a human's declaration, not a thing this script can detect. Without
  -DeskFree they are reported as DEFERRED — visibly, so a deferral can
  never be mistaken for a pass.

Exit codes: 0 pass · 1 test failures · 2 usage error · 3 broken
instrument · 4 passed with warnings (count drop or stale binary).
#>

[CmdletBinding()]
param(
    # Repo root. Default: two levels up from this script (tools/radiocheck/).
    [string]$RepoRoot,

    [ValidateSet('Debug', 'Release')]
    [string]$Config = 'Debug',

    [ValidateSet('x64', 'x86')]
    [string]$Platform = 'x64',

    # Override the app exe under test. Default is computed from RepoRoot,
    # Config and Platform. Pointing this at a nonexistent file is the
    # documented way to verify the broken-instrument path.
    [string]$ExePath,

    # Skip tiers. A skipped tier is reported as skipped, never as passed.
    [switch]$SkipUnit,
    [switch]$SkipSmoke,

    # The human's declaration that the desk is free. Enables the
    # foreground tier (JJFlexWpf.Tests). This is deliberately a switch a
    # person passes, not a state the script detects: "the desk is free"
    # is the four-beat handshake from the Sprint 33 plan, and only the
    # operator can say it.
    [switch]$DeskFree,

    # Auto-connect preflight override. If any operator profile would
    # auto-connect at startup, the spawned instance would reach for the
    # radio as a second MultiFlex client — and FlexBase.setupFromScratch()
    # sets RFPower = 100 unconditionally on a radio it has never seen
    # (found by Sprint 33 Track G). So by default the smoke tier refuses
    # to spawn when auto-connect is armed. Pass this only when reaching
    # the radio is intended and sanctioned.
    [switch]$AllowRadioReach,

    # Optional dotnet test --filter for the unit tier. A filtered run
    # legitimately has fewer tests, so count tracking is suspended for it
    # — comparing a filtered count against a full baseline would either
    # false-alarm or, worse, lower the stored baseline.
    [string]$UnitFilter,

    # How long the spawned app must stay alive after the marker appears.
    [int]$SettleSeconds = 10,

    # How long to wait for the transcript's session-start marker.
    [int]$MarkerTimeoutSeconds = 45,

    # Where this run's artifacts go. Default: a timestamped folder under
    # the state dir, outside the repo so runs never pollute git status.
    [string]$OutDir,

    # Where cross-run state (previous test counts) lives.
    [string]$StateDir
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3

# ── Resolve paths ────────────────────────────────────────────────────────

if (-not $RepoRoot) { $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path }
if (-not (Test-Path (Join-Path $RepoRoot 'JJFlexRadio.sln'))) {
    Write-Error "RepoRoot '$RepoRoot' does not contain JJFlexRadio.sln"
    exit 2
}
if (-not $StateDir) { $StateDir = Join-Path $env:LOCALAPPDATA 'jjflex-radiocheck' }
$runId = Get-Date -Format 'yyyyMMdd-HHmmss'
if (-not $OutDir) { $OutDir = Join-Path $StateDir "runs\$runId" }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

if (-not $ExePath) {
    $winArch = if ($Platform -eq 'x64') { 'win-x64' } else { 'win-x86' }
    $ExePath = Join-Path $RepoRoot "bin\$Platform\$Config\net10.0-windows\$winArch\jjflexible.exe"
}

$branch  = (& git -C $RepoRoot rev-parse --abbrev-ref HEAD 2>$null)
$headSha = (& git -C $RepoRoot rev-parse --short HEAD 2>$null)
$headCommitTime = (& git -C $RepoRoot log -1 --format=%cI 2>$null)

# ── Is the binary older than the source it claims to be built from? ──────
#
# COMMIT TIMESTAMPS ARE THE WRONG INSTRUMENT and the first version of this
# check used them. Two ways they lie, both seen on 2026-08-21:
#
#   - Judging against HEAD warns after a DOCS-ONLY commit, which cannot
#     possibly have changed the binary. A runner that cries stale every time
#     you write a planning note trains you to ignore it on the day it is right.
#   - Judging against the last SOURCE commit is still wrong, because the
#     normal order is build-then-commit. The exe measured that day was 59
#     seconds OLDER than the commit whose source it contained.
#
# What actually decides the question is the WORKING TREE: is any file the
# compiler reads newer on disk than the exe? That also catches the case
# commit times cannot see at all, and which is by far the most common in
# practice - an edit that was saved and never built.
function Get-NewestSourceWrite([string]$root) {
    $exts = @('.vb','.cs','.xaml','.resx','.csproj','.vbproj','.sln')
    # git ls-files, so bin/ obj/ and anything ignored stay out of it without
    # having to maintain an exclusion list that will drift.
    $tracked = & git -C $root ls-files 2>$null
    if (-not $tracked) { return $null }
    $newest = $null; $newestPath = $null
    foreach ($rel in $tracked) {
        $ext = [System.IO.Path]::GetExtension($rel)
        $isNative = ($rel -like 'runtimes/*') -and
                    (@('.dll','.so','.dylib','.pdb') -contains $ext)
        if ($exts -notcontains $ext -and -not $isNative) { continue }
        $full = Join-Path $root $rel
        try { $t = [System.IO.File]::GetLastWriteTimeUtc($full) } catch { continue }
        if ($null -eq $newest -or $t -gt $newest) { $newest = $t; $newestPath = $rel }
    }
    if ($null -eq $newest) { return $null }
    return [pscustomobject]@{ Utc = $newest; Path = $newestPath }
}
$newestSource = Get-NewestSourceWrite $RepoRoot

# ── The runner's own start marker ────────────────────────────────────────
# Written before anything else runs, for the same reason the app writes
# its transcript marker first: a run directory containing run-start.json
# but no result.json is a runner that DIED, which must be
# distinguishable from a runner that never ran. Retrofitting a marker
# costs a day — the 2026-08-21 harness failures were all missing-marker
# shaped.

$runStart = [ordered]@{
    schema  = 1
    tool    = 'radiocheck'
    runId   = $runId
    utc     = (Get-Date).ToUniversalTime().ToString('O')
    host    = $env:COMPUTERNAME
    branch  = $branch
    headSha = $headSha
    args    = [ordered]@{
        config = $Config; platform = $Platform; exePath = $ExePath
        skipUnit = [bool]$SkipUnit; skipSmoke = [bool]$SkipSmoke
        deskFree = [bool]$DeskFree; unitFilter = $UnitFilter
    }
}
$runStart | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutDir 'run-start.json')
Write-Host "RADIOCHECK START $runId  branch=$branch head=$headSha"

# ── Shared helpers ───────────────────────────────────────────────────────

$script:Tiers    = [System.Collections.Generic.List[object]]::new()
$script:Warnings = [System.Collections.Generic.List[string]]::new()

function New-Tier([string]$Name) {
    $t = [ordered]@{
        name    = $Name
        status  = 'not-run'   # pass | fail | broken | deferred | skipped
        detail  = [System.Collections.Generic.List[string]]::new()
        total   = $null; passed = $null; failed = $null
        previousTotal = $null; countDropped = $false
    }
    $script:Tiers.Add($t)
    return $t
}

function Read-State {
    $p = Join-Path $StateDir 'state.json'
    if (Test-Path $p) {
        try { return Get-Content $p -Raw | ConvertFrom-Json -AsHashtable } catch {
            $script:Warnings.Add("state file '$p' was unreadable and has been ignored: $($_.Exception.Message)")
        }
    }
    return @{ schema = 1; branches = @{} }
}

function Save-State($state) {
    New-Item -ItemType Directory -Force -Path $StateDir | Out-Null
    $state | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $StateDir 'state.json')
}

# Compare this tier's discovered-test total against the last full run on
# the same branch, and record the drop as a WARNING that survives into
# the exit code. Total, not passed: the 2026-08-20 incident was suite
# SHRINKAGE — every remaining test passed, which is exactly why nothing
# noticed.
function Compare-Count($tier, $state, [string]$tierKey, [bool]$updateState) {
    $branchKey = if ($branch) { $branch } else { '(unknown-branch)' }
    if (-not $state.branches.ContainsKey($branchKey)) { $state.branches[$branchKey] = @{} }
    $prev = $state.branches[$branchKey][$tierKey]
    $dropFrom = $null; $dropUtc = $null
    if ($null -ne $prev -and $null -ne $prev.total) {
        $tier.previousTotal = [int]$prev.total
        if ($tier.total -lt $tier.previousTotal) {
            $tier.countDropped = $true
            $msg = "TEST COUNT DROPPED in $($tier.name): $($tier.total) now, was $($tier.previousTotal) " +
                   "on $($prev.utc). A shrinking, greener suite is how two unmerged Sprint 33 tracks " +
                   "hid on 2026-08-20 — treat this as a missing-tests investigation, not a pass."
            $tier.detail.Add($msg)
            $script:Warnings.Add($msg)
            $dropFrom = $tier.previousTotal
            $dropUtc = $prev.utc
        }
        # An unrecovered drop leaves a SCAR in state: the warning above
        # fires once, at drop time, but if that one exit-4 run went
        # unread the shrink would otherwise vanish — the next run
        # compares against the new, lower baseline and reads green. The
        # scar keeps the loss visible (as a note, not a repeated alarm —
        # a suite that cries wolf gets ignored) until the count climbs
        # back to where it was.
        if ($prev.ContainsKey('droppedFrom') -and $null -ne $prev.droppedFrom) {
            if ($tier.total -lt [int]$prev.droppedFrom) {
                $dropFrom = [int]$prev.droppedFrom
                $dropUtc = $prev.droppedUtc
                if (-not $tier.countDropped) {
                    $tier.detail.Add("note: this tier's count dropped from $dropFrom on $dropUtc and has " +
                                     "not recovered (currently $($tier.total))")
                }
            }
            # else: recovered — the scar clears below by not being carried.
        }
    }
    if ($updateState -and $tier.total -gt 0) {
        $entry = @{
            total = $tier.total; utc = (Get-Date).ToUniversalTime().ToString('O'); runId = $runId
        }
        if ($null -ne $dropFrom) { $entry.droppedFrom = $dropFrom; $entry.droppedUtc = $dropUtc }
        $state.branches[$branchKey][$tierKey] = $entry
    }
}

# Run one dotnet-test tier and grade it. Never passes --no-build: a test
# run against a project the build silently skipped exits 0 having tested
# nothing, and this function's whole job is to make that impossible to
# report as green.
function Invoke-DotnetTestTier($tier, [string]$csproj, [string]$trxName, [string]$filter, $state, [string]$tierKey) {
    $trxDir = Join-Path $OutDir 'trx'
    $logPath = Join-Path $OutDir "$trxName-output.log"
    $testArgs = @('test', $csproj, '-c', $Config, "-p:Platform=$Platform",
                  '--logger', "trx;LogFileName=$trxName.trx",
                  '--results-directory', $trxDir, '--verbosity', 'minimal')
    if ($filter) { $testArgs += @('--filter', $filter) }

    & dotnet @testArgs 2>&1 | Tee-Object -FilePath $logPath | Out-Null
    $exit = $LASTEXITCODE

    $trxPath = Join-Path $trxDir "$trxName.trx"
    if (-not (Test-Path $trxPath)) {
        # No TRX at all: either the test project failed to build or the
        # run produced nothing. Either way nothing was tested, and a
        # nothing-was-tested run is a broken instrument, not a result.
        $tier.status = 'broken'
        $tier.detail.Add("BROKEN INSTRUMENT: no TRX result file was produced (dotnet test exit $exit). " +
                         "Nothing ran; this is not a pass. Output: $logPath")
        return
    }

    [xml]$trx = Get-Content $trxPath
    $c = $trx.TestRun.ResultSummary.Counters
    $tier.total  = [int]$c.total
    $tier.passed = [int]$c.passed
    $tier.failed = [int]$c.failed + [int]$c.error

    if ($tier.total -eq 0) {
        $tier.status = 'broken'
        $tier.detail.Add('BROKEN INSTRUMENT: the run discovered ZERO tests. `dotnet test` happily ' +
                         'exits 0 with nothing to test; zero is never green.')
        return
    }

    # A filtered run is a legitimate subset; comparing it against the
    # full baseline (or storing it AS the baseline) would poison count
    # tracking, so both are suspended and the summary says so.
    if ($filter) {
        $tier.detail.Add("filtered run (--filter '$filter'): count tracking suspended for this run")
        Compare-Count $tier $state $tierKey $false | Out-Null
    } else {
        Compare-Count $tier $state $tierKey $true | Out-Null
    }

    if ($tier.failed -gt 0) {
        $tier.status = 'fail'
        $tier.detail.Add("$($tier.failed) test(s) failed of $($tier.total). Output: $logPath")
    } elseif ($exit -ne 0) {
        # Tests all green but dotnet test still unhappy — do not paper
        # over it; something (a post-run hook, a crashed host) went wrong.
        $tier.status = 'broken'
        $tier.detail.Add("dotnet test exited $exit despite $($tier.passed)/$($tier.total) passing; " +
                         "treating as broken. Output: $logPath")
    } else {
        $tier.status = 'pass'
        $tier.detail.Add("$($tier.passed) passed of $($tier.total), 0 failed")
    }
}

# ── Build identity ───────────────────────────────────────────────────────
# Recorded FIRST and prominently: which binary did this run actually
# test? Stale binaries have wasted whole sessions here, so the exe is
# also compared against HEAD's commit time — an exe older than the last
# commit cannot contain it.

$buildInfo = [ordered]@{
    exePath = $ExePath; exists = $false
    lastWriteUtc = $null; fileVersion = $null; productVersion = $null
    headSha = $headSha; branch = $branch; stale = $false
    newestSourceFile = $null; newestSourceUtc = $null
    newestOutput = $null; newestOutputUtc = $null
}
if (Test-Path $ExePath) {
    $exeItem = Get-Item $ExePath
    $buildInfo.exists = $true
    $buildInfo.lastWriteUtc   = $exeItem.LastWriteTimeUtc.ToString('O')
    $buildInfo.fileVersion    = $exeItem.VersionInfo.FileVersion
    $buildInfo.productVersion = $exeItem.VersionInfo.ProductVersion
    if ($newestSource) {
        $buildInfo.newestSourceFile = $newestSource.Path
        $buildInfo.newestSourceUtc  = $newestSource.Utc.ToString('O')
        # Compare against the newest of OUR build outputs, not the exe alone.
        # JJ Flexible is an exe plus a dozen project DLLs, and MSBuild rebuilds
        # only the assemblies whose inputs changed. So an exe OLDER than a
        # Radios/*.cs edit is what a CORRECT incremental build looks like —
        # that code compiles into Radios.dll, which did rebuild. Measured
        # 2026-08-21: source 12:43:38, jjflexible.exe 12:43:15, Radios.dll
        # 12:43:48. Checking the exe alone fires on nearly every library edit,
        # and a warning that cries wolf on routine work is one you learn to
        # click past — which is how a safe false positive becomes a missed
        # real one.
        $newestOut = $exeItem.LastWriteTimeUtc
        $newestOutName = Split-Path $exeItem.FullName -Leaf
        $appDir = Split-Path $exeItem.FullName -Parent
        # Only OUR assemblies: a DLL counts if the repo has a project of the
        # same name. Third-party DLLs are excluded on purpose — a NuGet
        # restore refreshes them and would inflate the newest-output time,
        # masking a genuinely stale build.
        $ourNames = @{}
        foreach ($proj in (Get-ChildItem $RepoRoot -Recurse -Include *.csproj,*.vbproj -ErrorAction SilentlyContinue)) {
            $ourNames[[System.IO.Path]::GetFileNameWithoutExtension($proj.Name)] = $true
        }
        foreach ($dll in (Get-ChildItem $appDir -Filter *.dll -File -ErrorAction SilentlyContinue)) {
            if (-not $ourNames.ContainsKey([System.IO.Path]::GetFileNameWithoutExtension($dll.Name))) { continue }
            if ($dll.LastWriteTimeUtc -gt $newestOut) {
                $newestOut = $dll.LastWriteTimeUtc; $newestOutName = $dll.Name
            }
        }
        $buildInfo.newestOutput    = $newestOutName
        $buildInfo.newestOutputUtc = $newestOut.ToString('O')

        if ($newestOut -lt $newestSource.Utc) {
            $buildInfo.stale = $true
            $script:Warnings.Add("STALE BUILD: $($newestSource.Path) was written " +
                "$($newestSource.Utc.ToString('u')), which is AFTER the newest build output " +
                "($newestOutName, $($newestOut.ToString('u'))) — so no assembly can contain it. " +
                'Rebuild before trusting anything this run reports about the app.')
        }
    }
}

# ── Tier: unit (Radios.Tests — pure in-process, no desktop, no radio) ────

$state = Read-State

$unitTier = New-Tier 'unit (Radios.Tests)'
if ($SkipUnit) {
    $unitTier.status = 'skipped'
    $unitTier.detail.Add('skipped by -SkipUnit')
} else {
    Write-Host 'Tier: unit (Radios.Tests) ...'
    Invoke-DotnetTestTier $unitTier (Join-Path $RepoRoot 'Radios.Tests\Radios.Tests.csproj') `
        'radios-unit' $UnitFilter $state 'unit'
}

# ── Tier: smoke (spawn the just-built app, silent + recorded) ────────────

$smokeTier = New-Tier 'smoke (app spawn, silent, transcript-verified)'
$transcriptStats = $null

if ($SkipSmoke) {
    $smokeTier.status = 'skipped'
    $smokeTier.detail.Add('skipped by -SkipSmoke')
} elseif (-not $buildInfo.exists) {
    $smokeTier.status = 'broken'
    $smokeTier.detail.Add("BROKEN INSTRUMENT: app exe not found at $ExePath — there is no build to test. " +
                          'A runner with nothing to spawn must say so, not report green.')
} else {
    # Auto-connect preflight. The app has no --no-autoconnect switch, so
    # the only safe gate is reading the same config the app will read.
    # If ANY operator profile is armed, the spawn would grab the radio.
    $armed = @()
    $acDir = Join-Path $env:APPDATA 'JJFlexRadio'
    if (Test-Path $acDir) {
        foreach ($f in Get-ChildItem $acDir -Filter '*_autoConnectV2.xml' -ErrorAction SilentlyContinue) {
            try {
                [xml]$ac = Get-Content $f.FullName
                $n = $ac.AutoConnectConfig
                $globalOn = ($null -eq $n.GlobalAutoConnectEnabled) -or ($n.GlobalAutoConnectEnabled -ne 'false')
                if ($globalOn -and $n.Enabled -eq 'true' -and $n.RadioSerial) { $armed += $f.Name }
            } catch { $armed += "$($f.Name) (unreadable — assumed armed)" }
        }
    }

    if ($armed.Count -gt 0 -and -not $AllowRadioReach) {
        $smokeTier.status = 'deferred'
        $smokeTier.detail.Add("DEFERRED: auto-connect is armed in $($armed -join ', ') — a spawned instance " +
            'would connect to the radio as a second MultiFlex client (and FlexBase.setupFromScratch() sets ' +
            'RFPower=100 on an unknown radio). Pass -AllowRadioReach only when that is sanctioned.')
    } else {
        Write-Host 'Tier: smoke — spawning the app (silent, recorded) ...'
        $transcriptPath = Join-Path $OutDir 'transcript.jsonl'

        $psi = [System.Diagnostics.ProcessStartInfo]::new()
        $psi.FileName = $ExePath
        # --no-render is non-negotiable (operator machine; also what
        # exempts this instance from single-instance forwarding).
        # --record with an explicit path keeps this run's evidence out of
        # the operator's own transcript folder.
        $psi.ArgumentList.Add('--no-render')
        $psi.ArgumentList.Add("--record=$transcriptPath")
        $psi.WorkingDirectory = Split-Path $ExePath
        $psi.UseShellExecute = $false
        # Belt and suspenders: flags win over env vars, but set both.
        $psi.Environment['JJFLEX_RENDER'] = '0'

        $proc = [System.Diagnostics.Process]::Start($psi)
        $smokeTier.detail.Add("spawned pid $($proc.Id)")

        # Wait for the session-start marker. Its absence after the
        # timeout is BROKEN INSTRUMENT by contract (OutputChannelRecorder
        # docs): a transcript that never opened looks identical to an app
        # that said nothing, and only the marker separates them.
        $marker = $null
        $deadline = (Get-Date).AddSeconds($MarkerTimeoutSeconds)
        while ((Get-Date) -lt $deadline) {
            if ($proc.HasExited) { break }
            if (Test-Path $transcriptPath) {
                # The app holds the file open with FileShare.Read; open
                # with ReadWrite share so the read never collides.
                $fs = [System.IO.FileStream]::new($transcriptPath, 'Open', 'Read', [System.IO.FileShare]::ReadWrite)
                try {
                    $rd = [System.IO.StreamReader]::new($fs)
                    $first = $rd.ReadLine()
                } finally { $fs.Dispose() }
                if ($first) {
                    try { $marker = $first | ConvertFrom-Json } catch { }
                    if ($marker) { break }
                }
            }
            Start-Sleep -Milliseconds 250
        }

        if ($proc.HasExited -and -not $marker) {
            $smokeTier.status = 'broken'
            $smokeTier.detail.Add("BROKEN INSTRUMENT: app exited (code $($proc.ExitCode)) before writing a " +
                                  'session-start marker.')
        } elseif (-not $marker) {
            $smokeTier.status = 'broken'
            $smokeTier.detail.Add("BROKEN INSTRUMENT: no session-start marker within ${MarkerTimeoutSeconds}s. " +
                                  'By the transcript contract this is a dead recorder, never "no output".')
            try { $proc.Kill() } catch { }
        } elseif ($marker.event -ne 'session-start' -or $marker.render -ne $false -or [int]$marker.pid -ne $proc.Id) {
            # A marker that is not ours (wrong pid) or an instance that
            # is not actually silent (render true) is worse than none —
            # it means we are asserting against the wrong instrument.
            $smokeTier.status = 'broken'
            $smokeTier.detail.Add("BROKEN INSTRUMENT: marker mismatch — event=$($marker.event) " +
                                  "render=$($marker.render) pid=$($marker.pid) (expected our pid $($proc.Id)).")
            try { $proc.Kill() } catch { }
        } else {
            $smokeTier.detail.Add("session-start confirmed: appVersion=$($marker.appVersion) schema=$($marker.schema) render=off record=on")

            # Settle: the app must stay alive. An app that writes its
            # marker and then dies is a launch failure the marker alone
            # would hide.
            Start-Sleep -Seconds $SettleSeconds
            if ($proc.HasExited) {
                $smokeTier.status = 'fail'
                $smokeTier.detail.Add("app exited during the ${SettleSeconds}s settle (code $($proc.ExitCode))")
            } else {
                # Optional outside-in check via jjprobe (read-only
                # `windows` — it observes, it cannot inject; see
                # tools/uia-probe/README.md).
                $jjprobe = Join-Path $RepoRoot "tools\uia-probe\bin\$Platform\$Config\net10.0-windows\jjprobe.exe"
                if (Test-Path $jjprobe) {
                    $probeOut = & $jjprobe windows --pid $proc.Id 2>&1
                    $line = ($probeOut | Select-Object -First 1)
                    if ($line -match '(\d+) top-level window') {
                        $n = [int]$Matches[1]
                        if ($n -ge 1) { $smokeTier.detail.Add("jjprobe sees $n top-level window(s) — the app is visible to UIA, i.e. to a screen reader") }
                        else {
                            $smokeTier.status = 'fail'
                            $smokeTier.detail.Add('jjprobe sees ZERO top-level windows — the app is running but invisible to UIA')
                        }
                    } else {
                        $smokeTier.detail.Add("jjprobe windows output not understood; recorded but not graded: $line")
                    }
                } else {
                    $smokeTier.detail.Add('jjprobe not built; UIA window check skipped (build tools/uia-probe/UiaProbe.csproj to enable)')
                }

                # Teardown: polite close first, kill as fallback. Only
                # ever by the process OBJECT we spawned — never by name,
                # because the operator may be running his own jjflexible
                # and killing by name would take his session down.
                $closed = $false
                try { $closed = $proc.CloseMainWindow() } catch { }
                if (-not $proc.WaitForExit(15000)) {
                    try { $proc.Kill() } catch { }
                    $proc.WaitForExit(5000) | Out-Null
                    $smokeTier.detail.Add('app did not close politely within 15s; killed (transcript lines are flushed per event, so nothing is lost but the session-end)')
                } else {
                    $smokeTier.detail.Add("app closed politely (CloseMainWindow accepted: $closed)")
                }

                # Read the whole transcript and summarise. Event counts
                # are the assertion surface future tiers grow into; today
                # the smoke tier records them and asserts only what a
                # bare launch guarantees.
                $events = @{}
                $sessionEnd = $false
                foreach ($ln in Get-Content $transcriptPath) {
                    try { $e = $ln | ConvertFrom-Json } catch { continue }
                    $events[$e.event] = 1 + ($events[$e.event] ?? 0)
                    if ($e.event -eq 'session-end') { $sessionEnd = $true }
                }
                $transcriptStats = [ordered]@{
                    path = $transcriptPath
                    events = $events
                    sessionEnd = $sessionEnd
                }
                $evSummary = ($events.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ', '
                $smokeTier.detail.Add("transcript events: $evSummary")
                if (-not $sessionEnd) { $smokeTier.detail.Add('no session-end marker (expected when the app was killed; otherwise it crashed)') }
                $speechCount = ($events['speech'] ?? 0) + ($events['output'] ?? 0)
                if ($speechCount -eq 0) {
                    $script:Warnings.Add('smoke: zero speech events recorded during startup — the greeting should have ' +
                        'been recorded even if verbosity-gated; check the speech backend line in the transcript')
                }
                if ($smokeTier.status -eq 'not-run') { $smokeTier.status = 'pass' }
            }
        }

        # Orphan check: the pid we spawned must be gone. #21's
        # orphan-process bug is exactly an instance that outlives its
        # window; a runner that leaks one per run would reproduce the
        # problem it exists to catch.
        if (-not $proc.HasExited) {
            $smokeTier.status = 'broken'
            $smokeTier.detail.Add("ORPHAN: pid $($proc.Id) survived teardown including Kill(). Report this — it is the #21 shape.")
        }
    }
}

# ── Tier: foreground (gated — JJFlexWpf.Tests needs the desktop) ─────────

$fgTier = New-Tier 'foreground (JJFlexWpf.Tests — takes the desktop)'
if (-not $DeskFree) {
    $fgTier.status = 'deferred'
    $fgTier.detail.Add('DEFERRED: needs the interactive desktop and this is the operator''s only machine. ' +
        'Run with -DeskFree after the handshake ("gonna run a UI probe tool" / "cool have at it"), or run it ' +
        'on the laptop — see tools/radiocheck/README.md for the standing recommendation.')
} else {
    Write-Host 'Tier: foreground (JJFlexWpf.Tests) — desk declared free ...'
    Invoke-DotnetTestTier $fgTier (Join-Path $RepoRoot 'JJFlexWpf.Tests\JJFlexWpf.Tests.csproj') `
        'wpf-foreground' $null $state 'foreground'
}

Save-State $state

# ── Verdict ──────────────────────────────────────────────────────────────
# Precedence: broken beats fail beats warn beats pass. Deferred and
# skipped tiers never contribute a pass — but if NOTHING produced a
# result, the run as a whole is a broken instrument: a runner that ran
# nothing must not exit 0.

$ranAny  = $false
$verdict = 'pass'
foreach ($t in $script:Tiers) {
    switch ($t.status) {
        'broken' { $verdict = 'broken' }
        'fail'   { if ($verdict -ne 'broken') { $verdict = 'fail' } }
        'pass'   { $ranAny = $true }
    }
}
if (-not $ranAny -and $verdict -eq 'pass') {
    $verdict = 'broken'
    $script:Warnings.Add('no tier actually ran and passed — every tier was skipped, deferred or broken; ' +
        'that is a broken instrument, not a green run')
}
if ($verdict -eq 'pass' -and $script:Warnings.Count -gt 0) { $verdict = 'warn' }

# ── Emit: machine-readable result ────────────────────────────────────────

$result = [ordered]@{
    schema   = 1
    tool     = 'radiocheck'
    runId    = $runId
    utc      = (Get-Date).ToUniversalTime().ToString('O')
    verdict  = $verdict
    build    = $buildInfo
    tiers    = $script:Tiers
    warnings = $script:Warnings
    transcript = $transcriptStats
    outDir   = $OutDir
}
$result | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $OutDir 'result.json')

# ── Emit: human-readable summary (prose and bullets — no tables) ─────────

$sum = [System.Collections.Generic.List[string]]::new()
$sum.Add("Radiocheck run $runId — verdict: $($verdict.ToUpper())")
$sum.Add('')
$sum.Add("Build under test: $($buildInfo.exePath)")
if ($buildInfo.exists) {
    $sum.Add("  built $($buildInfo.lastWriteUtc) (UTC), file version $($buildInfo.fileVersion), " +
             "branch $branch at $headSha")
    if ($buildInfo.stale) {
        $sum.Add("  WARNING: STALE — $($buildInfo.newestSourceFile) was saved after the newest build output ($($buildInfo.newestOutput)).")
    }
} else {
    $sum.Add('  MISSING — no binary at that path.')
}
$sum.Add('')

# The test count line comes before per-tier detail on purpose: it is the
# number that lied on 2026-08-20, so it gets the prominence.
$countBits = foreach ($t in $script:Tiers) {
    if ($null -ne $t.total) {
        # A broken tier's count is not comparable — comparison never ran
        # for it, so claiming "first tracked run" here would be false.
        $prevNote = if ($t.status -eq 'broken') { ' — BROKEN, not comparable' }
                    elseif ($t.countDropped) { " — DROPPED from $($t.previousTotal)" }
                    elseif ($null -ne $t.previousTotal) { " (previously $($t.previousTotal))" }
                    else { ' (first tracked run on this branch)' }
        "$($t.name): $($t.total) tests$prevNote"
    }
}
if ($countBits) {
    $sum.Add('Test count:')
    foreach ($b in $countBits) { $sum.Add("  - $b") }
} else {
    $sum.Add('Test count: NOTHING PRODUCED A COUNT — see tier detail; this is never a pass.')
}
$sum.Add('')

foreach ($t in $script:Tiers) {
    $sum.Add("Tier $($t.name): $($t.status.ToUpper())")
    foreach ($d in $t.detail) { $sum.Add("  - $d") }
}
$sum.Add('')

$deferred = @($script:Tiers | Where-Object { $_.status -eq 'deferred' })
if ($deferred.Count -gt 0) {
    $sum.Add('Deferred — these did NOT run and are NOT passes:')
    foreach ($t in $deferred) { $sum.Add("  - $($t.name)") }
    $sum.Add('')
}
if ($script:Warnings.Count -gt 0) {
    $sum.Add('Warnings:')
    foreach ($w in $script:Warnings) { $sum.Add("  - $w") }
    $sum.Add('')
}
$sum.Add("Artifacts: $OutDir (result.json is the machine-readable record)")

$sum | Set-Content (Join-Path $OutDir 'summary.txt')
$sum | ForEach-Object { Write-Host $_ }

switch ($verdict) {
    'pass'   { exit 0 }
    'fail'   { exit 1 }
    'warn'   { exit 4 }
    default  { exit 3 }
}
