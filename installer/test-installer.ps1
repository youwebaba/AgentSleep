[CmdletBinding()]
param([string] $SetupPath)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (!$SetupPath) { $SetupPath = Join-Path $repo 'build/AgentSleep-Setup-v1.0-windows-x64.exe' }
$setup = (Get-Item -LiteralPath $SetupPath).FullName
$install = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\AgentSleep'
$menu = Join-Path ([Environment]::GetFolderPath('Programs')) 'AgentSleep'
$app = Join-Path $install 'AgentSleep.exe'
$uninstaller = Join-Path $install 'Uninstall.exe'
$arpPath = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\AgentSleep'
$runPath = 'Software\Microsoft\Windows\CurrentVersion\Run'
$token = [Guid]::NewGuid().ToString('N')
$sentinel = Join-Path $install "AgentSleep-test-$token.txt"
$payload = @{
    'AgentSleep.exe' = 'builds/windows-x64/AgentSleep.exe'
    'AgentSleep.DryRun.exe' = 'builds/windows-x64/AgentSleep.DryRun.exe'
    'LICENSE' = 'LICENSE'
}
$arpExpected = @{
    DisplayName = 'AgentSleep'
    DisplayVersion = '1.0.0'
    DisplayIcon = $app
    InstallLocation = $install
    UninstallString = '"' + $uninstaller + '"'
    QuietUninstallString = '"' + $uninstaller + '" /S'
    NoModify = 1
    NoRepair = 1
}

function Assert-Equal($Actual, $Expected, [string] $Check) {
    if ($Actual -cne $Expected) { throw "Unexpected result for $Check." }
}

function Test-PathEntry([string] $Path) {
    try { [void][IO.File]::GetAttributes($Path); return $true }
    catch [IO.FileNotFoundException] { return $false }
    catch [IO.DirectoryNotFoundException] { return $false }
}

function Assert-LocalDirectory([string] $Path) {
    $item = Get-Item -LiteralPath $Path -Force
    if (!$item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'The test folder is not a regular directory.'
    }
}

function Invoke-Quiet([string] $Path, [string] $Arguments, [int] $Expected, [string] $Check) {
    $process = Start-Process -FilePath $Path -ArgumentList $Arguments -WindowStyle Hidden -Wait -PassThru
    try { Assert-Equal $process.ExitCode $Expected "$Check (exit code $($process.ExitCode), expected $Expected)" }
    finally { $process.Dispose() }
}

function Get-StartupEntry {
    $key = $userHive.OpenSubKey($runPath)
    if (!$key) { return }
    try {
        if ($key.GetValueNames() -contains 'AgentSleep') {
            [ordered]@{
                Kind = $key.GetValueKind('AgentSleep')
                Value = $key.GetValue('AgentSleep', $null, 'DoNotExpandEnvironmentNames')
            }
        }
    }
    finally { $key.Dispose() }
}

function Assert-InstalledPackage {
    foreach ($file in $payload.Keys) {
        $hash = (Get-FileHash -LiteralPath (Join-Path $install $file)).Hash
        Assert-Equal $hash $payloadHashes[$file] "$file SHA-256"
    }
    $key = $userHive.OpenSubKey($arpPath)
    if (!$key) { throw 'AgentSleep is missing from the current user uninstall registry.' }
    try {
        foreach ($name in $arpExpected.Keys) {
            Assert-Equal $key.GetValue($name) $arpExpected[$name] "uninstall registry $name"
        }
    }
    finally { $key.Dispose() }

    $shell = New-Object -ComObject WScript.Shell
    try {
        foreach ($label in @('AgentSleep', 'Restore AgentSleep Displays')) {
            $link = Join-Path $menu "$label.lnk"
            if (!(Test-Path -LiteralPath $link -PathType Leaf)) { throw "Missing Start menu shortcut $label." }
            $shortcut = $shell.CreateShortcut($link)
            try {
                Assert-Equal $shortcut.TargetPath $app "$label target"
                $arguments = if ($label -eq 'AgentSleep') { '' } else { '--restore' }
                Assert-Equal $shortcut.Arguments $arguments "$label arguments"
            }
            finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) }
        }
    }
    finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
}

$payloadHashes = @{}
foreach ($file in $payload.Keys) {
    $payloadHashes[$file] = (Get-FileHash -LiteralPath (Join-Path $repo $payload[$file])).Hash
}

$userHive = [Microsoft.Win32.RegistryKey]::OpenBaseKey('CurrentUser', 'Registry64')
$ownsSentinel = $false
$uninstallerHash = $null
$uninstalled = $false
try {
    foreach ($path in @($install, $menu)) {
        if (Test-PathEntry $path) {
            throw 'The app or its Start menu folder already exists. Use a Windows profile without an AgentSleep installation.'
        }
    }
    $key = $userHive.OpenSubKey($arpPath)
    if ($key) {
        $key.Dispose()
        throw 'An AgentSleep uninstaller is already registered for this Windows account.'
    }
    $startup = Get-StartupEntry
    if ($startup -and $startup.Value -is [string] -and
        [Environment]::ExpandEnvironmentVariables($startup.Value) -eq ('"' + $app + '"')) {
        throw 'The installed app path is already registered to start at sign-in. Use a fresh Windows profile.'
    }
    $startupBefore = ConvertTo-Json -InputObject $startup -Compress

    Invoke-Quiet $setup '/S' 0 'silent setup'
    Assert-LocalDirectory $install
    Assert-LocalDirectory $menu
    $uninstallerHash = (Get-FileHash -LiteralPath $uninstaller).Hash
    Assert-InstalledPackage
    Assert-Equal (ConvertTo-Json -InputObject (Get-StartupEntry) -Compress) $startupBefore 'sign-in entry after setup'

    $file = [IO.File]::Open($sentinel, 'CreateNew', 'Write', 'None')
    $ownsSentinel = $true
    try {
        $bytes = [Text.Encoding]::ASCII.GetBytes($token)
        $file.Write($bytes, 0, $bytes.Length)
    }
    finally { $file.Dispose() }

    $before = Get-Item -LiteralPath $app
    $created = $before.CreationTimeUtc
    $written = $before.LastWriteTimeUtc
    $locked = [IO.File]::Open($app, 'Open', 'Read', 'None')
    try {
        Invoke-Quiet $setup '/S' 2 'setup with the installed app locked'
        # In-place execution reports the busy exit code from the real uninstaller process.
        Invoke-Quiet $uninstaller "/S _?=$install" 2 'uninstall with the installed app locked'
    }
    finally { $locked.Dispose() }
    $after = Get-Item -LiteralPath $app
    Assert-Equal $after.CreationTimeUtc $created 'locked app creation time'
    Assert-Equal $after.LastWriteTimeUtc $written 'locked app write time'
    Assert-Equal (Get-FileHash -LiteralPath $uninstaller).Hash $uninstallerHash 'uninstaller checksum after busy checks'
    Assert-InstalledPackage
    Assert-Equal (ConvertTo-Json -InputObject (Get-StartupEntry) -Compress) $startupBefore 'sign-in entry after busy checks'

    Invoke-Quiet $uninstaller '/S' 0 'standard silent uninstall'
    $uninstalled = $true
    Assert-LocalDirectory $install
    Assert-Equal ([IO.File]::ReadAllText($sentinel)) $token 'independent file after uninstall'
    $remaining = @(Get-ChildItem -LiteralPath $install -Force)
    if ($remaining.Count -ne 1 -or $remaining[0].FullName -ne $sentinel -or
        (Test-PathEntry $menu)) {
        throw 'Uninstall left package files or Start menu entries.'
    }
    $key = $userHive.OpenSubKey($arpPath)
    if ($key) {
        $key.Dispose()
        throw 'The current user uninstall registration remained.'
    }
    Assert-Equal (ConvertTo-Json -InputObject (Get-StartupEntry) -Compress) $startupBefore 'sign-in entry after uninstall'
}
finally {
    try {
        if (!$uninstalled -and $uninstallerHash -and (Test-Path -LiteralPath $uninstaller -PathType Leaf)) {
            Assert-LocalDirectory $install
            if ((Get-FileHash -LiteralPath $uninstaller).Hash -eq $uninstallerHash) {
                Invoke-Quiet $uninstaller '/S' 0 'cleanup after a failed test'
            }
        }
        if ($ownsSentinel -and (Test-Path -LiteralPath $sentinel -PathType Leaf)) {
            Assert-LocalDirectory $install
            $file = Get-Item -LiteralPath $sentinel -Force
            if (!$file.PSIsContainer -and !($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -and
                [IO.File]::ReadAllText($sentinel) -ceq $token) {
                [IO.File]::Delete($sentinel)
                if (!(Get-ChildItem -LiteralPath $install -Force)) { [IO.Directory]::Delete($install) }
            }
        }
    }
    finally { $userHive.Dispose() }
}
'AgentSleep installer smoke test passed.'
