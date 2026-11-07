param(
    [string]$LiveSplitDirectory = (Join-Path $PSScriptRoot '..\..\LiveSplit\bin\Release'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\settings-controls-verification'),
    [string]$Compiler
)
$ErrorActionPreference = 'Stop'
if (-not $Compiler) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $Compiler = & $vswhere -latest -products '*' -find 'MSBuild\**\Bin\Roslyn\csc.exe' | Select-Object -First 1
}
if (-not $Compiler -or -not (Test-Path -LiteralPath $Compiler)) { throw 'A Visual Studio C# compiler is required.' }
$LiveSplitDirectory = (Resolve-Path -LiteralPath $LiveSplitDirectory).Path
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
foreach ($architecture in @('x64', 'x86')) {
    $runtime = Join-Path $OutputDirectory $architecture
    New-Item -ItemType Directory -Force $runtime | Out-Null
    # Only managed dependencies are needed. Never stage GPU/native OCR runtime,
    # shader files, or a model: regressions cannot initialize a real recognizer.
    Get-ChildItem -LiteralPath $LiveSplitDirectory -File -Filter '*.dll' | Copy-Item -Destination $runtime -Force
    Copy-Item -LiteralPath (Join-Path $LiveSplitDirectory 'Components\LiveSplit.CrashTWoCLoadRemoval.dll') -Destination $runtime -Force
    Copy-Item -LiteralPath (Join-Path $LiveSplitDirectory 'Components\Tesseract.dll') -Destination $runtime -Force
    $program = Join-Path $runtime 'SettingsControlsVerification.exe'
    & $Compiler /nologo /target:exe "/platform:$architecture" "/out:$program" /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Xml.dll /r:System.Core.dll "/r:$runtime\LiveSplit.Core.dll" "/r:$runtime\UpdateManager.dll" "/r:$runtime\LiveSplit.CrashTWoCLoadRemoval.dll" "/r:$runtime\Tesseract.dll" "$PSScriptRoot\SettingsControlsVerification.cs"
    if ($LASTEXITCODE -ne 0) { throw "Settings controls test compile failed ($architecture)" }
    & $program 2>&1 | Tee-Object -FilePath (Join-Path $OutputDirectory "settings-controls-$architecture.log")
    if ($LASTEXITCODE -ne 0) { throw "Settings controls verification failed ($architecture); see $OutputDirectory\settings-controls-$architecture.log" }
}
