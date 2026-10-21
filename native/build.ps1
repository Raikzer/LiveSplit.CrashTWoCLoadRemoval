[CmdletBinding()]
param(
    [string]$VulkanSdk = $env:VULKAN_SDK,
    [string]$MSBuild,
    [ValidateSet('x64', 'x86')][string[]]$Architecture = @('x64', 'x86'),
    [switch]$Test,
    [switch]$Validation
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $VulkanSdk) {
    $sdk = Get-ChildItem -LiteralPath 'C:\VulkanSDK' -Directory -ErrorAction SilentlyContinue |
        Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    if ($sdk) { $VulkanSdk = $sdk.FullName }
}
if (-not $VulkanSdk -or -not (Test-Path -LiteralPath (Join-Path $VulkanSdk 'Include\vulkan\vulkan.h'))) {
    throw 'A Vulkan SDK is required. Set VULKAN_SDK or pass -VulkanSdk.'
}
if (-not $MSBuild) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $MSBuild = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'MSBuild\**\Bin\MSBuild.exe' |
            Select-Object -First 1
    }
    if (-not $MSBuild) {
        $command = Get-Command MSBuild.exe -ErrorAction SilentlyContinue
        if ($command) { $MSBuild = $command.Source }
    }
}
if (-not $MSBuild -or -not (Test-Path -LiteralPath $MSBuild)) {
    throw 'Visual Studio C++ build tools (v143) and MSBuild are required. Pass -MSBuild if necessary.'
}
$shaderOutput = Join-Path $PSScriptRoot 'build\shaders'
New-Item -ItemType Directory -Path $shaderOutput -Force | Out-Null
$compiler = Join-Path $VulkanSdk 'Bin\glslc.exe'
$validator = Join-Path $VulkanSdk 'Bin\spirv-val.exe'
foreach ($shader in @('conv1', 'conv2', 'classifier')) {
    & $compiler --target-env=vulkan1.0 -O (Join-Path $PSScriptRoot "shaders\$shader.comp") -o (Join-Path $shaderOutput "$shader.spv")
    if ($LASTEXITCODE -ne 0) { throw "Shader compilation failed: $shader" }
    & $validator --target-env vulkan1.0 (Join-Path $shaderOutput "$shader.spv")
    if ($LASTEXITCODE -ne 0) { throw "SPIR-V validation failed: $shader" }
}
foreach ($arch in $Architecture) {
    $platform = if ($arch -eq 'x86') { 'Win32' } else { 'x64' }
    & $MSBuild (Join-Path $PSScriptRoot 'TwocLoadingVulkan.vcxproj') /nologo /verbosity:minimal "/p:Configuration=Release" "/p:Platform=$platform" "/p:VulkanSdk=$VulkanSdk"
    if ($LASTEXITCODE -ne 0) { throw "Native $arch build failed." }
    if ($Test) {
        & $MSBuild (Join-Path $PSScriptRoot 'NativeTests.vcxproj') /nologo /verbosity:minimal "/p:Configuration=Release" "/p:Platform=$platform"
        if ($LASTEXITCODE -ne 0) { throw "Native $arch test build failed." }
        $testArguments = @($shaderOutput)
        if ($Validation) { $testArguments += '--validation' }
        & (Join-Path $PSScriptRoot "build\$arch\NativeTests.exe") @testArguments
        if ($LASTEXITCODE -ne 0) { throw "Native $arch tests failed." }
    }
}
