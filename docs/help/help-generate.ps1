#requires -Version 5.1
<#
help-generate.ps1 -- the help build's generator, dot-sourced by build-help.ps1.

Everything here decides what the built help CONTAINS: how the outline is read,
which checks fail the build, how a page is wrapped, and what the Contents,
Index and search are made of. help-stamp.ps1 hashes this file for that reason,
together with outline.md and help-links.lua.

No Markdown is parsed here. Pandoc is the only Markdown parser in the build;
this file reads the outline (its own small format, documented at the top of
outline.md) and the JSON sidecar the filter writes for each page.

Compatible with Windows PowerShell 5.1, because build-help.bat and every
packaging script call `powershell`, not `pwsh`.
#>

Set-StrictMode -Version 2.0

# ---------------------------------------------------------------------------
# Failures. Every check adds to one list and the driver stops at the end of a
# phase, so one build reports every problem it can see rather than the first.
# ---------------------------------------------------------------------------
$script:HelpFailures = New-Object System.Collections.Generic.List[string]

function Add-HelpFailure([string] $Message) {
  $script:HelpFailures.Add($Message)
}

# ---------------------------------------------------------------------------
# Names the HTML help reserves at its root, and the id its skip link targets.
# A page by one of these names would overwrite a generated page.
# ---------------------------------------------------------------------------
$script:ReservedPageNames = @('Contents.md', 'Index.md', 'Search.md')
$script:MainContentId = 'jjf-main-help'

# ---------------------------------------------------------------------------
# Heading ids that break as soon as a heading is added above them, and that
# the build therefore refuses (plan 4.6): an all-numeric heading's "section",
# and a "-N" pandoc added to tell two equal headings apart.
#
# The one page that trips it today is listed here with its reason. The list
# can only shrink: an entry that no longer matches anything fails the build,
# so it is removed the day the page is fixed.
# ---------------------------------------------------------------------------
$script:AnchorExceptions = @{
}

# ---------------------------------------------------------------------------
# Small helpers
# ---------------------------------------------------------------------------
function ConvertTo-HtmlText([string] $s) {
  return [System.Net.WebUtility]::HtmlEncode($s)
}

function Write-Utf8File([string] $Path, [string] $Text) {
  $dir = Split-Path -Parent $Path
  if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
  [System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding($false)))
}

# The compiled help's project, contents and index files are read by hhc in
# the ANSI code page. Write them in Windows-1252, and any character outside it
# as a numeric reference, so a heading with an em dash cannot turn into a
# question mark in the Contents.
function Write-AnsiFile([string] $Path, [string] $Text) {
  $enc = [System.Text.Encoding]::GetEncoding(1252)
  $sb = New-Object System.Text.StringBuilder
  foreach ($ch in $Text.ToCharArray()) {
    $code = [int]$ch
    if ($code -lt 128) { [void]$sb.Append($ch); continue }
    $round = $enc.GetString($enc.GetBytes([string]$ch))
    if ($round -eq [string]$ch) { [void]$sb.Append($ch) } else { [void]$sb.Append('&#' + $code + ';') }
  }
  [System.IO.File]::WriteAllText($Path, $sb.ToString(), $enc)
}

# A value for a sitemap <param>. The compiled help has always carried names
# like "Installation & First Connection" raw and the viewer shows them as
# written, so only the double quote, which would end the attribute, is refused.
function Test-SitemapText([string] $What, [string] $s) {
  if ($s.Contains('"')) {
    Add-HelpFailure ("$What contains a double quote, which the compiled help's Contents and Index cannot carry: " + $s)
  }
}

function Test-PascalName([string] $Segment) {
  return ($Segment -cmatch '^[A-Z][A-Za-z0-9]*$')
}

# ConvertFrom-Json in 5.1 turns an empty Lua table into an object with no
# properties, and a one-element array into a scalar. This gives an array back.
function Get-JsonArray($Value) {
  if ($null -eq $Value) { return @() }
  if ($Value -is [System.Management.Automation.PSCustomObject] -and @($Value.PSObject.Properties).Count -eq 0) { return @() }
  return @($Value)
}

# ---------------------------------------------------------------------------
# The outline
# ---------------------------------------------------------------------------
function Read-HelpOutline([string] $OutlinePath) {
  if (-not (Test-Path -LiteralPath $OutlinePath)) {
    Add-HelpFailure "The outline is missing: $OutlinePath. It names every page; without it there is no help to build."
    return @()
  }
  $lines = [System.IO.File]::ReadAllLines($OutlinePath)
  $entries = New-Object System.Collections.Generic.List[object]
  $prevLevel = 0
  $last = $null
  $lastWasHeading = $false
  $pathRx = '^(?<path>(?:(?<folder>[A-Za-z0-9]+)/)?(?<name>[A-Za-z0-9]+\.md))(?:#(?<anchor>[A-Za-z0-9_.:-]+))?$'

  for ($i = 0; $i -lt $lines.Length; $i++) {
    $n = $i + 1
    $line = $lines[$i].TrimEnd()
    if ($i -eq 0) { $line = $line.TrimStart([char]0xFEFF) }

    if ($line.Trim() -eq '') { $lastWasHeading = $false; continue }
    if ($line.StartsWith('>')) { $lastWasHeading = $false; continue }

    if ($line -match '^(?<hashes>#{1,6})\s+(?<text>.+?)\s*$') {
      $level = $Matches['hashes'].Length
      $text = $Matches['text']
      if ($prevLevel -eq 0 -and $level -ne 1) {
        Add-HelpFailure "outline.md line ${n}: the first entry must be a level-one heading (# ...), found level $level."
      } elseif ($level -gt $prevLevel + 1) {
        Add-HelpFailure "outline.md line ${n}: '$text' is level $level straight after level $prevLevel; a heading can only go one level deeper than the one before it."
      }
      Test-SitemapText "outline.md line $n" $text
      $last = [pscustomobject]@{
        Line = $n; Level = $level; Title = $text
        Path = $null; Name = $null; Folder = $null; Anchor = $null; Planned = $false
      }
      $entries.Add($last)
      $prevLevel = $level
      $lastWasHeading = $true
      continue
    }

    $planned = $false
    $rest = $line
    if ($line -cmatch '^Planned:\s+(?<p>.+)$') { $planned = $true; $rest = $Matches['p'] }

    if ($rest -match $pathRx) {
      $path = $Matches['path']; $name = $Matches['name']
      $folder = if ($Matches.ContainsKey('folder')) { $Matches['folder'] } else { $null }
      $anchor = if ($Matches.ContainsKey('anchor')) { $Matches['anchor'] } else { $null }
      if (-not $lastWasHeading -or $null -eq $last -or $null -ne $last.Path) {
        Add-HelpFailure "outline.md line ${n}: the page line $line must be the line straight under its heading."
        continue
      }
      foreach ($seg in ($path -replace '\.md$', '').Split('/')) {
        if (-not (Test-PascalName $seg)) {
          Add-HelpFailure "outline.md line ${n}: '$seg' in $path is not PascalCase (a capital, then letters and digits)."
        }
      }
      if ($planned -and $anchor) {
        Add-HelpFailure "outline.md line ${n}: a planned page has no headings yet, so it cannot carry #$anchor."
      }
      $last.Path = $path; $last.Name = $name; $last.Folder = $folder
      $last.Anchor = $anchor; $last.Planned = $planned
      $lastWasHeading = $false
      continue
    }

    Add-HelpFailure ("outline.md line ${n}: not a heading, a page line, a note (>) or a blank line: " + $line)
    $lastWasHeading = $false
  }

  # The same page twice would be two Contents entries for one file.
  $seen = @{}
  foreach ($e in $entries) {
    if (-not $e.Name) { continue }
    $k = $e.Name.ToLowerInvariant()
    if ($seen.ContainsKey($k)) {
      Add-HelpFailure ("outline.md line $($e.Line): $($e.Path) is already named on line " + $seen[$k] + '; each page appears once.')
    } else { $seen[$k] = $e.Line }
    foreach ($r in $script:ReservedPageNames) {
      if ($e.Name -ieq $r) {
        Add-HelpFailure "outline.md line $($e.Line): $($e.Name) is a name the HTML help reserves for its own $($r -replace '\.md$','') page."
      }
    }
  }
  return $entries.ToArray()
}

# ---------------------------------------------------------------------------
# The page tree on disk
# ---------------------------------------------------------------------------
function Get-HelpPageTree([string] $MdDir) {
  $pages = New-Object System.Collections.Generic.List[object]
  $root = (Resolve-Path -LiteralPath $MdDir).Path.TrimEnd('\')
  foreach ($f in (Get-ChildItem -LiteralPath $root -Recurse -Force)) {
    $rel = $f.FullName.Substring($root.Length + 1).Replace('\', '/')
    $depth = ($rel.Split('/')).Length - 1
    if ($f.PSIsContainer) {
      if ($depth -ge 1) { Add-HelpFailure "md/$rel is a folder inside a folder; the help has one level of folders." }
      elseif (-not (Test-PascalName $f.Name)) { Add-HelpFailure "md/$rel is not a PascalCase folder name (a capital, then letters and digits)." }
      continue
    }
    if ($f.Extension -ne '.md') {
      Add-HelpFailure "md/$rel is not a .md page. Only pages belong under md/."
      continue
    }
    if ($depth -gt 1) { continue } # already reported through its folder
    if (-not (Test-PascalName $f.BaseName)) {
      Add-HelpFailure "md/$rel is not a PascalCase page name (a capital, then letters and digits)."
    }
    $pages.Add([pscustomobject]@{
      Rel = $rel; Name = $f.Name; FullName = $f.FullName
      Folder = $(if ($depth -eq 1) { $rel.Split('/')[0] } else { $null })
    })
  }

  # Bare names are unique across the tree, ignoring case, because links name
  # a page without its folder. Checked before any page is converted.
  $byName = @{}
  foreach ($p in $pages) {
    $k = $p.Name.ToLowerInvariant()
    if ($byName.ContainsKey($k)) {
      Add-HelpFailure ("md/$($p.Rel) and md/" + $byName[$k].Rel + ' share a page name (ignoring case). Page names must be unique across every folder, because links name a page without its folder.')
    } else { $byName[$k] = $p }
  }
  return [pscustomobject]@{ Pages = $pages.ToArray(); ByName = $byName }
}

function Test-OutlineAgainstTree($Entries, $Tree) {
  $named = @{}
  foreach ($e in $Entries) {
    if (-not $e.Name) { continue }
    $k = $e.Name.ToLowerInvariant()
    $named[$k] = $e
    $onDisk = $Tree.ByName[$k]
    if ($e.Planned) {
      if ($onDisk) {
        Add-HelpFailure ("outline.md line $($e.Line): $($e.Path) is marked Planned, but md/$($onDisk.Rel) exists. Now the page is written, delete 'Planned: ' from that line.")
      }
      continue
    }
    if (-not $onDisk) {
      Add-HelpFailure "outline.md line $($e.Line): $($e.Path) names no page; there is no file of that name under md/."
      continue
    }
    if ($onDisk.Rel -cne $e.Path) {
      if ($onDisk.Rel -ieq $e.Path) {
        Add-HelpFailure "outline.md line $($e.Line): $($e.Path) has the wrong case; the file is md/$($onDisk.Rel)."
      } else {
        $where = if ($onDisk.Folder) { "in the $($onDisk.Folder) folder" } else { 'at the top of md/' }
        Add-HelpFailure "outline.md line $($e.Line): $($e.Path) names the wrong folder; the file is $where, md/$($onDisk.Rel)."
      }
    }
  }
  foreach ($p in $Tree.Pages) {
    if (-not $named.ContainsKey($p.Name.ToLowerInvariant())) {
      Add-HelpFailure "md/$($p.Rel) is not in outline.md. Every page must have its place in the outline, or nobody can reach it."
    }
  }
}

# ---------------------------------------------------------------------------
# Conversion: the pandoc command line for one page
# ---------------------------------------------------------------------------
function Get-PandocArguments([string] $PagePath, [string] $MdDir, [string] $Filter,
                             [string] $BodyPath, [string] $SidecarPath, [string[]] $PlannedNames) {
  $q = { param($s) '"' + ($s -replace '"', '\"') + '"' }
  $parts = @(
    (& $q $PagePath), '-f', 'markdown', '-t', 'html',
    '-M', (& $q ('help_root=' + $MdDir)),
    '-M', (& $q ('help_sidecar=' + $SidecarPath)),
    '--lua-filter', (& $q $Filter),
    '-o', (& $q $BodyPath)
  )
  if ([System.IO.Path]::GetFileName($PagePath) -ceq 'WhatsNew.md') {
    $parts += @('-M', 'help_imported=true')
  }
  if ($PlannedNames -and $PlannedNames.Count -gt 0) {
    $parts += @('-M', (& $q ('help_planned=' + ($PlannedNames -join ';'))))
  }
  return ($parts -join ' ')
}

# ---------------------------------------------------------------------------
# Sidecars
# ---------------------------------------------------------------------------
function Read-HelpSidecar([string] $Path) {
  $raw = [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)
  $j = $raw | ConvertFrom-Json
  $sections = @()
  foreach ($s in (Get-JsonArray $j.sections)) {
    $sections += [pscustomobject]@{ Id = [string]$s.id; Heading = [string]$s.heading; Level = [int]$s.level; Text = [string]$s.text }
  }
  return [pscustomobject]@{
    Page     = [string]$j.page
    Title    = [string]$j.title
    Keywords = @(Get-JsonArray $j.keywords | ForEach-Object { [string]$_ })
    Ids      = @(Get-JsonArray $j.ids | ForEach-Object { [string]$_ })
    Suspect  = @(Get-JsonArray $j.suspect_ids)
    Sections = $sections
  }
}

function Test-HelpSidecars($Sidecars, $Entries) {
  $used = @{}
  foreach ($sc in $Sidecars) {
    foreach ($s in $sc.Suspect) {
      $key = $sc.Page + '#' + $s.id
      if ($script:AnchorExceptions.ContainsKey($key)) { $used[$key] = $true; continue }
      Add-HelpFailure ("md/$($sc.Page): the heading id #$($s.id) breaks as soon as a heading is added above it: " + $s.why + '. Give the heading an explicit id, like ## Heading text {#a-lasting-id}, or reword it.')
    }
    if ($sc.Ids -contains $script:MainContentId) {
      Add-HelpFailure "md/$($sc.Page): a heading has the id #$($script:MainContentId), which the HTML help's skip link uses."
    }
    Test-SitemapText "md/$($sc.Page)'s title" $sc.Title
    foreach ($k in $sc.Keywords) {
      Test-SitemapText "md/$($sc.Page)'s keyword" $k
      if ($k.Trim() -eq '') { Add-HelpFailure "md/$($sc.Page) has an empty keyword." }
    }
  }
  foreach ($k in $script:AnchorExceptions.Keys) {
    if (-not $used.ContainsKey($k)) {
      Add-HelpFailure "help-generate.ps1 excuses $k, but that heading id is no longer produced. The page has been fixed: remove the exception."
    }
  }
  $byPage = @{}
  foreach ($sc in $Sidecars) { $byPage[$sc.Page] = $sc }
  foreach ($e in $Entries) {
    if ($e.Anchor -and -not $e.Planned -and $byPage.ContainsKey($e.Path)) {
      if (-not ($byPage[$e.Path].Ids -contains $e.Anchor)) {
        Add-HelpFailure ("outline.md line $($e.Line): $($e.Path) has no heading with id #$($e.Anchor); it has: " + ($byPage[$e.Path].Ids -join ' '))
      }
    }
  }
}

# ---------------------------------------------------------------------------
# The Contents tree, with planned pages and empty folders pruned
# ---------------------------------------------------------------------------
function Get-ContentsTree($Entries) {
  # Build parent/child from heading levels.
  $root = [pscustomobject]@{ Entry = $null; Children = (New-Object System.Collections.Generic.List[object]) }
  $stack = New-Object System.Collections.Generic.List[object]
  $stack.Add($root)
  foreach ($e in $Entries) {
    $node = [pscustomobject]@{ Entry = $e; Children = (New-Object System.Collections.Generic.List[object]) }
    while ($stack.Count -gt $e.Level) { $stack.RemoveAt($stack.Count - 1) }
    $stack[$stack.Count - 1].Children.Add($node)
    $stack.Add($node)
  }
  return (Get-PrunedNode $root)
}

function Get-PrunedNode($Node) {
  $kept = New-Object System.Collections.Generic.List[object]
  foreach ($c in $Node.Children) {
    $p = Get-PrunedNode $c
    if ($null -ne $p) { $kept.Add($p) }
  }
  $e = $Node.Entry
  if ($null -ne $e) {
    $hasPage = $e.Path -and -not $e.Planned
    if (-not $hasPage -and $kept.Count -eq 0) { return $null }
    if ($e.Planned -and $kept.Count -gt 0) {
      # A planned page with written pages beneath it: show it as a folder.
      $e = [pscustomobject]@{ Line = $e.Line; Level = $e.Level; Title = $e.Title; Path = $null; Name = $null; Folder = $null; Anchor = $null; Planned = $false }
    }
  }
  return [pscustomobject]@{ Entry = $e; Children = $kept }
}

function Get-EntryHref($Entry, [string] $Prefix) {
  $h = $Prefix + ($Entry.Path -replace '\.md$', '.htm')
  if ($Entry.Anchor) { $h += '#' + $Entry.Anchor }
  return $h
}

# ---------------------------------------------------------------------------
# The compiled help's inputs: [FILES], toc.hhc and index.hhk
# ---------------------------------------------------------------------------
function Get-HhpText($Entries) {
  $files = New-Object System.Collections.Generic.List[string]
  foreach ($e in $Entries) {
    if ($e.Path -and -not $e.Planned) { $files.Add('pages\' + ($e.Path -replace '\.md$', '.htm').Replace('/', '\')) }
  }
  $lines = @(
    '; Generated by docs\help\build-help.ps1 from outline.md. Do not edit;',
    '; the next help build overwrites it.',
    '[OPTIONS]',
    'Compatibility=1.1 or later',
    'Compiled file=JJFlexRadio.chm',
    'Contents file=toc.hhc',
    'Default topic=pages\Welcome.htm',
    'Display compile progress=No',
    'Full-text search=Yes',
    'Index file=index.hhk',
    'Language=0x409 English (United States)',
    'Title=JJ Flexible Radio Access Help',
    '',
    '[FILES]'
  ) + $files.ToArray()
  return (($lines -join "`r`n") + "`r`n")
}

function Get-HhcText($Tree) {
  $sb = New-Object System.Text.StringBuilder
  [void]$sb.AppendLine('<!DOCTYPE HTML PUBLIC "-//IETF//DTD HTML//EN">')
  [void]$sb.AppendLine('<HTML>')
  [void]$sb.AppendLine('<HEAD>')
  [void]$sb.AppendLine('<meta name="GENERATOR" content="JJFlex Help Build">')
  [void]$sb.AppendLine('<!-- Generated by docs\help\build-help.ps1 from outline.md. Do not edit. -->')
  [void]$sb.AppendLine('</HEAD>')
  [void]$sb.AppendLine('<BODY>')
  [void]$sb.AppendLine('<OBJECT type="text/site properties">')
  [void]$sb.AppendLine('  <param name="Auto Generated" value="No">')
  [void]$sb.AppendLine('</OBJECT>')
  Add-HhcList $sb $Tree.Children ''
  [void]$sb.AppendLine('</BODY>')
  [void]$sb.AppendLine('</HTML>')
  return $sb.ToString()
}

function Add-HhcList($sb, $Nodes, [string] $Indent) {
  [void]$sb.AppendLine($Indent + '<UL>')
  foreach ($n in $Nodes) {
    $e = $n.Entry
    [void]$sb.AppendLine($Indent + '  <LI><OBJECT type="text/sitemap">')
    [void]$sb.AppendLine($Indent + '    <param name="Name" value="' + $e.Title + '">')
    if ($e.Path) {
      [void]$sb.AppendLine($Indent + '    <param name="Local" value="' + ((Get-EntryHref $e 'pages/').Replace('/', '\')) + '">')
    }
    [void]$sb.AppendLine($Indent + '  </OBJECT>')
    if ($n.Children.Count -gt 0) { Add-HhcList $sb $n.Children ($Indent + '  ') }
  }
  [void]$sb.AppendLine($Indent + '</UL>')
}

# One index term per page title and per keyword. A term reaching several
# pages becomes one entry carrying each page.
function Get-IndexTerms($Sidecars) {
  $terms = @{}
  foreach ($sc in $Sidecars) {
    $words = @($sc.Title) + @($sc.Keywords)
    foreach ($w in $words) {
      $k = $w.ToLowerInvariant()
      if (-not $terms.ContainsKey($k)) { $terms[$k] = [pscustomobject]@{ Term = $w; Pages = (New-Object System.Collections.Generic.List[object]) } }
      $already = $false
      foreach ($p in $terms[$k].Pages) { if ($p.Page -eq $sc.Page) { $already = $true } }
      if (-not $already) { $terms[$k].Pages.Add($sc) }
    }
  }
  $list = @($terms.Values)
  foreach ($t in $list) {
    $sorted = @($t.Pages | Sort-Object -Property @{ Expression = { $_.Title.ToLowerInvariant() } })
    $t.Pages.Clear(); foreach ($p in $sorted) { $t.Pages.Add($p) }
  }
  return @($list | Sort-Object -Property @{ Expression = { $_.Term.ToLowerInvariant() } })
}

function Get-HhkText($Terms) {
  $sb = New-Object System.Text.StringBuilder
  [void]$sb.AppendLine('<!DOCTYPE HTML PUBLIC "-//IETF//DTD HTML//EN">')
  [void]$sb.AppendLine('<HTML>')
  [void]$sb.AppendLine('<HEAD>')
  [void]$sb.AppendLine('<meta name="GENERATOR" content="JJFlex Help Build">')
  [void]$sb.AppendLine('<!-- Generated by docs\help\build-help.ps1 from each page''s title and keywords. Do not edit. -->')
  [void]$sb.AppendLine('</HEAD>')
  [void]$sb.AppendLine('<BODY>')
  [void]$sb.AppendLine('<UL>')
  foreach ($t in $Terms) {
    [void]$sb.AppendLine('  <LI><OBJECT type="text/sitemap">')
    [void]$sb.AppendLine('    <param name="Name" value="' + $t.Term + '">')
    if ($t.Pages.Count -eq 1) {
      [void]$sb.AppendLine('    <param name="Local" value="pages\' + ($t.Pages[0].Page -replace '\.md$', '.htm').Replace('/', '\') + '">')
    } else {
      foreach ($p in $t.Pages) {
        [void]$sb.AppendLine('    <param name="Name" value="' + $p.Title + '">')
        [void]$sb.AppendLine('    <param name="Local" value="pages\' + ($p.Page -replace '\.md$', '.htm').Replace('/', '\') + '">')
      }
    }
    [void]$sb.AppendLine('  </OBJECT>')
  }
  [void]$sb.AppendLine('</UL>')
  [void]$sb.AppendLine('</BODY>')
  [void]$sb.AppendLine('</HTML>')
  return $sb.ToString()
}

# ---------------------------------------------------------------------------
# Page wrappers. One pandoc body per page serves both outputs, because both
# keep md/'s folder shape and the filter writes relative links.
# ---------------------------------------------------------------------------
function Get-UpPrefix([string] $Rel) {
  $depth = ($Rel.Split('/')).Length - 1
  return ('../' * $depth)
}

function Get-ChmPageHtml([string] $Title, [string] $Rel, [string] $Body) {
  # Pages sit under pages\ in the compiled help, style.css one level above.
  $css = '../' + (Get-UpPrefix $Rel) + 'style.css'
  return @"
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8" />
<title>$(ConvertTo-HtmlText $Title)</title>
<link rel="stylesheet" href="$css" />
</head>
<body>
$Body
</body>
</html>
"@
}

# Every HTML help page opens, in this order, with a skip link to the page's
# content, a search box, and links to the Contents and the Index (Noel,
# 2026-10-27, #834). The skip link's words are his.
function Get-WebTop([string] $Up) {
  return @"
<header class="help-top">
<a class="skip-link" href="#$($script:MainContentId)">Skip to main help</a>
<form class="help-search" role="search" action="${Up}Search.htm" method="get">
<label for="help-search-box">Search help</label>
<input type="search" id="help-search-box" name="q">
<button type="submit">Search</button>
</form>
<nav class="help-links" aria-label="Help">
<a href="${Up}Contents.htm">Contents</a>
<a href="${Up}Index.htm">Index</a>
</nav>
</header>
"@
}

function Get-WebPageHtml([string] $Title, [string] $Up, [string] $Body, [string[]] $ExtraScripts) {
  $scripts = '<script src="' + $Up + 'help.js"></script>'
  foreach ($s in $ExtraScripts) { $scripts += "`n" + '<script src="' + $Up + $s + '"></script>' }
  return @"
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>$(ConvertTo-HtmlText $Title)</title>
<link rel="stylesheet" href="${Up}style.css">
<link rel="stylesheet" href="${Up}help-web.css">
$scripts
</head>
<body>
$(Get-WebTop $Up)
<main id="$($script:MainContentId)" tabindex="-1">
$Body
</main>
</body>
</html>
"@
}

# ---------------------------------------------------------------------------
# The HTML help's generated pages
# ---------------------------------------------------------------------------
function Get-ContentsBody($Tree, $TitleByPath) {
  $sb = New-Object System.Text.StringBuilder
  [void]$sb.AppendLine('<h1 id="contents">Contents</h1>')
  Add-ContentsList $sb $Tree.Children
  return $sb.ToString()
}

function Add-ContentsList($sb, $Nodes) {
  [void]$sb.AppendLine('<ul class="help-contents">')
  foreach ($n in $Nodes) {
    $e = $n.Entry
    if ($e.Path) {
      [void]$sb.Append('<li><a href="' + (Get-EntryHref $e '') + '">' + (ConvertTo-HtmlText $e.Title) + '</a>')
    } else {
      [void]$sb.Append('<li>' + (ConvertTo-HtmlText $e.Title))
    }
    if ($n.Children.Count -gt 0) { [void]$sb.AppendLine(''); Add-ContentsList $sb $n.Children }
    [void]$sb.AppendLine('</li>')
  }
  [void]$sb.AppendLine('</ul>')
}

# The Index: one link per list item, and each link says where it goes.
# Grouped under a heading per first letter, so H moves between letters.
function Get-IndexBody($Terms) {
  $sb = New-Object System.Text.StringBuilder
  [void]$sb.AppendLine('<h1 id="index">Index</h1>')
  $group = $null
  $open = $false
  foreach ($t in $Terms) {
    $first = $t.Term.Substring(0, 1).ToUpperInvariant()
    $g = if ($first -match '^[A-Z]$') { $first } else { 'Numbers' }
    if ($g -ne $group) {
      if ($open) { [void]$sb.AppendLine('</ul>') }
      $id = 'index-' + $g.ToLowerInvariant()
      [void]$sb.AppendLine('<h2 id="' + $id + '">' + $g + '</h2>')
      [void]$sb.AppendLine('<ul class="help-index">')
      $group = $g; $open = $true
    }
    foreach ($p in $t.Pages) {
      $href = ($p.Page -replace '\.md$', '.htm')
      $text = if ($t.Term -ieq $p.Title) { $p.Title } else { $t.Term + ': ' + $p.Title }
      [void]$sb.AppendLine('<li><a href="' + $href + '">' + (ConvertTo-HtmlText $text) + '</a></li>')
    }
  }
  if ($open) { [void]$sb.AppendLine('</ul>') }
  return $sb.ToString()
}

function Get-SearchBody {
  return @"
<h1 id="search">Search</h1>
<div id="help-search-results"></div>
<noscript><p>Search needs JavaScript. The Contents and the Index above work without it.</p></noscript>
"@
}

# One record per section of every page, for MiniSearch. A .js file setting a
# global rather than JSON fetched at run time, because a browser opening the
# pages from disk refuses to fetch local files (#834 research).
function Get-SearchDataJs($Sidecars) {
  $records = New-Object System.Collections.Generic.List[object]
  $n = 0
  foreach ($sc in $Sidecars) {
    $page = ($sc.Page -replace '\.md$', '.htm')
    foreach ($s in $sc.Sections) {
      $n++
      $isTop = ($s.Level -eq 1)
      $text = $s.Text
      $excerpt = if ($text.Length -gt 200) { $text.Substring(0, 200).TrimEnd() + '...' } else { $text }
      $records.Add([ordered]@{
        id       = $n
        href     = $(if ($isTop) { $page } else { $page + '#' + $s.Id })
        page     = $sc.Title
        heading  = $(if ($isTop) { '' } else { $s.Heading })
        keywords = $(if ($isTop) { ($sc.Keywords -join ', ') } else { '' })
        text     = $text
        excerpt  = $excerpt
      })
    }
  }
  $json = ConvertTo-Json -InputObject $records.ToArray() -Depth 4 -Compress
  return "// Generated by docs\help\build-help.ps1. Do not edit.`nwindow.JJFLEX_HELP_SEARCH = $json;`n"
}
