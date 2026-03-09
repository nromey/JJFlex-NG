# convert-md.ps1 — Simple Markdown to HTML converter for JJFlex help
# Usage: convert-md.ps1 <input-dir> <output-dir>

param(
    [string]$InputDir,
    [string]$OutputDir
)

function Convert-MarkdownToHtml {
    param([string]$MarkdownText, [string]$Title)

    $lines = $MarkdownText -split "`n"
    $html = [System.Collections.Generic.List[string]]::new()
    $inTable = $false
    $inList = $false
    $listType = ""

    foreach ($rawLine in $lines) {
        $line = $rawLine.TrimEnd("`r")

        # Skip table separator rows (|---|---|)
        if ($line -match '^\|[\s\-:]+\|') { continue }

        # Table rows
        if ($line -match '^\|(.+)\|$') {
            $cells = ($Matches[1] -split '\|') | ForEach-Object { $_.Trim() }
            if (-not $inTable) {
                $html.Add('<table>')
                $tag = 'th'
                $inTable = $true
            } else {
                $tag = 'td'
            }
            $row = '<tr>' + (($cells | ForEach-Object { "<$tag>$(Convert-InlineMarkdown $_)</$tag>" }) -join '') + '</tr>'
            $html.Add($row)
            continue
        }

        # Close table if we were in one
        if ($inTable) {
            $html.Add('</table>')
            $inTable = $false
        }

        # Empty line — close list if in one, add paragraph break
        if ($line -match '^\s*$') {
            if ($inList) {
                $html.Add("</$listType>")
                $inList = $false
            }
            continue
        }

        # Headings
        if ($line -match '^(#{1,6})\s+(.+)$') {
            $level = $Matches[1].Length
            $text = Convert-InlineMarkdown $Matches[2]
            $html.Add("<h$level>$text</h$level>")
            continue
        }

        # Unordered list items
        if ($line -match '^\s*[-*]\s+(.+)$') {
            if (-not $inList -or $listType -ne 'ul') {
                if ($inList) { $html.Add("</$listType>") }
                $html.Add('<ul>')
                $inList = $true
                $listType = 'ul'
            }
            $html.Add("<li>$(Convert-InlineMarkdown $Matches[1])</li>")
            continue
        }

        # Ordered list items
        if ($line -match '^\s*\d+\.\s+(.+)$') {
            if (-not $inList -or $listType -ne 'ol') {
                if ($inList) { $html.Add("</$listType>") }
                $html.Add('<ol>')
                $inList = $true
                $listType = 'ol'
            }
            $html.Add("<li>$(Convert-InlineMarkdown $Matches[1])</li>")
            continue
        }

        # Close list if we hit a non-list line
        if ($inList) {
            $html.Add("</$listType>")
            $inList = $false
        }

        # Tip/warning divs
        if ($line -match '^\*\*Tip:\*\*\s*(.+)$') {
            $html.Add("<div class=`"tip`"><strong>Tip:</strong> $(Convert-InlineMarkdown $Matches[1])</div>")
            continue
        }
        if ($line -match '^\*\*Warning:\*\*\s*(.+)$') {
            $html.Add("<div class=`"warning`"><strong>Warning:</strong> $(Convert-InlineMarkdown $Matches[1])</div>")
            continue
        }

        # Regular paragraph
        $html.Add("<p>$(Convert-InlineMarkdown $line)</p>")
    }

    # Close any open elements
    if ($inTable) { $html.Add('</table>') }
    if ($inList) { $html.Add("</$listType>") }

    $body = $html -join "`n"

    return @"
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>$Title</title>
<link rel="stylesheet" href="../style.css">
</head>
<body>
$body
</body>
</html>
"@
}

function Convert-InlineMarkdown {
    param([string]$Text)

    # Bold
    $Text = $Text -replace '\*\*(.+?)\*\*', '<strong>$1</strong>'
    # Italic
    $Text = $Text -replace '\*(.+?)\*', '<em>$1</em>'
    # Inline code
    $Text = $Text -replace '`(.+?)`', '<code>$1</code>'
    # Links
    $Text = $Text -replace '\[(.+?)\]\((.+?)\)', '<a href="$2">$1</a>'

    return $Text
}

# Main
if (-not (Test-Path $InputDir)) {
    Write-Error "Input directory not found: $InputDir"
    exit 1
}

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

$files = Get-ChildItem "$InputDir\*.md"
foreach ($file in $files) {
    $name = $file.BaseName
    $content = Get-Content $file.FullName -Raw -Encoding UTF8
    # Derive title from first H1 heading or filename
    if ($content -match '^#\s+(.+)$') {
        $title = $Matches[1] -replace '\*', ''
    } else {
        $title = $name -replace '-', ' '
        $title = (Get-Culture).TextInfo.ToTitleCase($title)
    }
    $htmlContent = Convert-MarkdownToHtml -MarkdownText $content -Title $title
    $outPath = Join-Path $OutputDir "$name.htm"
    [System.IO.File]::WriteAllText($outPath, $htmlContent, [System.Text.UTF8Encoding]::new($false))
    Write-Host "  Converted $name.md"
}

Write-Host "Done. $($files.Count) files converted."
