#requires -Version 5.1
<#
build-help.ps1 -- builds the JJ Flexible help (#826, #834).

One build, two outputs, both from docs\help\outline.md and the pages under
docs\help\md\:

  - the compiled help, JJFlexRadio.chm, which F1 and the Help menu open today
    and which keeps shipping until the new help window exists;
  - the HTML help, docs\help\html\, the help's future: the pages, a Contents
    page, an Index page and an offline search. Not packaged yet.

build-help.bat is a thin wrapper around this script, so build-debug.bat,
build-installers.bat and install.bat's guard keep calling what they always
called.

What it does, in order, and every check is a FAILURE, never a warning:
  1. checks pandoc (with Lua), hhc.exe and help-stamp.ps1 are there;
  2. copies docs\CHANGELOG.md to md\WhatsNew.md;
  3. reads the outline and the page tree and checks them against each other:
     every page is in the outline, every page line names its real folder and
     case, a planned page does not exist yet, names are unique ignoring case,
     one level of folders, PascalCase names;
  4. converts every page with pandoc and help-links.lua, which resolves bare
     page links and fails on a dead link, a wrong case, a missing #anchor, a
     .htm link, a non-page target, and a page without exactly one h1;
  5. refuses heading ids that break when a heading is added above them;
  6. writes both outputs: pages\ plus the generated jjflex-help.hhp, toc.hhc
     and index.hhk for the compiled help, and html\ for the HTML help;
  7. compiles the CHM and fails on any "HHCnnnn: Error" line or exit code 0
     (#830: hhc writes a complete CHM even when it reports an error);
  8. stamps the CHM with a hash of its sources (help-stamp.ps1).

Usage:
  build-help.ps1                 build both outputs
  build-help.ps1 -NoChm          build and check everything but the CHM
                                 (no hhc, no stamp); the HTML help is built
  build-help.ps1 -CompileOnly    compile the generated jjflex-help.hhp as it
                                 stands, and never stamp. For proving the hhc
                                 checks against a planted project file; a CHM
                                 built this way will not package.
  -HhcPath <path>                  use this compiler instead of HTML Help
                                 Workshop's hhc.exe. For proving the exit-code
                                 checks with a stand-in; never for a real build.
#>

param(
  [string] $HelpDir = $PSScriptRoot,
  [switch] $NoChm,
  [switch] $CompileOnly,
  [string] $HhcPath = '',
  [switch] $Fallback
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'help-generate.ps1')

$helpDir   = (Resolve-Path -LiteralPath $HelpDir).Path.TrimEnd('\')
$mdDir     = Join-Path $helpDir 'md'
$outline   = Join-Path $helpDir 'outline.md'
$filter    = Join-Path $helpDir 'help-links.lua'
$webDir    = Join-Path $helpDir 'web'
$workDir   = Join-Path $helpDir 'build'
$pagesDir  = Join-Path $helpDir 'pages'
$htmlDir   = Join-Path $helpDir 'html'
$chmPath   = Join-Path $helpDir 'JJFlexRadio.chm'
$prevPath  = Join-Path $helpDir 'JJFlexRadio.chm.prev'
$stampPath = Join-Path $helpDir 'JJFlexRadio.chm.stamp'
$changelog = Join-Path (Split-Path -Parent $helpDir) 'CHANGELOG.md'

function Stop-Help([string[]] $Lines) {
  Write-Host ''
  foreach ($l in $Lines) { Write-Host $l }
  Write-Host ''
  exit 1
}

function Stop-IfFailed([string] $Phase) {
  if ($script:HelpFailures.Count -eq 0) { return }
  Write-Host ''
  Write-Host ("FAILED: " + $Phase + ' - ' + $script:HelpFailures.Count + ' problem(s):')
  foreach ($f in $script:HelpFailures) { Write-Host ('  ' + $f) }
  Write-Host ''
  exit 1
}

# ---------------------------------------------------------------------------
# 7 and 8, as a function, because -CompileOnly runs them alone.
# ---------------------------------------------------------------------------
function Invoke-ChmCompile([bool] $Stamp) {
  # -------------------------------------------------------------------------
  # 7. Compile the CHM (#830)
  # -------------------------------------------------------------------------
  # The previous CHM is moved aside first, so that afterwards "the file is
  # there" can only mean hhc just wrote it. On failure it is put back, unstamped.
  #
  # "hhc wrote a file" is NOT "hhc succeeded". Measured 2026-10-26: a page
  # missing from [FILES] (HHC5003) and a contents entry pointing at a missing
  # page (HHC5013) both print an error AND write a complete CHM. So the build
  # fails on either sign: an "HHCnnnn: Error" line, or exit code 0, which is
  # inverted rather than useless (1 is a clean compile, 0 means an error was
  # printed). hhc checks no links, anchors or index targets; the filter and the
  # generator above are what check those.
  if (Test-Path -LiteralPath $prevPath) { Remove-Item -LiteralPath $prevPath -Force }
  if (Test-Path -LiteralPath $chmPath) { Move-Item -LiteralPath $chmPath -Destination $prevPath -Force }
  if (Test-Path -LiteralPath $stampPath) { Remove-Item -LiteralPath $stampPath -Force }

  Write-Host ''
  Write-Host 'Compiling the CHM ...'
  # 'Continue' for this one call: Windows PowerShell turns any line a native
  # program writes to stderr into a terminating error under 'Stop', and hhc's
  # output is exactly what has to be read here, not thrown.
  $ErrorActionPreference = 'Continue'
  $hhcOut = @(& $hhc (Join-Path $helpDir 'jjflex-help.hhp') 2>&1 | ForEach-Object { "$_" })
  $hhcExit = $LASTEXITCODE
  $ErrorActionPreference = 'Stop'
  foreach ($l in $hhcOut) { if ($l.Trim() -ne '') { Write-Host ('  ' + $l) } }

  $why = $null
  if (@($hhcOut | Where-Object { $_ -match 'HHC\d+:\s*Error' }).Count -gt 0) { $why = 'hhc.exe printed an error - see the HHC lines above.' }
  elseif ($hhcExit -eq 0) { $why = 'hhc.exe exited 0, which it does only after an error.' }
  elseif (-not (Test-Path -LiteralPath $chmPath)) { $why = 'hhc.exe produced no output file.' }

  if ($why) {
    # An errored compile still writes a CHM. Remove it so it cannot ship.
    if (Test-Path -LiteralPath $chmPath) { Remove-Item -LiteralPath $chmPath -Force }
    $lines = @("FAILED: CHM compilation failed - $why")
    if (Test-Path -LiteralPath $prevPath) {
      Move-Item -LiteralPath $prevPath -Destination $chmPath -Force
      $lines += @('        The previous CHM has been restored. It is now STALE and',
                  '        unstamped, so packaging will refuse it until this succeeds.')
    }
    Stop-Help $lines
  }
  if (Test-Path -LiteralPath $prevPath) { Remove-Item -LiteralPath $prevPath -Force }

  # -------------------------------------------------------------------------
  # 8. Stamp (#556)
  # -------------------------------------------------------------------------
  if (-not $Stamp) {
    Write-Host 'SUCCESS: JJFlexRadio.chm compiled. NOT stamped (-CompileOnly), so packaging will refuse it.'
    return
  }
  & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $helpDir 'help-stamp.ps1') -Mode Write -HelpDir $helpDir
  if ($LASTEXITCODE -ne 0) {
    Stop-Help @('ERROR: the CHM was built but the freshness stamp could not be written.')
  }
  Write-Host ('SUCCESS: JJFlexRadio.chm built, ' + (Get-Item -LiteralPath $chmPath).Length + ' bytes.')
}

# ---------------------------------------------------------------------------
# 1. Tools
# ---------------------------------------------------------------------------
if ($Fallback) {
  Stop-Help @(
    'ERROR: /fallback is retired. The regex converter (convert-md.ps1) cannot',
    'resolve links between folders, read keyword blocks or write the sidecars',
    'the Contents, Index and search are built from, so it can no longer produce',
    'a help that works at all. Install pandoc instead:',
    '  winget install --id JohnMacFarlane.Pandoc'
  )
}

$pandoc = Get-Command pandoc -ErrorAction SilentlyContinue
if (-not $pandoc) {
  Stop-Help @(
    'ERROR: pandoc is not installed, and it is the help''s Markdown converter.',
    '  Install it with:  winget install --id JohnMacFarlane.Pandoc'
  )
}
$pandocVersion = (& $pandoc.Source --version | Out-String)
if ($pandocVersion -notmatch '\+lua') {
  Stop-Help @('ERROR: this pandoc was built without Lua, and the help build needs its Lua filter.',
              ('  ' + ($pandocVersion -split "`n")[0].Trim()))
}

$hhc = if ($HhcPath) { $HhcPath } else { Join-Path ${env:ProgramFiles(x86)} 'HTML Help Workshop\hhc.exe' }
if (-not $NoChm) {
  if (-not (Test-Path -LiteralPath $hhc)) {
    Stop-Help @(
      "ERROR: HTML Help Workshop not found at $hhc",
      'Install from: https://web.archive.org/web/2024/https://www.microsoft.com/en-us/download/details.aspx?id=21138'
    )
  }
  if (-not (Test-Path -LiteralPath (Join-Path $helpDir 'help-stamp.ps1'))) {
    Stop-Help @('ERROR: docs\help\help-stamp.ps1 is missing. A CHM built without it',
                'cannot be stamped, and an unstamped CHM will not package.')
  }
}
foreach ($need in @($filter, $webDir)) {
  if (-not (Test-Path -LiteralPath $need)) { Stop-Help @("ERROR: $need is missing; the help build cannot run without it.") }
}

if ($CompileOnly) {
  if ($NoChm) { Stop-Help @('ERROR: -CompileOnly and -NoChm contradict each other.') }
  Invoke-ChmCompile -Stamp $false
  exit 0
}

# ---------------------------------------------------------------------------
# 2. What's New, from the changelog
# ---------------------------------------------------------------------------
# The changelog is the user record and the single source of What's New. It is
# copied byte for byte. A link in it the help cannot follow (CHANGELOG-legacy.md,
# a file beside the changelog, not a help page) is not the build's to fix: the
# filter keeps its words, drops the link, and prints a notice naming it.
$whatsNew = Join-Path $mdDir 'WhatsNew.md'
if (Test-Path -LiteralPath $changelog) {
  Write-Host 'Importing docs\CHANGELOG.md as md\WhatsNew.md ...'
  Copy-Item -LiteralPath $changelog -Destination $whatsNew -Force
}

# ---------------------------------------------------------------------------
# 3. Outline against tree
# ---------------------------------------------------------------------------
Write-Host 'Checking the outline against the pages ...'
$entries = @(Read-HelpOutline $outline)
$tree = Get-HelpPageTree $mdDir
Test-OutlineAgainstTree $entries $tree
Stop-IfFailed 'the outline and the pages disagree'

$plannedNames = @($entries | Where-Object { $_.Planned } | ForEach-Object { $_.Name })

# ---------------------------------------------------------------------------
# 4. Convert every page: one pandoc per page, several at once
# ---------------------------------------------------------------------------
if (Test-Path -LiteralPath $workDir) { Remove-Item -LiteralPath $workDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $workDir | Out-Null

$jobs = New-Object System.Collections.Generic.List[object]
foreach ($p in $tree.Pages) {
  $body = Join-Path $workDir ('body\' + ($p.Rel -replace '\.md$', '.htm').Replace('/', '\'))
  $side = Join-Path $workDir ('side\' + ($p.Rel -replace '\.md$', '.json').Replace('/', '\'))
  New-Item -ItemType Directory -Force -Path (Split-Path -Parent $body) | Out-Null
  New-Item -ItemType Directory -Force -Path (Split-Path -Parent $side) | Out-Null
  $jobs.Add([pscustomobject]@{
    Page = $p; Body = $body; Side = $side
    Args = (Get-PandocArguments $p.FullName $mdDir $filter $body $side $plannedNames)
    Process = $null; StdErr = $null; Exit = $null; Err = ''
  })
}

Write-Host ('Converting ' + $jobs.Count + ' pages with pandoc ...')
$parallel = [Math]::Max(2, [Environment]::ProcessorCount)
$pending = New-Object System.Collections.Generic.Queue[object]
foreach ($j in $jobs) { $pending.Enqueue($j) }
$running = New-Object System.Collections.Generic.List[object]
while ($pending.Count -gt 0 -or $running.Count -gt 0) {
  while ($pending.Count -gt 0 -and $running.Count -lt $parallel) {
    $j = $pending.Dequeue()
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $pandoc.Source
    $psi.Arguments = $j.Args
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardError = $true
    $psi.RedirectStandardOutput = $true
    $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
    $j.Process = [System.Diagnostics.Process]::Start($psi)
    $j.StdErr = $j.Process.StandardError.ReadToEndAsync()
    [void]$j.Process.StandardOutput.ReadToEndAsync()
    $running.Add($j)
  }
  foreach ($j in $running.ToArray()) {
    if ($j.Process.HasExited) {
      $j.Process.WaitForExit()
      $j.Exit = $j.Process.ExitCode
      $j.Err = $j.StdErr.Result
      $j.Process.Dispose()
      [void]$running.Remove($j)
    }
  }
  if ($running.Count -gt 0) { Start-Sleep -Milliseconds 15 }
}

foreach ($j in $jobs) {
  $errLines = @(($j.Err -split "`r?`n") | Where-Object { $_.Trim() -ne '' })
  if ($j.Exit -ne 0) {
    # The filter's own message is the one line worth reading; pandoc follows
    # it with a Lua traceback an operator should not have to listen to.
    $mine = @($errLines | Where-Object { $_ -like 'help-links:*' })
    if ($mine.Count -gt 0) { foreach ($m in $mine) { Add-HelpFailure ($m -replace '^help-links:\s*', '') } }
    else { Add-HelpFailure ("md/$($j.Page.Rel): pandoc failed (exit $($j.Exit)): " + ($errLines -join ' ')) }
  } else {
    foreach ($w in $errLines) { Write-Host ("  pandoc, md/$($j.Page.Rel): " + $w) }
    if (-not (Test-Path -LiteralPath $j.Body) -or -not (Test-Path -LiteralPath $j.Side)) {
      Add-HelpFailure "md/$($j.Page.Rel): pandoc exited cleanly but wrote no page or no sidecar."
    }
  }
}
Stop-IfFailed 'one or more pages did not convert'

# ---------------------------------------------------------------------------
# 5. Sidecars: titles, keywords, ids
# ---------------------------------------------------------------------------
# Sidecars are read in outline order, so the Index ties and the search data
# come out in a stable order from build to build.
$sidecarByPage = @{}
foreach ($j in $jobs) { $sidecarByPage[$j.Page.Rel] = Read-HelpSidecar $j.Side }
$sidecars = @()
foreach ($e in $entries) { if ($e.Path -and -not $e.Planned) { $sidecars += $sidecarByPage[$e.Path] } }
Test-HelpSidecars $sidecars $entries
Stop-IfFailed 'a page''s headings, title or keywords cannot be used as they are'

# ---------------------------------------------------------------------------
# 6. Write both outputs
# ---------------------------------------------------------------------------
$contentsTree = Get-ContentsTree $entries
$terms = Get-IndexTerms $sidecars
$bodyOf = @{}
foreach ($j in $jobs) { $bodyOf[$j.Page.Rel] = [System.IO.File]::ReadAllText($j.Body, [System.Text.Encoding]::UTF8) }

# The compiled help's pages. Rebuilt from empty every time, so a page that
# was renamed or deleted cannot linger in pages\ and be compiled anyway.
if (Test-Path -LiteralPath $pagesDir) { Remove-Item -LiteralPath $pagesDir -Recurse -Force }
foreach ($sc in $sidecars) {
  $out = Join-Path $pagesDir (($sc.Page -replace '\.md$', '.htm').Replace('/', '\'))
  Write-Utf8File $out (Get-ChmPageHtml $sc.Title $sc.Page $bodyOf[$sc.Page])
}
Write-AnsiFile (Join-Path $helpDir 'jjflex-help.hhp') (Get-HhpText $entries)
Write-AnsiFile (Join-Path $helpDir 'toc.hhc') (Get-HhcText $contentsTree)
Write-AnsiFile (Join-Path $helpDir 'index.hhk') (Get-HhkText $terms)

# The HTML help.
if (Test-Path -LiteralPath $htmlDir) { Remove-Item -LiteralPath $htmlDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $htmlDir | Out-Null
Copy-Item -LiteralPath (Join-Path $helpDir 'style.css') -Destination $htmlDir
foreach ($a in @('help.js', 'help-search.js', 'help-web.css')) {
  Copy-Item -LiteralPath (Join-Path $webDir $a) -Destination $htmlDir
}
$msDir = Join-Path $htmlDir 'minisearch'
New-Item -ItemType Directory -Force -Path $msDir | Out-Null
foreach ($a in @('minisearch-7.2.0.js', 'LICENSE.txt', 'README.txt')) {
  Copy-Item -LiteralPath (Join-Path $webDir ('minisearch\' + $a)) -Destination $msDir
}
foreach ($sc in $sidecars) {
  $out = Join-Path $htmlDir (($sc.Page -replace '\.md$', '.htm').Replace('/', '\'))
  Write-Utf8File $out (Get-WebPageHtml $sc.Title (Get-UpPrefix $sc.Page) $bodyOf[$sc.Page] @())
}
$generated = @{
  'Contents.htm' = (Get-WebPageHtml 'Contents' '' (Get-ContentsBody $contentsTree) @())
  'Index.htm'    = (Get-WebPageHtml 'Index' '' (Get-IndexBody $terms) @())
  'Search.htm'   = (Get-WebPageHtml 'Search' '' (Get-SearchBody) @('minisearch/minisearch-7.2.0.js', 'search-data.js', 'help-search.js'))
}
foreach ($k in $generated.Keys) {
  if ($generated[$k] -match '<table') { Add-HelpFailure "html\$k would contain a table; the help's generated pages never do." }
  Write-Utf8File (Join-Path $htmlDir $k) $generated[$k]
}
Write-Utf8File (Join-Path $htmlDir 'search-data.js') (Get-SearchDataJs $sidecars)
Stop-IfFailed 'the generated pages'

$plannedEntries = @($entries | Where-Object { $_.Planned })
Write-Host ('HTML help written to docs\help\html: ' + $sidecars.Count + ' pages, plus Contents, Index and Search.')

if ($NoChm) {
  Write-Host 'Skipping the compiled help (-NoChm).'
} else {
  Invoke-ChmCompile -Stamp $true
}

if ($plannedEntries.Count -gt 0) {
  Write-Host ''
  Write-Host ('Planned, in the outline but not written yet: ' + $plannedEntries.Count + ' pages.')
  foreach ($e in $plannedEntries) { Write-Host ('  ' + $e.Path) }
}
exit 0
