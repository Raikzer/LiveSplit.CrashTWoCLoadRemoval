param([string]$Python = "python")
$ErrorActionPreference = "Stop"
$Venv = Join-Path $PSScriptRoot ".venv"
if (!(Test-Path -LiteralPath $Venv)) {
    & $Python -m venv $Venv
    if ($LASTEXITCODE -ne 0) { throw "Creating the local Python environment failed." }
}
$LocalPython = Join-Path $Venv "Scripts\python.exe"
$Requirements = Join-Path $PSScriptRoot "requirements-lock.txt"
if (!(Test-Path -LiteralPath $Requirements)) { $Requirements = Join-Path $PSScriptRoot "requirements.txt" }
& $LocalPython -m pip install -r $Requirements
if ($LASTEXITCODE -ne 0) { throw "Installing training dependencies failed." }
Write-Host "Ready: $LocalPython"
