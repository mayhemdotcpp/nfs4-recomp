# Recompiles the game, applies the patches and publishes it into build\<runtime>\.
# .\build.ps1                      Windows build in build\win-x64\
# .\build.ps1 -Runtime linux-x64   Linux build in build\linux-x64\ (also works from Windows)
param(
    [string]$Runtime = 'win-x64',
    [switch]$Recompiler
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root "build/$Runtime"
$recompilerDir = Join-Path $root 'external/RecompOne/RecompOne.Recompiler'
$recompilerProj = Join-Path $recompilerDir 'RecompOne.Recompiler.csproj'
$recompilerDll = Join-Path $recompilerDir 'bin/Release/net10.0/recompone.dll'

# csc.exe may crash on some older Windows builds, runs the compiler through dotnet.exe instead
$csc = @()
if ($env:OS -eq 'Windows_NT') { $csc = '-p:UseSharedCompilation=false', "-p:CscToolPath=$root", '-p:CscToolExe=csc.cmd' }

if (-not (Test-Path $recompilerProj)) {
    git -C $root submodule update --init --recursive
    if ($LASTEXITCODE -ne 0) { throw 'fetching the RecompOne submodule failed, run: git submodule update --init --recursive' }
}

if ($Recompiler -or -not (Test-Path $recompilerDll)) {
    dotnet build $recompilerProj -c Release @csc
    if ($LASTEXITCODE -ne 0) { throw 'building the recompiler failed' }
}

$discDir = Join-Path $root 'disc'
$cue = @(Get-ChildItem $discDir -Filter *.cue -File)

Push-Location (Join-Path $root 'config')
try { dotnet $recompilerDll nfs4.json -disc $cue[0].FullName | Where-Object { $_ -notmatch '^\[Unknown\]' } }
finally { Pop-Location }
if ($LASTEXITCODE -ne 0) { throw 'recompile failed' }

$python = if (Get-Command python -ErrorAction SilentlyContinue) { 'python' } else { 'python3' }
& $python -I (Join-Path $root 'tools/apply_patches.py')
if ($LASTEXITCODE -ne 0) { throw 'applying the patches failed' }

dotnet publish (Join-Path $root 'nfs4.csproj') -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:DebugType=none -p:NoConsole=true @csc -o $out
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

$outDisc = Join-Path $out 'disc'
New-Item -ItemType Directory -Force $outDisc | Out-Null
Get-ChildItem $discDir -File | Where-Object { $_.Extension -in '.cue', '.bin' } | ForEach-Object {
    if (-not (Test-Path (Join-Path $outDisc $_.Name))) {
        Write-Host "copying $($_.Name)"
        Copy-Item $_.FullName $outDisc
    }
}
Write-Host "built to $out"
