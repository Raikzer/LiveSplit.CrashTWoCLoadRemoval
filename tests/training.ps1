param(
    [string]$LiveSplitDirectory = (Join-Path $PSScriptRoot '..\..\LiveSplit\bin\Release'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\training-verification'),
    [string]$Compiler,
    [switch]$RequireKeyboardFocus
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
    Copy-Item "$LiveSplitDirectory\*" $runtime -Recurse -Force
    Copy-Item "$LiveSplitDirectory\Components\LiveSplit.CrashTWoCLoadRemoval.dll" $runtime -Force
    Copy-Item "$LiveSplitDirectory\Components\Tesseract.dll" $runtime -Force
    $program = Join-Path $runtime 'TrainingVerification.exe'
    & $Compiler /nologo /target:exe "/platform:$architecture" "/out:$program" /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Xml.dll /r:System.Xml.Linq.dll "/r:$runtime\LiveSplit.Core.dll" "/r:$runtime\UpdateManager.dll" "/r:$runtime\LiveSplit.CrashTWoCLoadRemoval.dll" "$PSScriptRoot\TrainingVerification.cs"
    if ($LASTEXITCODE -ne 0) { throw "Training test compile failed ($architecture)" }
    $programArguments = if ($RequireKeyboardFocus) { @('--require-keyboard-focus') } else { @() }
    & $program @programArguments 2>&1 | Tee-Object -FilePath (Join-Path $OutputDirectory "training-$architecture.log")
    if ($LASTEXITCODE -ne 0) { throw "Training verification failed ($architecture); see $OutputDirectory\training-$architecture.log" }
}
