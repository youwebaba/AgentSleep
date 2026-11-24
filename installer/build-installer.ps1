[CmdletBinding()]
param(
    [string] $MakeNsis,
    [string] $OutputPath
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$build = Join-Path $repo 'build'
if (!$OutputPath) {
    $OutputPath = Join-Path $build 'AgentSleep-Setup-v1.0-windows-x64.exe'
}
$OutputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)

if (!$MakeNsis) {
    $localCompiler = '3.13', '3.12' | ForEach-Object {
        Join-Path $build "installer-tools/nsis-$_/makensis.exe"
    } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    $pathCompiler = Get-Command makensis.exe -ErrorAction SilentlyContinue
    if ($localCompiler) { $MakeNsis = $localCompiler }
    elseif ($pathCompiler) { $MakeNsis = $pathCompiler.Source }
    else {
        $cache = Join-Path $env:LOCALAPPDATA 'electron-builder/Cache/nsis-3.0.4.1'
        if (Test-Path -LiteralPath $cache -PathType Container) {
            $cached = Get-ChildItem -LiteralPath $cache -Directory | ForEach-Object {
                Join-Path $_.FullName 'makensis.exe'
            } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
            if ($cached) { $MakeNsis = $cached }
        }
    }
}
if (!$MakeNsis -or !(Test-Path -LiteralPath $MakeNsis -PathType Leaf)) {
    throw 'Supply -MakeNsis with the path to the NSIS compiler. NSIS 3.13 is recommended.'
}
$MakeNsis = (Resolve-Path -LiteralPath $MakeNsis).Path

$portable = 'builds/windows-x64/AgentSleep.exe', 'builds/windows-x64/AgentSleep.DryRun.exe'
foreach ($file in ($portable + @('LICENSE', 'installer/Recovery.txt', 'fixture/index.html'))) {
    $inputPath = [IO.Path]::GetFullPath((Join-Path $repo $file))
    if (!(Test-Path -LiteralPath $inputPath -PathType Leaf)) { throw "Missing installer input: $file" }
    if ($OutputPath -eq $inputPath) { throw 'Installer output cannot overwrite a payload file.' }
}

if (Test-Path -LiteralPath (Join-Path $repo '.git')) {
    & git -C $repo ls-files --error-unmatch -- @portable | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'The Windows x64 portable binaries must be tracked before packaging.' }
    & git -C $repo diff --quiet HEAD -- @portable
    if ($LASTEXITCODE -ne 0) { throw 'The portable binaries must match the tracked HEAD versions before packaging.' }
}

New-Item -ItemType Directory -Path (Split-Path -Parent $OutputPath) -Force | Out-Null
Write-Output ("NSIS compiler: " + (& $MakeNsis /VERSION))
& $MakeNsis /NOCONFIG /V2 "/DREPO_ROOT=$repo" "/DSETUP_OUTPUT=$OutputPath" (Join-Path $PSScriptRoot 'AgentSleep.nsi')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256
