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
#
# ===========================================================================
# SYMBOL EXTRACTION (task #272, built 2026-08-27) - AND WHO OWNS WHAT
# ===========================================================================
# The paragraph above said "file paths and code symbols" from the day this was
# written, and until 2026-08-27 there was no symbol extraction in it at all.
# That is the exact defect this tool exists to catch, sitting in its own
# header: a description promising a capability nobody built. Anyone relying on
# it to catch a renamed symbol was relying on nothing, and got a green result
# for their trouble.
#
# THERE ARE TWO CHECKERS AND THAT IS DELIBERATE. They divide by CORPUS, and
# the division is written here and in the other one's remarks so neither grows
# into the other:
#
#   Radios.Tests/IntegrationPassInstructionTests  ->  CLAUDE.md, MIGRATION.md
#       The repository's own instruction documents. It is an xUnit test scoped
#       by `git ls-files` inside the repo, on purpose: Track F made it
#       git-scoped on 2026-08-26 precisely so its verdict cannot depend on what
#       is lying around a checkout.
#
#   this script                                   ->  the memory tree, and the
#                                                     task register
#       Both live OUTSIDE the repository - under the user profile and in
#       JJFlex-private - so neither is in any repo's `git ls-files`, and the
#       integration pass explicitly excludes `\.claude\` from its corpus.
#
# SO THIS DOES NOT SCAN CLAUDE.md OR MIGRATION.md. Extending the integration
# pass to reach these corpora would mean giving it a filesystem walk into the
# user profile - undoing the thing that made it trustworthy the day before -
# and would make Radios.Tests fail on any machine that is not this one. The
# duplication this project keeps finding is two implementations of ONE job;
# this is one implementation each of two jobs that cannot share a runtime.
#
# THE HIGHER-VALUE HALF IS THE TASK REGISTER, not memory. Tasks carry
# implementation instructions naming specific symbols and line numbers, and
# they are read by agents about to write code. On 2026-08-26 a track renamed
# three earcon voices and reported "six memory entries reference these by
# name"; memory was clean and the stale references were in a TASK. Nobody could
# settle it without a manual grep. The register is generated, so a stale symbol
# there means a stale task - fix the task, then regenerate.
#
# THE PATH CHECK STAYS ON MEMORY ONLY. Its noise filters were tuned against
# memory entries, and turning them loose on a 6,500-line generated register
# would produce a flood on its first run. A checker nobody reads is worse than
# none, so that is a separate decision for a separate day.
#
# HOW A SYMBOL IS RECOGNISED, and why it is narrow: backticks are the strong
# signal - `Coalesce` in backticks is a method, the word "coalesce" in a
# sentence is not. Of those, only bare identifiers with an internal capital and
# at least five characters are checked; a dotted span is checked on its last
# segment, which is the distinctive half. Everything else backticks wrap in
# these documents - shell commands, flags, branch names, key chords, env vars,
# lexicon keys, file paths - is filtered out by shape. Same judgement the
# integration pass reached independently against the same prose style.
#
# THE CORPUS EXCLUDES MARKDOWN, AND THAT IS THE SELF-ERASURE DEFENCE. Track F
# hit this trap in its own sweep: recording `RegisterScope` in a baseline put
# the word into the tree, so the checker read it back and concluded the symbol
# existed. Every phantom would have quietly erased itself by being written
# down. Findings from THIS tool get written into memory entries, task entries
# and after-action reports, all of which are .md - so excluding .md from the
# corpus closes that door by construction rather than by a list somebody has to
# maintain. This script itself is .ps1 and names phantoms in its own comments,
# so it carries the exempt token below for the same reason.
#
# THIS FILE CARRIES BOTH TOOLS' EXEMPT TOKENS, and it earned the second one the
# hard way. Naming a phantom in the header above put that word into a .ps1 --
# which is in the INTEGRATION PASS's corpus as well as this one -- so its
# control went red within minutes of this file being written. That is the two
# checkers' positive controls catching a mistake across a tool boundary,
# working exactly as intended. Both tokens appear verbatim in the
# $exemptTokens array below, which is what makes this file exempt from both.
#
# THE POSITIVE CONTROL RUNS EVERY TIME, not behind a switch. A corpus reader
# that returned nothing would report every symbol as a phantom, which is loud;
# one that returned everything reports none, which is silent, and silence is
# indistinguishable from a clean tree. So each run proves the corpus holds a
# name that is certainly there, lacks one that is certainly not, and that the
# extractor still pulls a symbol out of a fixture. If any of those fail the run
# exits non-zero and says the result cannot be believed.

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
        "C:\Users\nrome\JJFlex-private"   # planning (moved 2026-08-25), AARs, easter eggs
    ),
    # The generated backlog. Symbols named here are read by agents about to
    # write code, which is why it is swept at all - see the header.
    [string]   $Register  = "C:\Users\nrome\JJFlex-private\planning\active\task-register.md",
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

# --- the symbol corpus, gathered on the same walk -----------------------------
# Code-ish files only. Markdown is deliberately absent: see the self-erasure
# paragraph in the header. A file containing the exempt token is skipped
# entirely, which is how this script's own comments -- which name phantoms on
# purpose -- stay out of the evidence.
$codeExt = @('.cs','.vb','.xaml','.csproj','.vbproj','.props','.targets','.sln',
             '.json','.xml','.resx','.config','.bat','.ps1','.psm1','.psd1',
             '.py','.js','.ts','.yml','.yaml','.nsi','.txt','.h','.c','.cpp')
# TWO TOKENS, AND EACH ONE WAS EARNED BY A FAILURE.
#
# LITERALS, NOT CONCATENATIONS. The first draft built the first token by joining
# two halves so the assembled word would not appear in this file -- which meant
# this file was NOT exempt from its own corpus, so the phantoms named in the
# header above were read back as real identifiers. Written as literals, the
# file contains them and the tokeniser skips it. Exempting the tool from its own
# evidence is the point.
#
# THE SECOND TOKEN IS THE INTEGRATION PASS'S, and it is honoured here because
# the two tools' corpora overlap. The integration pass's baseline and test files
# record known phantoms on purpose and mark themselves exempt for exactly that
# reason -- but they are .cs, so THIS tool read them and concluded RegisterScope
# exists. Proved 2026-08-27: a fixture naming it came back clean until this
# array had both entries.
#
# The general rule, worth stating because the next tool will hit it too: a file
# marked corpus-exempt by ANY of these checkers is a file that writes phantom
# names down deliberately, and no checker should treat it as evidence.
[string[]] $exemptTokens = @('DRIFTCHECK_CORPUS_EXEMPT', 'INTEGRATION_PASS_CORPUS_EXEMPT')
$corpusPaths = [System.Collections.Generic.List[string]]::new()

# THE TOKENISER IS COMPILED, and that is not premature optimisation - it is the
# difference between this being a seal step and not. Tokenising ~20,000 files
# with a PowerShell loop over MatchCollection took 77 seconds; the same work in
# C# takes a couple. #272 says "keep it fast; the seal's value depends on it
# staying cheap enough to run every time", and a check that costs a minute and a
# quarter is a check that starts getting skipped.
#
# Deliberately plain C# so it compiles under Windows PowerShell 5.1's CodeDom as
# well as PowerShell 7's Roslyn. Whoever runs the seal should not have to know
# which shell they are in.
if (-not ('JJFlexDriftCorpus' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;

public static class JJFlexDriftCorpus
{
    public static int FilesRead;

    public static HashSet<string> Tokenize(IEnumerable<string> paths, string[] exemptTokens)
    {
        HashSet<string> tokens = new HashSet<string>(StringComparer.Ordinal);
        string[] exempt = exemptTokens ?? new string[0];
        FilesRead = 0;

        foreach (string path in paths)
        {
            string text;
            try { text = File.ReadAllText(path); }
            catch { continue; }

            bool skip = false;
            foreach (string token in exempt)
                if (text.IndexOf(token, StringComparison.Ordinal) >= 0) { skip = true; break; }
            if (skip) continue;

            FilesRead++;

            int i = 0;
            int n = text.Length;
            while (i < n)
            {
                char c = text[i];
                if (c == '_' || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
                {
                    int start = i;
                    i++;
                    while (i < n)
                    {
                        char d = text[i];
                        if (d == '_' || (d >= '0' && d <= '9') ||
                            (d >= 'A' && d <= 'Z') || (d >= 'a' && d <= 'z')) i++;
                        else break;
                    }
                    tokens.Add(text.Substring(start, i - start));
                }
                else i++;
            }
        }
        return tokens;
    }
}
'@
}

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

        if ($codeExt -contains [System.IO.Path]::GetExtension($rel).ToLower()) {
            $corpusPaths.Add((Join-Path $r $rel))
        }
    }
    $n = $byRel.Count - $before
    Write-Host ("  {0,-38} {1,6} files" -f (Split-Path $r -Leaf), $files.Count)
    if ($files.Count -eq 0) { Write-Host "     ^ INDEXED NOTHING - references here will read as stale" -ForegroundColor Red }
}
$corpusSw    = [System.Diagnostics.Stopwatch]::StartNew()
$symbols     = [JJFlexDriftCorpus]::Tokenize($corpusPaths, $exemptTokens)
$corpusFiles = [JJFlexDriftCorpus]::FilesRead
$corpusSw.Stop()

Write-Host ""
Write-Host "  indexed $($byRel.Count) distinct paths, $($byName.Count) distinct names"
Write-Host ("  read $corpusFiles code files, $($symbols.Count) distinct identifiers in {0:0.0}s" -f $corpusSw.Elapsed.TotalSeconds)
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

# ===========================================================================
# SYMBOL CHECK (task #272)
# ===========================================================================

# Backticks are the strong signal. Everything below narrows from there by
# SHAPE, never by a list of known-good words - a list is one more thing that
# goes stale, which is the failure this file is about.
$tickRx = [regex]'`([^`\r\n]+)`'

# A file extension at the end means the path check owns it, not this one.
$looksLikePath = [regex]'(?i)\.(cs|vb|xaml|csproj|vbproj|sln|ps1|psm1|bat|md|xml|json|txt|dll|props|targets|resx|nsi|py|js|ts|yml|yaml|chm|exe|pdb|zip)$'

function Get-SymbolCandidate([string] $span) {
    $s = $span.Trim()

    # A call written with its parentheses is still a symbol.
    if ($s.EndsWith('()')) { $s = $s.Substring(0, $s.Length - 2) }

    # Anything with shell, path, chord or expression punctuation in it is not a
    # bare identifier: `Ctrl+J`, `git ls-files`, `--publish`, `Radios\Fixer`,
    # `x => x.Foo`, `<Version>`.
    if ($s -match '[\s/\\+\-*<>{}()\[\]%$,;:=''"|&#!?@~^]') { return $null }

    # Lexicon and settings keys are dotted and entirely lower case
    # (`audio.fixer.speak_now`). They belong to the lexicon coverage test.
    if ($s -cmatch '^[a-z0-9_.]+$') { return $null }

    if ($looksLikePath.IsMatch($s)) { return $null }

    # A dotted span is checked on its LAST segment - `TransmitSafety.Reflected`
    # is distinctive in its member, and the type half is usually named
    # elsewhere anyway.
    if ($s.Contains('.')) { $s = ($s -split '\.')[-1] }

    if ($s -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') { return $null }
    if ($s.Length -lt 5)                         { return $null }
    if ($s -cnotmatch '[A-Z]')                   { return $null }   # prose
    if ($s -cmatch '^[A-Z0-9_]+$')               { return $null }   # SHOUTED / env var

    return $s
}

# --- POSITIVE CONTROL. Runs every time; see the header. ----------------------
# Three separate things can silently break: the corpus reader, the corpus
# scope, and the extractor. Each gets its own proof, and the tokens are chosen
# so that no plausible edit to the tree makes the wrong one pass.
$controlFailures = @()

if ($symbols.Count -lt 20000) {
    $controlFailures += "only $($symbols.Count) identifiers were read out of the trees, which is far too few - the corpus reader has broken and everything would look like a phantom"
}
foreach ($known in @('FlexBase', 'KeyScope', 'SpeechArbiter')) {
    if (-not $symbols.Contains($known)) {
        $controlFailures += "the corpus does not contain '$known', which is certainly in the tree - so a real symbol would be reported as missing"
    }
}
# This token exists nowhere but this line, and this file is corpus-exempt, so a
# corpus that "contains" it is a corpus that is reading its own exclusions.
$neverToken = 'Zzq' + 'Phantom' + 'Absentia'
if ($symbols.Contains($neverToken)) {
    $controlFailures += "the corpus contains '$neverToken', which nothing defines - the exempt-token exclusion is not working and findings would erase themselves"
}
# And the extractor itself, against a fixture rather than against live data.
if ((Get-SymbolCandidate 'ReflectedWarnPercent') -ne 'ReflectedWarnPercent') {
    $controlFailures += "the extractor no longer recognises a plain PascalCase identifier"
}
if ((Get-SymbolCandidate 'TransmitSafety.ReflectedWarnPercent') -ne 'ReflectedWarnPercent') {
    $controlFailures += "the extractor no longer reduces a dotted span to its member"
}
foreach ($noise in @('Ctrl+J', 'JJFLEX_CONFIG_DIR', 'audio.fixer.speak_now', 'KeyCommands.cs', 'git ls-files')) {
    if ((Get-SymbolCandidate $noise) -ne $null) {
        $controlFailures += "the extractor accepted '$noise', which is not a symbol - the run would drown in noise"
    }
}

Write-Host ""
Write-Host "=========================================================="
Write-Host "SYMBOL CHECK  (memory entries + the task register)"
Write-Host "=========================================================="

if ($controlFailures.Count) {
    Write-Host ""
    Write-Host "POSITIVE CONTROL FAILED - do not believe this run." -ForegroundColor Red
    $controlFailures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    Write-Host ""
    Write-Host "A symbol sweep that cannot find what is there reports everything as" -ForegroundColor DarkGray
    Write-Host "missing; one that finds everything reports nothing, and nothing reads" -ForegroundColor DarkGray
    Write-Host "exactly like a clean tree. Neither result is usable." -ForegroundColor DarkGray
    exit 3
}
Write-Host "  positive control: corpus holds known symbols, lacks a known phantom,"
Write-Host "                    extractor still recognises and still filters."

# --- the corpora being checked ------------------------------------------------
$docs = @()
foreach ($f in Get-ChildItem $MemoryDir -Filter *.md | Sort-Object Name) {
    if ($f.Name -eq 'MEMORY.md' -or $f.Name -like 'index_*') { continue }
    $docs += [pscustomobject]@{ Label = $f.Name; Path = $f.FullName }
}
if ($Register -and (Test-Path $Register)) {
    $docs += [pscustomobject]@{ Label = 'task-register.md'; Path = $Register }
} elseif ($Register) {
    Write-Host "  WARNING: no task register at $Register - the higher-value half of" -ForegroundColor Yellow
    Write-Host "           this sweep did not run." -ForegroundColor Yellow
}

$symRows    = @()
$symChecked = 0

foreach ($d in $docs) {
    $text = [System.IO.File]::ReadAllText($d.Path)

    # The register is one file holding hundreds of independent entries, so a
    # single "renamed" anywhere in it would exempt the lot. Split it into its
    # `### #nnn` sections and judge each on its own words; ordinary memory
    # entries are judged whole, as the path check does.
    if ($d.Label -eq 'task-register.md') {
        $sections = [regex]::Split($text, '(?m)^(?=###\s)')
    } else {
        $sections = @($text)
    }

    foreach ($section in $sections) {
        if (-not $section.Trim()) { continue }
        $isHistory = $section -match $historyRx
        $label = $d.Label
        if ($d.Label -eq 'task-register.md') {
            $m = [regex]::Match($section, '(?m)^###\s+(#\d+)')
            if ($m.Success) { $label = 'task ' + $m.Groups[1].Value }
        }

        $spans = $tickRx.Matches($section) | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
        foreach ($span in $spans) {
            $cand = Get-SymbolCandidate $span
            if (-not $cand) { continue }

            # Counted before the history filter, deliberately: counting only
            # what survives would let a filter that swallowed everything look
            # like a document with nothing in it.
            $symChecked++
            if ($isHistory) { continue }
            if ($symbols.Contains($cand)) { continue }

            $symRows += [pscustomobject]@{ Entry = $label; Symbol = $span; Resolved = $cand }
        }
    }
}

Write-Host ""
Write-Host "  documents swept          : $($docs.Count)"
Write-Host "  symbol references checked: $symChecked"
Write-Host "  named nowhere in the code: $($symRows.Count)" -ForegroundColor $(if ($symRows.Count) { 'Yellow' } else { 'Green' })
Write-Host ""

if ($symRows.Count) {
    Write-Host "SYMBOL STALENESS CANDIDATES" -ForegroundColor Yellow
    $symRows | Group-Object Entry | Sort-Object Count -Descending | ForEach-Object {
        Write-Host ("  {0}  ({1})" -f $_.Name, $_.Count)
        if ($Detailed) {
            $_.Group | ForEach-Object { Write-Host "      $($_.Symbol)" -ForegroundColor DarkGray }
        }
    }
    Write-Host ""
    Write-Host "CANDIDATES, NOT ERRORS - the same discipline the path check has." -ForegroundColor DarkGray
    Write-Host "A name nothing defines can mean the document is stale, or that it is" -ForegroundColor DarkGray
    Write-Host "correctly recording something that was removed, or that the symbol" -ForegroundColor DarkGray
    Write-Host "lives in an estate this machine does not hold. Judge, then either fix" -ForegroundColor DarkGray
    Write-Host "the document or stamp it RESOLVED/SHIPPED/SUPERSEDED." -ForegroundColor DarkGray
    Write-Host ""
    Write-Host "DO NOT CHASE THE COUNT TO ZERO. The number to watch is a NEW entry" -ForegroundColor DarkGray
    Write-Host "appearing, not the total. A stale symbol in a TASK is the expensive" -ForegroundColor DarkGray
    Write-Host "one: fix the task, then re-run export-task-register.ps1." -ForegroundColor DarkGray
} else {
    Write-Host "No live document names a symbol the code does not define." -ForegroundColor Green
}
