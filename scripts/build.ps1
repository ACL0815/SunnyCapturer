[CmdletBinding()]
param(
    [string]$DotnetPath,
    [switch]$SkipChecks,
    [switch]$BuildOnly
)

$ErrorActionPreference = 'Stop'
$kairosRoot = Split-Path -Parent $PSScriptRoot
$kairosProject = Join-Path $kairosRoot 'src\KairosCapture\KairosCapture.csproj'
$kairosPrivateDotnet = Join-Path $kairosRoot '.tools\dotnet\dotnet.exe'
$kairosSdkHintPath = Join-Path $kairosRoot '.tools\dotnet-sdk-path.txt'
# Git worktrees share the repository's already installed private SDK/cache.
$kairosCommonGit = & git -C $kairosRoot rev-parse --git-common-dir 2>$null
$kairosSharedRoot = if ($LASTEXITCODE -eq 0) { Split-Path -Parent ([IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($kairosCommonGit)) { $kairosCommonGit } else { Join-Path $kairosRoot $kairosCommonGit }))) } else { $kairosRoot }
$kairosSharedHint = Join-Path $kairosSharedRoot '.tools\dotnet-sdk-path.txt'
if (-not (Test-Path -LiteralPath $kairosSdkHintPath) -and (Test-Path -LiteralPath $kairosSharedHint)) { $kairosSdkHintPath = $kairosSharedHint }
if ($DotnetPath) {
    $kairosDotnet = (Get-Item -LiteralPath $DotnetPath -ErrorAction Stop).FullName
}
elseif (Test-Path -LiteralPath $kairosPrivateDotnet -PathType Leaf) {
    $kairosDotnet = $kairosPrivateDotnet
}
elseif ((Test-Path -LiteralPath $kairosSdkHintPath -PathType Leaf) -and
        (Test-Path -LiteralPath (Get-Content -LiteralPath $kairosSdkHintPath -Raw).Trim() -PathType Leaf)) {
    $kairosDotnet = (Get-Content -LiteralPath $kairosSdkHintPath -Raw).Trim()
}
else {
    $kairosCommand = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue
    if (-not $kairosCommand) { throw 'Kein passendes .NET-10-SDK gefunden. Vorhandene private SDK-Kopie mit -DotnetPath <dotnet.exe> angeben.' }
    $kairosDotnet = $kairosCommand.Source
}

$kairosEnvironment = @{
    DOTNET_CLI_HOME = Join-Path $kairosRoot '.tools\dotnet-home'
    NUGET_PACKAGES = Join-Path $kairosSharedRoot '.tools\nuget-packages'
    DOTNET_ROOT = Split-Path -Parent $kairosDotnet
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    DOTNET_NOLOGO = '1'
}
$kairosPreviousEnvironment = @{}
foreach ($kairosName in $kairosEnvironment.Keys) {
    $kairosPreviousEnvironment[$kairosName] = [Environment]::GetEnvironmentVariable($kairosName, 'Process')
    [Environment]::SetEnvironmentVariable($kairosName, $kairosEnvironment[$kairosName], 'Process')
}
Push-Location -LiteralPath $kairosRoot
try {
    $kairosSdk = & $kairosDotnet --version
    if ($LASTEXITCODE -ne 0 -or $kairosSdk -notmatch '^10\.0\.4\d\d$') {
        throw "Das von global.json angeforderte stabile .NET 10.0.4xx SDK fehlt in $kairosDotnet. Es wurde nichts installiert."
    }
    Write-Host "SDK: $kairosSdk ($kairosDotnet)"
    & $kairosDotnet build $kairosProject -c Release -r win-x64 --self-contained false --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Release-Build fehlgeschlagen.' }
    $kairosAssembly = Join-Path $kairosRoot 'src\KairosCapture\bin\Release\net10.0-windows\win-x64\KairosCapture.dll'
    if (-not $SkipChecks) {
        $kairosChecks = Join-Path $kairosRoot 'test-results'
        New-Item -ItemType Directory -Path $kairosChecks -Force | Out-Null
        & $kairosDotnet $kairosAssembly --self-test $kairosChecks
        $kairosCheckExit = $LASTEXITCODE
        $kairosPreview = Join-Path $kairosChecks 'settings-window.png'
        & $kairosDotnet $kairosAssembly --render-settings-preview $kairosPreview
        $kairosPreviewExit = $LASTEXITCODE
        $kairosOverlayPreview = Join-Path $kairosChecks 'overlay.png'
        & $kairosDotnet $kairosAssembly --render-overlay-preview $kairosOverlayPreview
        $kairosOverlayExit = $LASTEXITCODE
        foreach ($kairosExtra in @(@('--render-ocr-preview', 'ocr-result.png'), @('--render-tray-preview', 'tray-menu.png'))) {
            $kairosExtraPath = Join-Path $kairosChecks $kairosExtra[1]
            & $kairosDotnet $kairosAssembly $kairosExtra[0] $kairosExtraPath
            if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $kairosExtraPath)) { throw "Vorschau fehlgeschlagen: $kairosExtraPath" }
        }
        if ($kairosCheckExit -ne 0) { throw "Offline-Prüfung fehlgeschlagen (Exit $kairosCheckExit). Bericht: $kairosChecks\self-test.json" }
        if ($kairosPreviewExit -ne 0 -or -not (Test-Path -LiteralPath $kairosPreview)) {
            throw "WPF-Vorschau fehlgeschlagen (Exit $kairosPreviewExit)."
        }
        if ($kairosOverlayExit -ne 0 -or -not (Test-Path -LiteralPath $kairosOverlayPreview)) {
            throw "Die Vorschau des Aufnahme-Overlays ist fehlgeschlagen (Exit $kairosOverlayExit)."
        }
        $kairosReport = Get-Content -LiteralPath (Join-Path $kairosChecks 'self-test.json') -Raw | ConvertFrom-Json
        if (-not $kairosReport.passed) { throw 'Der Offline-Bericht enthält fehlgeschlagene oder fehlende Prüfungen.' }
        Write-Host "Offline-Prüfungen bestanden. Aufnahme-Overlay: $kairosOverlayPreview"
    }
    if (-not $BuildOnly) {
        $kairosOutput = Join-Path $kairosRoot 'artifacts\KairosCapture-0.4.0-win-x64'
        & $kairosDotnet publish $kairosProject -c Release -r win-x64 --self-contained false --no-restore --nologo -o $kairosOutput
        if ($LASTEXITCODE -ne 0) { throw 'Frameworkabhängiger Publish-Build fehlgeschlagen.' }
        Write-Host "Startbereit: $(Join-Path $kairosOutput 'KairosCapture.exe')"
    }
}
finally {
    Pop-Location
    foreach ($kairosName in $kairosPreviousEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($kairosName, $kairosPreviousEnvironment[$kairosName], 'Process')
    }
}
