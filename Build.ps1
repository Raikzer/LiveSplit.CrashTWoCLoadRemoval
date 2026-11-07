[CmdletBinding()]
param(
    [string]$LiveSplitRoot = (Join-Path $PSScriptRoot '..\LiveSplit'),
    [string]$PackagesRoot = (Join-Path $PSScriptRoot '..\packages'),
    [string]$ModelPath = (Join-Path $PSScriptRoot 'ml\models\eng-ps2-ghost-town\loading-text.twml'),
    [string]$MSBuild,
    [string]$Dotnet,
    [string]$Python = 'python',
    [switch]$BuildHost,
    [switch]$TrainDemo,
    [switch]$Test
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$LiveSplitRoot = [IO.Path]::GetFullPath($LiveSplitRoot)
$PackagesRoot = [IO.Path]::GetFullPath($PackagesRoot)
$ModelPath = [IO.Path]::GetFullPath($ModelPath)
if (-not (Test-Path -LiteralPath (Join-Path $LiveSplitRoot 'LiveSplit.sln'))) {
    throw 'Clone LiveSplit 1.8.37 with recursive submodules, or pass -LiveSplitRoot to its source directory. See BUILDING-ML.md.'
}
if (-not $MSBuild) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $MSBuild = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
}
if (-not $MSBuild -or -not (Test-Path -LiteralPath $MSBuild)) { throw 'Visual Studio 2022 C++ tools and MSBuild are required.' }

if ($TrainDemo) {
    if (-not $PSBoundParameters.ContainsKey('ModelPath')) {
        $ModelPath = Join-Path $PSScriptRoot 'ml\artifacts\demo\loading-text.twml'
    }
    & (Join-Path $PSScriptRoot 'ml\setup.ps1') -Python $Python
    $trainingPython = Join-Path $PSScriptRoot 'ml\.venv\Scripts\python.exe'
    $demoData = Join-Path $PSScriptRoot 'ml\datasets\demo'
    if (-not (Test-Path -LiteralPath (Join-Path $demoData 'samples.csv'))) {
        & $trainingPython (Join-Path $PSScriptRoot 'ml\generate_demo.py') $demoData --groups 64 --per-group 12 --seed 86420
        if ($LASTEXITCODE -ne 0) { throw 'Synthetic data generation failed.' }
    }
    & $trainingPython (Join-Path $PSScriptRoot 'ml\train.py') (Join-Path $demoData 'samples.csv') --output (Join-Path $PSScriptRoot 'ml\artifacts\demo') --epochs 40 --patience 5 --seed 1234 --synthetic-demo
    if ($LASTEXITCODE -ne 0) { throw 'Demo training failed.' }
}
if (-not (Test-Path -LiteralPath $ModelPath)) { throw 'Model file missing. Pass -ModelPath or use -TrainDemo.' }

& (Join-Path $PSScriptRoot 'capture-native\build.ps1') -MSBuild $MSBuild
if ($Test) { & (Join-Path $PSScriptRoot 'capture-native\test.ps1') -MSBuild $MSBuild }
& (Join-Path $PSScriptRoot 'native\build.ps1') -MSBuild $MSBuild -Test:$Test

if ($BuildHost -or -not (Test-Path -LiteralPath (Join-Path $LiveSplitRoot 'bin\Release\LiveSplit.Core.dll'))) {
    if (-not $Dotnet) {
        $localDotnet = Join-Path $PSScriptRoot '..\dotnet\dotnet.exe'
        $Dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { 'dotnet' }
    }
    & $Dotnet build (Join-Path $LiveSplitRoot 'LiveSplit.sln') -c Release -p:DebugType=None
    if ($LASTEXITCODE -ne 0) { throw 'LiveSplit host build failed; .NET SDK 8 is required.' }
}

$nuget = Join-Path $PSScriptRoot '..\tools\nuget.exe'
if (-not (Test-Path -LiteralPath $nuget)) {
    $nuget = Join-Path $PSScriptRoot 'tools\nuget.exe'
    if (-not (Test-Path -LiteralPath $nuget)) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $nuget) | Out-Null
        Invoke-WebRequest -Uri 'https://dist.nuget.org/win-x86-commandline/v6.11.1/nuget.exe' -OutFile $nuget
    }
}
& $nuget restore (Join-Path $PSScriptRoot 'packages.config') -PackagesDirectory $PackagesRoot -NonInteractive -Verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Component package restore failed.' }

# Supply the targeting pack without changing a system-wide SDK installation.
$packDirectory = Join-Path $PackagesRoot 'Microsoft.NETFramework.ReferenceAssemblies.net481.1.0.3'
$referenceDirectory = Join-Path $packDirectory 'build\.NETFramework\v4.8.1'
if (-not (Test-Path -LiteralPath (Join-Path $referenceDirectory 'mscorlib.dll'))) {
    & $nuget install Microsoft.NETFramework.ReferenceAssemblies.net481 -Version 1.0.3 -OutputDirectory $PackagesRoot -NonInteractive -Verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw '.NET Framework reference assembly restore failed.' }
}
& $MSBuild (Join-Path $PSScriptRoot 'LiveSplit.CrashTWoCLoadRemoval.csproj') -nologo -v:minimal -t:Build -p:Configuration=Release -p:DebugType=None "-p:LiveSplitRoot=$LiveSplitRoot" "-p:PackagesRoot=$PackagesRoot" "-p:FrameworkPathOverride=$referenceDirectory" "-p:MlModelFile=$ModelPath"
if ($LASTEXITCODE -ne 0) { throw 'Managed component build failed.' }

$runtime = Join-Path $LiveSplitRoot 'bin\Release'
if ($Test) {
    & (Join-Path $PSScriptRoot 'tests\build.ps1') -LiveSplitDirectory $runtime -Compiler (Join-Path (Split-Path -Parent $MSBuild) 'Roslyn\csc.exe')
    & (Join-Path $PSScriptRoot 'tests\training.ps1') -LiveSplitDirectory $runtime -Compiler (Join-Path (Split-Path -Parent $MSBuild) 'Roslyn\csc.exe')
    & (Join-Path $PSScriptRoot 'tests\graphics-capture.ps1') -LiveSplitDirectory $runtime -Compiler (Join-Path (Split-Path -Parent $MSBuild) 'Roslyn\csc.exe')
    & (Join-Path $PSScriptRoot 'tests\settings-controls.ps1') -LiveSplitDirectory $runtime -Compiler (Join-Path (Split-Path -Parent $MSBuild) 'Roslyn\csc.exe')
}
Write-Output "Built component, x86/x64 Windows Graphics Capture and Vulkan runtimes, shaders and model in $runtime\Components"
