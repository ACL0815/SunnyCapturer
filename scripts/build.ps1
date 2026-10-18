[CmdletBinding()]
param([switch]$SkipChecks, [switch]$BuildOnly)

$ErrorActionPreference = 'Stop'
$kairosRoot = Split-Path -Parent $PSScriptRoot
$kairosProject = Join-Path $kairosRoot 'src\KairosCapture\KairosCapture.csproj'
$kairosLocalDotnet = Join-Path $env:LOCALAPPDATA 'KairosCapture\dotnet\dotnet.exe'
$kairosDotnet = if (Test-Path -LiteralPath $kairosLocalDotnet) { $kairosLocalDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
$kairosSdks = & $kairosDotnet --list-sdks
if (-not ($kairosSdks -match '^10\.')) {
    throw 'Das .NET 10 SDK fehlt. Installiere es von https://dotnet.microsoft.com/download/dotnet/10.0 und starte dieses Skript erneut.'
}

Push-Location -LiteralPath $kairosRoot
try {
    & $kairosDotnet build $kairosProject -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build fehlgeschlagen.' }
    if (-not $SkipChecks) {
        $kairosChecks = Join-Path $kairosRoot 'test-results'
        $kairosAssembly = Join-Path $kairosRoot 'src\KairosCapture\bin\Release\net10.0-windows\KairosCapture.dll'
        & $kairosDotnet $kairosAssembly --self-check $kairosChecks
        if ($LASTEXITCODE -ne 0) { throw "Prüfung fehlgeschlagen. Bericht: $kairosChecks" }
    }
    if (-not $BuildOnly) {
        $kairosOutput = Join-Path $kairosRoot 'artifacts\KairosCapture-win-x64'
        & $kairosDotnet publish $kairosProject -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true --nologo -o $kairosOutput
        if ($LASTEXITCODE -ne 0) { throw 'Veröffentlichungs-Build fehlgeschlagen.' }
        Write-Host "Startbereit: $(Join-Path $kairosOutput 'KairosCapture.exe')"
    }
}
finally { Pop-Location }
