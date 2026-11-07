[CmdletBinding()]
param(
    [string]$MSBuild,
    [ValidateSet('Both', 'Win32', 'x64')][string]$Architecture = 'Both',
    [string]$WindowsSdkVersion = '10.0.22621.0'
)
$ErrorActionPreference = 'Stop'
if (-not $MSBuild) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'vswhere was not found. Install Visual Studio C++ v143 tools or pass -MSBuild.' }
    $MSBuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
}
if (-not $MSBuild -or -not (Test-Path -LiteralPath $MSBuild)) { throw 'MSBuild was not found. Install Visual Studio C++ v143 tools or pass -MSBuild.' }
$platforms = if ($Architecture -eq 'Both') { @('Win32', 'x64') } else { @($Architecture) }
foreach ($platform in $platforms) {
    & $MSBuild (Join-Path $PSScriptRoot 'TwocWindowsGraphicsCapture.vcxproj') /nologo /m /t:Build /p:Configuration=Release "/p:Platform=$platform" "/p:WindowsTargetPlatformVersion=$WindowsSdkVersion"
    if ($LASTEXITCODE -ne 0) { throw "Native Windows Graphics Capture build failed for $platform (exit $LASTEXITCODE)." }
}
