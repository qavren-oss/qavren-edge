#requires -Version 7
<#
.SYNOPSIS
  Builds qedge_sqlite3.dll (or qedge_sqlcipher.dll) for win-x64 or win-arm64 using the
  VS-bundled CMake, Ninja and MSVC located through vswhere.
.PARAMETER Arch
  x64 (default) or arm64. arm64 is a cross-compile: it produces a DLL that cannot run on
  an x64 host, so no smoke test is run against it locally.
.PARAMETER SkipIfToolsetMissing
  Exit 0 with a printed reason instead of throwing when the requested toolset is absent.
  Used for the local arm64 slice, never in CI.
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')]
    [string]$Arch = 'x64',
    [switch]$Cipher,
    [switch]$SkipIfToolsetMissing,
    [string]$BuildSha = 'local',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$nativeRoot = Split-Path -Parent $PSScriptRoot

# A shell launched from (or inheriting the environment of) a VS Developer Prompt carries
# VCToolsVersion/VCINSTALLDIR/INCLUDE/LIB pointing at whatever toolset that prompt pinned. If
# that install is gone or older than the one vswhere picks, vcvars*.bat honours the stale
# VCToolsVersion and dies with "Version 'x.y.z' is not valid; directory does not exist".
# Scrub the whole MSVC/SDK block out of this process before handing it to cmd.
foreach ($stale in @(
        'VCINSTALLDIR', 'VCToolsVersion', 'VCToolsInstallDir', 'VCToolsRedistDir', 'VCIDEInstallDir',
        'VSINSTALLDIR', 'VisualStudioVersion', 'DevEnvDir', 'INCLUDE', 'LIB', 'LIBPATH', 'Platform',
        'WindowsSdkDir', 'WindowsSDKVersion', 'WindowsSdkBinPath', 'WindowsSdkVerBinPath',
        'WindowsLibPath', 'UCRTVersion', 'UniversalCRTSdkDir', 'ExtensionSdkDir')) {
    Remove-Item -Path "Env:\$stale" -ErrorAction SilentlyContinue
}
Get-ChildItem Env: | Where-Object Name -like 'VSCMD_*' | ForEach-Object {
    Remove-Item -Path "Env:\$($_.Name)" -ErrorAction SilentlyContinue
}

$vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { throw "vswhere.exe not found at $vswhere" }

# The x64 host toolset is always required (it hosts the cross compiler); arm64 additionally
# requires the ARM64 target tools.
$required = @('Microsoft.VisualStudio.Component.VC.Tools.x86.x64')
if ($Arch -eq 'arm64') { $required += 'Microsoft.VisualStudio.Component.VC.Tools.ARM64' }

$vsArgs = @('-latest', '-products', '*')
foreach ($c in $required) { $vsArgs += @('-requires', $c) }
$vsArgs += @('-property', 'installationPath')

$vsRoot = (& $vswhere @vsArgs)
if ([string]::IsNullOrWhiteSpace($vsRoot)) {
    $msg = "No Visual Studio installation carries: $($required -join ', ')"
    if ($SkipIfToolsetMissing) { Write-Host "SKIP ($Arch): $msg"; exit 0 }
    throw $msg
}
$vsRoot = $vsRoot.Trim()

$cmake  = Join-Path $vsRoot 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
$ninja  = Join-Path $vsRoot 'Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe'
$vcvarsName = if ($Arch -eq 'arm64') { 'vcvarsamd64_arm64.bat' } else { 'vcvars64.bat' }
$vcvars = Join-Path $vsRoot "VC\Auxiliary\Build\$vcvarsName"
foreach ($p in $cmake, $ninja) { if (-not (Test-Path $p)) { throw "missing tool: $p" } }
if (-not (Test-Path $vcvars)) {
    $msg = "missing tool: $vcvars"
    if ($SkipIfToolsetMissing) { Write-Host "SKIP ($Arch): $msg"; exit 0 }
    throw $msg
}

$rid       = "win-$Arch"
$variant   = if ($Cipher) { 'cipher' } else { 'plain' }
$buildDir  = Join-Path $nativeRoot "build\$rid-$variant"
$outDir    = Join-Path $nativeRoot "artifacts\$rid"
New-Item -ItemType Directory -Force -Path $buildDir, $outDir | Out-Null

# Drop any DLL a previous run left in artifacts\ BEFORE compiling: if this build fails, the
# smoke test must not find a stale library and report success over a red build.
$dllName = if ($Cipher) { 'qedge_sqlcipher.dll' } else { 'qedge_sqlite3.dll' }
Remove-Item (Join-Path $outDir $dllName) -Force -ErrorAction SilentlyContinue

$cipherFlag = if ($Cipher) { 'ON' } else { 'OFF' }
$systemProcessor = if ($Arch -eq 'arm64') { 'ARM64' } else { 'AMD64' }

# vcvars*.bat only exports into a cmd session, so configure and build inside one.
# CMAKE_SYSTEM_NAME/PROCESSOR put CMake into cross-compiling mode for the arm64 slice so it
# never tries to run a target-architecture test binary on the host.
$script = @"
call "$vcvars" >nul || exit /b 1
"$cmake" -S "$nativeRoot" -B "$buildDir" -G Ninja ^
  -DCMAKE_MAKE_PROGRAM="$ninja" ^
  -DCMAKE_BUILD_TYPE=$Configuration ^
  -DCMAKE_SYSTEM_NAME=Windows ^
  -DCMAKE_SYSTEM_PROCESSOR=$systemProcessor ^
  -DBUILD_SHARED_LIBS=ON ^
  -DQEDGE_CIPHER=$cipherFlag ^
  -DQEDGE_BUILD_SHA=$BuildSha || exit /b 1
"$cmake" --build "$buildDir" --config $Configuration || exit /b 1
"@

$batch = Join-Path $env:TEMP ("qedge-build-{0}.bat" -f ([guid]::NewGuid().ToString('N')))
Set-Content -Path $batch -Value $script -Encoding ascii
try {
    & cmd.exe /c $batch
    if ($LASTEXITCODE -ne 0) { throw "native build failed with exit code $LASTEXITCODE" }
}
finally {
    Remove-Item $batch -ErrorAction SilentlyContinue
}

$built = Join-Path $buildDir "out\$dllName"
if (-not (Test-Path $built)) { throw "expected $built" }
Copy-Item $built (Join-Path $outDir $dllName) -Force

Write-Host "OK: $(Join-Path $outDir $dllName)"
