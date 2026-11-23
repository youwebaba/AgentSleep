[CmdletBinding()]
param([switch] $Test, [switch] $DryRunOnly)

$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'The 64-bit inbox .NET Framework C# compiler was not found.'
}

$output = Join-Path $PSScriptRoot 'build'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' -File | ForEach-Object FullName)
$manifest = Join-Path $PSScriptRoot 'src/app.manifest'
$references = '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll'
$common = '/nologo', '/langversion:5', '/platform:x64', '/optimize+', '/warn:4', '/utf8output'
if ($DryRunOnly) {
    $app = Join-Path $output 'AgentSleep.DryRun.exe'
    $drySources = @($sources | Where-Object { (Split-Path -Leaf $_) -like 'Displays*.cs' })
    $drySources += Join-Path $PSScriptRoot 'src/TrayDryRun.cs'
    & $compiler @common /define:DRY_RUN_ONLY /target:exe /main:AgentSleep.TrayDryRun "/win32manifest:$manifest" "/out:$app" @drySources
} else {
    $app = Join-Path $output 'AgentSleep.exe'
    & $compiler @common @references /target:winexe /main:AgentSleep.Program "/win32manifest:$manifest" "/out:$app" @sources
}
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Output $app

if ($Test) {
    $tests = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'tests') -Filter '*.cs' -File | ForEach-Object FullName)
    if ($DryRunOnly) {
        $testApp = Join-Path $output 'AgentSleep.DryRun.Tests.exe'
        $testSources = @($sources | Where-Object { (Split-Path -Leaf $_) -match '^(Displays|Recovery|TrayDryRun)' })
        $tests = @($tests | Where-Object { (Split-Path -Leaf $_) -ne 'TrayTests.cs' })
        & $compiler @common @references /define:DRY_RUN_ONLY /target:exe /main:AgentSleep.TestProgram "/out:$testApp" @testSources @tests
    } else {
        $testApp = Join-Path $output 'AgentSleep.Tests.exe'
        & $compiler @common @references /target:exe /main:AgentSleep.TestProgram "/out:$testApp" @sources @tests
    }
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $testApp
    exit $LASTEXITCODE
}
