#requires -Version 7
<#
.SYNOPSIS
  Downloads and verifies the pinned upstream C sources into foundation/native/_deps.
.PARAMETER UpdateHashes
  Recomputes every hash and rewrites versions.json. Use once to seed, then never again
  without reading the diff.
#>
[CmdletBinding()]
param(
    [switch]$UpdateHashes,
    [switch]$IncludeCipher
)

$ErrorActionPreference = 'Stop'
$nativeRoot   = Split-Path -Parent $PSScriptRoot
$versionsPath = Join-Path $nativeRoot 'versions.json'
$depsRoot     = Join-Path $nativeRoot '_deps'
$downloadRoot = Join-Path $depsRoot 'download'

New-Item -ItemType Directory -Force -Path $depsRoot, $downloadRoot | Out-Null

$versions = Get-Content $versionsPath -Raw | ConvertFrom-Json
$names = if ($IncludeCipher) { 'sqlite', 'sqliteVec', 'sqlcipher', 'libtomcrypt' } else { 'sqlite', 'sqliteVec' }

function Get-Digest {
    param([string]$Path, [string]$Algorithm)
    $script = "import hashlib,sys; h=hashlib.new(sys.argv[2]); h.update(open(sys.argv[1],'rb').read()); print(h.hexdigest())"
    $value = & python -c $script $Path $Algorithm
    if ($LASTEXITCODE -ne 0) { throw "python hashing failed for $Path" }
    return $value.Trim()
}

foreach ($name in $names) {
    $entry   = $versions.$name
    $archive = Join-Path $downloadRoot $entry.archive

    if (-not (Test-Path $archive)) {
        Write-Host "downloading $($entry.url)"
        Invoke-WebRequest -Uri $entry.url -OutFile $archive -UseBasicParsing
    }

    $actual = Get-Digest -Path $archive -Algorithm $entry.hashAlgorithm

    if ($UpdateHashes) {
        Write-Host "$name $($entry.hashAlgorithm) = $actual"
        $entry.hash = $actual
    }
    elseif ($entry.hash -ne $actual) {
        throw "HASH MISMATCH for $name`n  expected $($entry.hash)`n  actual   $actual`n  file     $archive"
    }

    $target = Join-Path $depsRoot $entry.extractedDir
    if (-not (Test-Path $target)) {
        Write-Host "extracting $($entry.archive)"
        if ($entry.archive.EndsWith('.zip')) {
            Expand-Archive -Path $archive -DestinationPath $depsRoot -Force
        }
        else {
            # sqlite-vec's amalgamation has no directory prefix, so give it its own folder.
            $dest = if ($name -eq 'sqliteVec') { $target } else { $depsRoot }
            New-Item -ItemType Directory -Force -Path $dest | Out-Null
            & tar -xzf $archive -C $dest
            if ($LASTEXITCODE -ne 0) { throw "tar failed for $archive" }
        }
    }

    if (-not (Test-Path $target)) { throw "expected $target after extracting $($entry.archive)" }
}

if ($UpdateHashes) {
    $versions | ConvertTo-Json -Depth 8 | Set-Content -Path $versionsPath -Encoding utf8
    Write-Host "versions.json updated - review the diff before committing"
}

Write-Host "OK: sources present under $depsRoot"
