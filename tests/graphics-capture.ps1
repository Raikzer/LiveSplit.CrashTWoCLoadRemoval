[CmdletBinding()]
param(
    [string]$LiveSplitDirectory = (Join-Path $PSScriptRoot '..\..\LiveSplit\bin\Release'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\graphics-capture-verification'),
    [string]$Compiler,
    [ValidateSet('x64', 'x86')][string[]]$Architecture = @('x64', 'x86'),
    [switch]$LiveNativeSmoke,
    [switch]$NativeOnly
)
$ErrorActionPreference = 'Stop'
if ($NativeOnly -and -not $LiveNativeSmoke) { throw '-NativeOnly requires -LiveNativeSmoke (owned synthetic window capture).' }
if (-not $Compiler) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $Compiler = & $vswhere -latest -products '*' -find 'MSBuild\**\Bin\Roslyn\csc.exe' | Select-Object -First 1
}
if (-not $Compiler -or -not (Test-Path -LiteralPath $Compiler)) { throw 'A Visual Studio C# compiler is required.' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $NativeOnly) { $LiveSplitDirectory = (Resolve-Path -LiteralPath $LiveSplitDirectory).Path }
foreach ($arch in $Architecture) {
    if (-not $NativeOnly) {
        $runtime = Join-Path $OutputDirectory "managed-$arch"
        New-Item -ItemType Directory -Force $runtime | Out-Null
        Get-ChildItem -LiteralPath $LiveSplitDirectory -File -Filter '*.dll' | Copy-Item -Destination $runtime -Force
        Copy-Item -LiteralPath (Join-Path $LiveSplitDirectory 'Components\LiveSplit.CrashTWoCLoadRemoval.dll') -Destination $runtime -Force
        Copy-Item -LiteralPath (Join-Path $LiveSplitDirectory 'Components\Tesseract.dll') -Destination $runtime -Force
        # Deliberately do not copy the WGC DLL into this runtime. Provider factories
        # and synthetic bitmaps must suffice for all default verification.
        $program = Join-Path $runtime 'GraphicsCaptureVerification.exe'
        & $Compiler /nologo /target:exe "/platform:$arch" "/out:$program" /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Xml.dll /r:System.Core.dll "/r:$runtime\LiveSplit.Core.dll" "/r:$runtime\UpdateManager.dll" "/r:$runtime\LiveSplit.CrashTWoCLoadRemoval.dll" "/r:$runtime\Tesseract.dll" "$PSScriptRoot\GraphicsCaptureVerification.cs"
        if ($LASTEXITCODE -ne 0) { throw "Managed capture test compile failed ($arch)" }
        & $program 2>&1 | Tee-Object -FilePath (Join-Path $OutputDirectory "managed-$arch.log")
        if ($LASTEXITCODE -ne 0) { throw "Managed capture verification failed ($arch)." }
    }
    if ($LiveNativeSmoke) {
        $runtime = Join-Path $OutputDirectory "native-$arch"
        New-Item -ItemType Directory -Force $runtime | Out-Null
        $nativeArch = if ($arch -eq 'x86') { 'Win32' } else { 'x64' }
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot "..\capture-native\bin\$nativeArch\TwocWindowsGraphicsCapture.dll") -Destination $runtime -Force
        $program = Join-Path $runtime 'GraphicsCaptureNativeSmoke.exe'
        $managedArguments = @()
        if (-not $NativeOnly) {
            Copy-Item -LiteralPath (Join-Path $LiveSplitDirectory 'Components\LiveSplit.CrashTWoCLoadRemoval.dll') -Destination $runtime -Force
            $componentNative = Join-Path $runtime "Components\$arch"
            New-Item -ItemType Directory -Force $componentNative | Out-Null
            Copy-Item -LiteralPath (Join-Path $runtime 'TwocWindowsGraphicsCapture.dll') -Destination $componentNative -Force
            $managedArguments = @('/define:MANAGED_CAPTURE', "/r:$runtime\LiveSplit.CrashTWoCLoadRemoval.dll")
        }
        & $Compiler /nologo /target:exe "/platform:$arch" "/out:$program" /r:System.Drawing.dll /r:System.Windows.Forms.dll @managedArguments "$PSScriptRoot\GraphicsCaptureNativeSmoke.cs"
        if ($LASTEXITCODE -ne 0) { throw "Native smoke harness compile failed ($arch)" }
        & $program 2>&1 | Tee-Object -FilePath (Join-Path $OutputDirectory "native-$arch.log")
        if ($LASTEXITCODE -ne 0) { throw "Native owned-window smoke failed ($arch); see $OutputDirectory\native-$arch.log. No alternate target was captured." }
    }
}
