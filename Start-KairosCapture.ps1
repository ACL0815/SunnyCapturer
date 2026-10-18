[CmdletBinding()]
param([switch]$Demo)

$ErrorActionPreference = 'Stop'
$kairosExecutable = Join-Path $PSScriptRoot 'artifacts\KairosCapture-win-x64\KairosCapture.exe'
if (-not (Test-Path -LiteralPath $kairosExecutable)) {
    & (Join-Path $PSScriptRoot 'scripts\build.ps1')
}
if ($Demo) {
    Start-Process -FilePath $kairosExecutable -ArgumentList '--demo'
}
else { Start-Process -FilePath $kairosExecutable }
