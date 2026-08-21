# check-memory-drift.ps1
#
# Memory entries assert things about the code. Code moves. Nothing notices.
#
# This walks every memory entry, pulls out the file paths and code symbols it
# names, and checks whether they still exist. What comes back is a list of
# STALENESS CANDIDATES - not errors. A missing path can mean the entry is out
# of date, or that it was describing history on purpose. A human decides which.
#
# Built 2026-08-21 after a stale entry claimed the Prism migration had not
# started, when Prism had in fact shipped. The entry named
# Radios/Speech/PrismNative.cs nowhere; the index line was right and the entry
# was wrong. See memory/feedback_negative_results_need_a_positive_control.md
# and memory/project_description_drift_pattern.md.
#
# WHY A CHECKER AND NOT A "VERIFY:" LINE PER ENTRY: a hand-written recipe is
# itself a thing that goes stale, and there are 150 project entries. This runs
# in seconds over all of them and cannot drift out of sync with the tree,
# because it reads the tree.
#
#   .\check-memory-drift.ps1                  # summary
#   .\check-memory-drift.ps1 -Detailed        # every miss, with its entry
#   .\check-memory-drift.ps1 -Tree C:\dev\jjflex-33a

# The tree list MATTERS. Memory legitimately names files in sibling repos and
# in JJFlex-private; if those are not indexed, every such reference reads as
# staleness and the real signal drowns. First run on 2026-08-21 flagged 69
# candidates, of which a large share were only "not in THIS repo".
param(
    [string]   $MemoryDir = "C:\Users\nrome\.claude\projects\C--dev-JJFlex-NG\memory",
    [string[]] $Tree      = @(
        "C:\dev\jjflex-33a",              # current sprint tree
        "C:\dev\JJFlex-NG",               # main working tree
        "C:\dev\jjf-data",                # data provider repo
        "C:\dev\jjflexible-connect",      # Connect
        "C:\dev\rigmeter",                # extracted Sprint 30
        "C:\dev\prism",                   # speech backend, vendored as a dll
        "C:\Users\nrome\JJFlex-private"   # AARs, easter eggs, unlock codes
    ),
    [switch]   $Detailed
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $MemoryDir)) { Write-Host "No memory dir at $MemoryDir" -ForegroundColor Red; exit 2 }
$roots = @($Tree | Where-Object { Test-Path $_ })
if (-not $roots) { Write-Host "None of the given trees exist." -ForegroundColor Red; exit 2 }

Write-Host "Memory drift check"
Write-Host "  memory : $MemoryDir"
Write-Host "  trees  : $($roots -join ', ')"
Write-Host ""

# Build one index of every file in the trees, keyed by relative path AND by
# bare filename. Bare-name matching is what lets an entry say "FlexBase.cs"
# without a path and still resolve.
$byRel  = @{}
$byName = @{}
foreach ($r in $roots) {
    $before = $byRel.Count
    $files = @()
    if (Test-Path (Join-Path $r ".git")) {
        Push-Location $r
        $files = @(git ls-files 2>$null)
        Pop-Location
    }
    # Not a git repo (JJFlex-private), or ls-files came back empty for any
    # reason - walk the filesystem instead. An empty index would make every
    # reference into that tree look stale, which is the exact false-negative
    # this whole exercise is about.
    if (-not $files -or $files.Count -eq 0) {
        $files = @(Get-ChildItem $r -Recurse -File -ErrorAction SilentlyContinue |
                   Where-Object { $_.FullName -notmatch '\\(\.git|bin|obj|node_modules|\.vs)\\' } |
                   ForEach-Object { $_.FullName.Substring($r.Length).TrimStart('\','/') })
    }
    foreach ($p in $files) {
        $rel = $p -replace '/', '\'
        $byRel[$rel.ToLower()] = $true
        $byName[(Split-Path $rel -Leaf).ToLower()] = $true
    }
    $n = $byRel.Count - $before
    Write-Host ("  {0,-38} {1,6} files" -f (Split-Path $r -Leaf), $files.Count)
    if ($files.Count -eq 0) { Write-Host "     ^ INDEXED NOTHING - references here will read as stale" -ForegroundColor Red }
}
Write-Host ""
Write-Host "  indexed $($byRel.Count) distinct paths, $($byName.Count) distinct names"
Write-Host ""

# Paths look like  Foo/Bar.cs  or  Foo\Bar.cs  or bare  Bar.cs
$ext = 'cs|vb|xaml|csproj|vbproj|sln|ps1|bat|md|xml|json|txt|dll|props|targets|resx'
$pathRx = [regex]"(?<![A-Za-z0-9_./\\-])([A-Za-z0-9_.\\/-]*[A-Za-z0-9_-]\.($ext))\b"

# Entries that describe history on purpose should not be nagged about.
$historyRx = '(?i)RESOLVED|SHIPPED|SUPERSEDED|CLOSED|retired|deleted|removed|no longer exists|used to|formerly|renamed'

$rows    = @()
$checked = 0
$missing = 0

foreach ($f in Get-ChildItem $MemoryDir -Filter *.md | Sort-Object Name) {
    if ($f.Name -eq 'MEMORY.md' -or $f.Name -like 'index_*') { continue }
    $text = [System.IO.File]::ReadAllText($f.FullName)
    $isHistory = $text -match $historyRx

    $hits = $pathRx.Matches($text) | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
    foreach ($h in $hits) {
        # --- NOISE FILTERS ---
        # Tuned 2026-08-21 against a real run: the raw matcher flagged 63
        # candidates of which most were not staleness at all. Each rule below
        # removes a category that CANNOT be a tracked repo file, so removing it
        # cannot hide a real miss.

        # URLs and absolute paths.
        if ($h -match '^(https?:|www\.|//)') { continue }
        if ($h -match '^[A-Za-z]:') { continue }

        # Bare memory cross-links written as filenames.
        if ($h -like '*.md' -and $h -notlike '*/*' -and $h -notlike '*\*') { continue }

        # PLACEHOLDERS AND TEMPLATES. "JJFlexError-YYYYMMDD-HHMMSS.txt",
        # "for-noel/2026-MM-DD-blocker.md", "sprintN-coordination.md",
        # "libhamlib-N.dll", "foo.json", "\path\to\log.txt" - these are
        # illustrations, and a checker that nags about them trains you to
        # ignore it.
        if ($h -match 'YYYY|MM-DD|HHMMSS|\bfoo\b|path.to|sprintN|-N\.dll|<|>|\{|\}') { continue }

        # RUNTIME AND USER-PROFILE STATE, correctly absent from git:
        # AppData config, per-radio autoconnect xml, Claude settings, Dropbox
        # tester drops, saved home layout.
        if ($h -match '(?i)\\config\.xml$|autoConnect|\.claude|settings\.json$|_home_layout|^don\\') { continue }

        $checked++
        $rel  = ($h -replace '/', '\').ToLower()
        $leaf = (Split-Path $rel -Leaf)

        if ($byRel.ContainsKey($rel) -or $byName.ContainsKey($leaf)) { continue }

        $missing++
        $rows += [pscustomobject]@{
            Entry   = $f.Name
            Path    = $h
            History = $isHistory
        }
    }
}

$live = @($rows | Where-Object { -not $_.History })
$hist = @($rows | Where-Object { $_.History })

Write-Host "  path references checked : $checked"
Write-Host "  not found in any tree   : $missing"
Write-Host ""
Write-Host "  in entries NOT marked as history : $($live.Count)   <- look at these" -ForegroundColor $(if ($live.Count) { 'Yellow' } else { 'Green' })
Write-Host "  in entries that read as history  : $($hist.Count)   (probably fine)" -ForegroundColor DarkGray
Write-Host ""

if ($live.Count) {
    Write-Host "STALENESS CANDIDATES" -ForegroundColor Yellow
    $live | Group-Object Entry | Sort-Object Count -Descending | ForEach-Object {
        Write-Host ("  {0}  ({1} missing)" -f $_.Name, $_.Count)
        if ($Detailed) { $_.Group | ForEach-Object { Write-Host "      $($_.Path)" -ForegroundColor DarkGray } }
    }
    Write-Host ""
    Write-Host "These name a file no tree contains. Either the entry is stale, or it" -ForegroundColor DarkGray
    Write-Host "is describing something that was deliberately removed - in which case" -ForegroundColor DarkGray
    Write-Host "stamp it RESOLVED/SHIPPED/SUPERSEDED so this stops flagging it and the" -ForegroundColor DarkGray
    Write-Host "seal archive sweep can find it." -ForegroundColor DarkGray
} else {
    Write-Host "No live entry names a file that has gone missing." -ForegroundColor Green
}

if ($Detailed -and $hist.Count) {
    Write-Host ""
    Write-Host "HISTORY-MARKED (informational)" -ForegroundColor DarkGray
    $hist | Group-Object Entry | Sort-Object Count -Descending | Select-Object -First 15 | ForEach-Object {
        Write-Host ("  {0}  ({1})" -f $_.Name, $_.Count) -ForegroundColor DarkGray
    }
}
