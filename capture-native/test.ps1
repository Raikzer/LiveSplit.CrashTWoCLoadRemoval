[CmdletBinding()]
param([string]$MSBuild)
$ErrorActionPreference = 'Stop'
if (-not $MSBuild) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'vswhere was not found; pass -MSBuild.' }
    $MSBuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
}
if (-not $MSBuild -or -not (Test-Path -LiteralPath $MSBuild)) { throw 'MSBuild was not found; pass -MSBuild.' }
& (Join-Path $PSScriptRoot 'build.ps1') -MSBuild $MSBuild
foreach ($platform in @('Win32', 'x64')) {
    & $MSBuild (Join-Path $PSScriptRoot 'tests\NonCaptureTests.vcxproj') /nologo /m /v:minimal /p:Configuration=Release "/p:Platform=$platform"
    if ($LASTEXITCODE -ne 0) { throw "Non-capture test build failed for $platform." }
    & (Join-Path $PSScriptRoot "tests\bin\$platform\NonCaptureTests.exe") (Join-Path $PSScriptRoot "bin\$platform\TwocWindowsGraphicsCapture.dll")
    if ($LASTEXITCODE -ne 0) { throw "Non-capture tests failed for $platform." }
}
