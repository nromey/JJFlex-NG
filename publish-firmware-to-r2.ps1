# publish-firmware-to-r2.ps1 — upload firmware blobs directly to the R2 bucket.
#
# Why this exists: firmware .ssdr images are 60-370 MB and GitHub hard-rejects
# files over 100 MB, so the blobs cannot ride the nromey/jjf-data git-push
# pipeline the way manifest.json does. This script uploads them straight to
# the jjflex-data R2 bucket with the same credentials the GitHub Action uses.
# The Action's sync excludes firmware/*.ssdr so it never deletes what this
# script uploads.
#
# Manifest-driven: reads jjf-data's firmware/manifest.json, finds each listed
# blob under the search roots, verifies size + SHA256 against the manifest,
# and uploads only verified files. A blob that fails verification is reported
# and skipped — this script cannot ship a file the manifest didn't promise.
# Idempotent: blobs already in R2 with the right size are skipped (use -Force
# to re-upload).
#
# Credentials, in order of preference:
#   1. R2_ACCESS_KEY_ID / R2_SECRET_ACCESS_KEY / R2_ENDPOINT env vars, if set.
#   2. 1Password CLI (op) pulling the "Cloudflare R2 — jjf-data sync" item —
#      requires the desktop app's Developer > "Integrate with 1Password CLI"
#      toggle; 1Password prompts to approve. Secrets go into this process's
#      environment only, never to disk.
#
# Invoke with the call operator (repo-root script convention):
#   & "C:\dev\JJFlex-NG\publish-firmware-to-r2.ps1"

param(
    [string]$ManifestPath = 'C:\dev\jjf-data\firmware\manifest.json',
    [string[]]$SearchRoots = @(
        'C:\dev\smartsdr-v4.2.20-extracted',
        'C:\dev\smartsdr-v4.2.18-extracted'
    ),
    [string]$Bucket = 'jjflex-data',
    [string]$OpItemName = 'r2 cloudflare',
    [string]$OpPath = 'op',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

function Resolve-AwsCli {
    $cmd = Get-Command aws -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $fallback = Join-Path $env:ProgramFiles 'Amazon\AWSCLIV2\aws.exe'
    if (Test-Path $fallback) { return $fallback }
    throw 'aws CLI not found. Install with: winget install Amazon.AWSCLI'
}

function Resolve-Credentials {
    param([string]$ItemName, [string]$Op)
    if ($env:R2_ACCESS_KEY_ID -and $env:R2_SECRET_ACCESS_KEY -and $env:R2_ENDPOINT) {
        Write-Host 'Using R2 credentials from environment.'
        return
    }
    $opCmd = Get-Command $Op -ErrorAction SilentlyContinue
    if (-not $opCmd) {
        throw @'
No R2 credentials. Either set R2_ACCESS_KEY_ID, R2_SECRET_ACCESS_KEY, and
R2_ENDPOINT in this shell, or install the 1Password CLI (winget install
AgileBits.1Password.CLI) and enable Settings > Developer > "Integrate with
1Password CLI" in the 1Password desktop app.
'@
    }

    # One op call for the whole item — one approval prompt at most. The item is
    # a secure note whose body holds label/value lines; parse the three values
    # out of the note text. Values never get echoed anywhere.
    Write-Host "Requesting R2 credentials from 1Password item '$ItemName' (approve the prompt if asked)..."
    $item = & $opCmd.Source item get $ItemName --format json | ConvertFrom-Json
    if (-not $item) { throw "1Password item '$ItemName' not found." }
    $note = ($item.fields | Where-Object label -eq 'notesPlain').value ?? ''

    # The note's actual layout (verified 2026-08-03): a label line ending in a
    # colon, value on the next line — plus the endpoint URL sharing a line with
    # its label. Accept same-line "label = value" / "label: value" too, so a
    # future re-paste in either style keeps working.
    function Get-NoteValue([string]$text, [string[]]$labels) {
        $lines = $text -split "\r?\n"
        for ($i = 0; $i -lt $lines.Count; $i++) {
            foreach ($label in $labels) {
                if ($lines[$i] -match "(?i)^\s*$label\s*[=:]\s*(\S+)\s*$") { return $Matches[1] }
                if ($lines[$i] -match "(?i)^\s*$label\s*[=:]?\s*$") {
                    for ($j = $i + 1; $j -lt $lines.Count; $j++) {
                        if ($lines[$j].Trim().Length -gt 0) { return $lines[$j].Trim() }
                    }
                }
            }
        }
        return $null
    }
    $env:R2_ACCESS_KEY_ID     = Get-NoteValue $note @('R2_ACCESS_KEY_ID', 'Access Key ID', 'Access Key')
    $env:R2_SECRET_ACCESS_KEY = Get-NoteValue $note @('R2_SECRET_ACCESS_KEY', 'Secret Access Key', 'Secret Key')
    $env:R2_ENDPOINT          = Get-NoteValue $note @('R2_ENDPOINT', 'S3 API Endpoint', 'Endpoint')
    # Endpoint fallback: pull the https URL out of any line mentioning the R2
    # storage domain, labeled or not.
    if (-not $env:R2_ENDPOINT) {
        foreach ($line in ($note -split "\r?\n")) {
            if ($line -match '(https://\S*r2\.cloudflarestorage\.com\S*)') { $env:R2_ENDPOINT = $Matches[1].TrimEnd('/'); break }
        }
    }
    if (-not ($env:R2_ACCESS_KEY_ID -and $env:R2_SECRET_ACCESS_KEY -and $env:R2_ENDPOINT)) {
        $missing = @()
        if (-not $env:R2_ACCESS_KEY_ID) { $missing += 'access key id' }
        if (-not $env:R2_SECRET_ACCESS_KEY) { $missing += 'secret access key' }
        if (-not $env:R2_ENDPOINT) { $missing += 'endpoint' }
        throw "Could not parse from the '$ItemName' note: $($missing -join ', '). Expected lines like 'R2_ACCESS_KEY_ID = <value>'."
    }
}

$aws = Resolve-AwsCli
Resolve-Credentials -ItemName $OpItemName -Op $OpPath

# aws CLI reads AWS_* names; map from our R2_* convention. Process scope only.
$env:AWS_ACCESS_KEY_ID     = $env:R2_ACCESS_KEY_ID
$env:AWS_SECRET_ACCESS_KEY = $env:R2_SECRET_ACCESS_KEY
$env:AWS_DEFAULT_REGION    = 'auto'

$manifest = Get-Content $ManifestPath -Raw | ConvertFrom-Json
Write-Host "Manifest: $ManifestPath ($($manifest.images.Count) image entries)"

$failures = @()
foreach ($image in $manifest.images) {
    $name = $image.file_name
    Write-Host ''
    Write-Host "=== $name ($($image.family) $($image.version)) ==="

    $local = $SearchRoots | ForEach-Object {
        Get-ChildItem $_ -Recurse -Filter $name -ErrorAction SilentlyContinue
    } | Select-Object -First 1
    if (-not $local) {
        Write-Host "  NOT FOUND under search roots -- skipped."
        $failures += "$name -- not found locally"
        continue
    }
    Write-Host "  Local: $($local.FullName)"

    if ($local.Length -ne $image.size_bytes) {
        Write-Host "  SIZE MISMATCH: local $($local.Length), manifest $($image.size_bytes) -- skipped."
        $failures += "$name -- size mismatch"
        continue
    }
    Write-Host '  Hashing...'
    $hash = (Get-FileHash $local.FullName -Algorithm SHA256).Hash.ToLower()
    if ($hash -ne $image.sha256.ToLower()) {
        Write-Host "  SHA256 MISMATCH -- skipped. This file is not what the manifest promises."
        $failures += "$name -- sha256 mismatch"
        continue
    }
    Write-Host '  Verified against manifest.'

    $key = "firmware/$name"
    if (-not $Force) {
        & $aws s3api head-object --bucket $Bucket --key $key --endpoint-url $env:R2_ENDPOINT 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "  Already in R2 -- skipped (use -Force to re-upload)."
            continue
        }
    }

    Write-Host "  Uploading to s3://$Bucket/$key ..."
    # Versioned filenames never change content, so long immutable caching is
    # safe and lets Cloudflare's edge absorb repeat downloads.
    & $aws s3 cp $local.FullName "s3://$Bucket/$key" `
        --endpoint-url $env:R2_ENDPOINT `
        --content-type 'application/octet-stream' `
        --cache-control 'public, max-age=31536000, immutable'
    if ($LASTEXITCODE -ne 0) {
        $failures += "$name -- upload failed (aws exit $LASTEXITCODE)"
        continue
    }

    # Confirm the public URL serves the right byte count.
    try {
        $head = Invoke-WebRequest -Uri $image.url -Method Head -TimeoutSec 30
        $len = [long]$head.Headers['Content-Length'][0]
        if ($len -eq $image.size_bytes) {
            Write-Host "  LIVE: $($image.url) ($len bytes)"
        } else {
            $failures += "$name -- public URL length $len != $($image.size_bytes)"
        }
    } catch {
        $failures += "$name -- public URL check failed: $($_.Exception.Message)"
    }
}

Write-Host ''
if ($failures) {
    Write-Host 'FAILURES:'
    $failures | ForEach-Object { Write-Host "  $_" }
    exit 1
}
Write-Host 'All manifest blobs verified and live.'
