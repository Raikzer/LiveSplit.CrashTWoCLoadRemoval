param(
    [string]$LiveSplitDirectory = (Join-Path $PSScriptRoot '..\..\LiveSplit\bin\Release'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\ml-verification'),
    [string]$Compiler
)
$ErrorActionPreference = 'Stop'
if (-not $Compiler) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $Compiler = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'MSBuild\**\Bin\Roslyn\csc.exe' | Select-Object -First 1
}
if (-not $Compiler -or -not (Test-Path -LiteralPath $Compiler)) { throw 'A Visual Studio C# compiler with C++ build tools is required.' }
$vsRoot = Split-Path -Parent ([IO.Path]::GetFullPath($Compiler))
while ($vsRoot -and -not (Test-Path -LiteralPath (Join-Path $vsRoot 'VC\Auxiliary\Build'))) { $vsRoot = Split-Path -Parent $vsRoot }
if (-not $vsRoot) { throw 'Cannot find Visual Studio VC tools relative to the selected compiler.' }
$LiveSplitDirectory = (Resolve-Path $LiveSplitDirectory).Path
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$repoDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
foreach ($architecture in @('x64', 'x86')) {
    $runtime = Join-Path $OutputDirectory $architecture
    New-Item -ItemType Directory -Force $runtime | Out-Null
    Copy-Item "$LiveSplitDirectory\*" $runtime -Recurse -Force
    Copy-Item "$LiveSplitDirectory\Components\LiveSplit.CrashTWoCLoadRemoval.dll" $runtime -Force
    Copy-Item "$LiveSplitDirectory\Components\Tesseract.dll" $runtime -Force
    # The harness loads Tesseract beside its executable; its native loader resolves
    # these two OCR libraries relative to that managed assembly location.
    foreach ($ocrArchitecture in @('x64', 'x86')) {
        Copy-Item "$runtime\Components\$ocrArchitecture\leptonica-1.80.0.dll" "$runtime\$ocrArchitecture\" -Force
        Copy-Item "$runtime\Components\$ocrArchitecture\tesseract41.dll" "$runtime\$ocrArchitecture\" -Force
    }
    Copy-Item "$repoDirectory\ml\artifacts\demo\loading-text.twml" "$runtime\Components\ML\loading-text.twml" -Force
    Copy-Item "$repoDirectory\ml\artifacts\demo\parity-samples" $runtime -Recurse -Force
    foreach ($nativeArchitecture in @('x64', 'x86')) {
        $nativeBuild = Join-Path $repoDirectory "native\build\$nativeArchitecture\TwocLoadingVulkan.dll"
        if (Test-Path $nativeBuild) { Copy-Item $nativeBuild "$runtime\Components\$nativeArchitecture\TwocLoadingVulkan.dll" -Force }
    }
    $program = Join-Path $runtime 'ManagedMlVerification.exe'
    & $Compiler /nologo /target:exe "/platform:$architecture" "/out:$program" /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Xml.dll /r:System.Web.Extensions.dll "/r:$runtime\LiveSplit.Core.dll" "/r:$runtime\LiveSplit.CrashTWoCLoadRemoval.dll" "/r:$runtime\Tesseract.dll" "$PSScriptRoot\ManagedMlVerification.cs"
    if ($LASTEXITCODE -ne 0) { throw "C# compile failed ($architecture)" }
    & $program 2>&1 | Tee-Object -FilePath (Join-Path $OutputDirectory "managed-$architecture.log")
    if ($LASTEXITCODE -ne 0) { throw "Managed verification failed ($architecture); see $OutputDirectory\managed-$architecture.log" }
    $faultRuntime = Join-Path $OutputDirectory "fault-$architecture"
    New-Item -ItemType Directory -Force $faultRuntime | Out-Null
    Copy-Item "$runtime\*" $faultRuntime -Recurse -Force
    $vcvars = if ($architecture -eq 'x64') { 'vcvars64.bat' } else { 'vcvars32.bat' }
    $setup = Join-Path $vsRoot "VC\Auxiliary\Build\$vcvars"
    $batch = Join-Path $faultRuntime 'compile-fault.cmd'
    @"
@echo off
call "$setup" >nul
if errorlevel 1 exit /b 1
cl /nologo /LD /MT /EHsc "$PSScriptRoot\BlockingNative.cpp" /Fo"$faultRuntime\BlockingNative.obj" /link /DEF:"$PSScriptRoot\BlockingNative.def" /OUT:"$faultRuntime\Components\$architecture\TwocLoadingVulkan.dll" /IMPLIB:"$faultRuntime\BlockingNative.lib"
"@ | Set-Content -LiteralPath $batch -Encoding Ascii
    & $env:ComSpec /d /c $batch
    if ($LASTEXITCODE -ne 0) { throw "Fault fixture compile failed ($architecture)" }
    & "$faultRuntime\ManagedMlVerification.exe" --blocking-native 2>&1 | Tee-Object -FilePath (Join-Path $OutputDirectory "blocking-$architecture.log")
    if ($LASTEXITCODE -ne 0) { throw "Blocking cleanup verification failed ($architecture)" }
}
