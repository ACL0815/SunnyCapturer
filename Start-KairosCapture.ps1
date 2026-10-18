[CmdletBinding()]
param([switch]$Demo, [string]$DotnetPath)

$ErrorActionPreference = 'Stop'
$kairosExecutable = Join-Path $PSScriptRoot 'artifacts\KairosCapture-0.2.0-win-x64\KairosCapture.exe'
if (-not (Test-Path -LiteralPath $kairosExecutable)) {
    if ($DotnetPath) { & (Join-Path $PSScriptRoot 'scripts\build.ps1') -DotnetPath $DotnetPath }
    else { & (Join-Path $PSScriptRoot 'scripts\build.ps1') }
}
if (-not (Test-Path -LiteralPath $kairosExecutable -PathType Leaf)) { throw 'KairosCapture.exe wurde nicht erzeugt.' }
if ($Demo) {
    Start-Process -FilePath $kairosExecutable -ArgumentList '--demo'
}
else { Start-Process -FilePath $kairosExecutable }
