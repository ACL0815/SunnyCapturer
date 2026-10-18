param([switch]$Acquire)
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$toolRoot = Join-Path $workspace '.tools\reverse'
$reportRoot = Join-Path $workspace 'docs\kairos\reverse\native'
$runtimeRoot = 'C:\Users\Axel_desktop\AppData\Local\KairosCapture\ReverseTools\97cc3309-5721-48c0-984b-21dc6de14109'
$assets = @(
    [pscustomobject]@{ name='ghidra_12.1.4_PUBLIC_20260921.zip'; bytes=569649598; sha256='ddac49f903da9d5bac833e5cc79395098b9c33cfd3279be5f31bd00387d2d4db'; url='https://github.com/NationalSecurityAgency/ghidra/releases/download/Ghidra_12.1.4_build/ghidra_12.1.4_PUBLIC_20260921.zip'; extract='ghidra' },
    [pscustomobject]@{ name='OpenJDK21U-jdk_x64_windows_hotspot_21.0.12.1_1.zip'; bytes=205073461; sha256='f9d6e191ab098c0d416e7d588a24420a8621cd2f4720dab2459b8b7b2d2d8b4e'; url='https://github.com/adoptium/temurin21-binaries/releases/download/jdk-21.0.12.1%2B1/OpenJDK21U-jdk_x64_windows_hotspot_21.0.12.1_1.zip'; extract='jdk' }
)
$plan = [ordered]@{
    target='C:\Users\Axel_desktop\AppData\Local\Programs\SunnyCapturer\SunnyCapturer.exe'
    targetSha256='e97cc2612b79ab154076844a13ec01cdeb70be0f14cf20675ce0e39dff81ecc8'
    toolRoot=$toolRoot; runtimeRoot=$runtimeRoot; freeBytes=(Get-PSDrive C).Free; assets=$assets
    node='C:\Users\Axel_desktop\.t3\projects\clockify\.rea-runtime\node-v24.18.0-win-x64\node.exe'
    rea='C:\Users\Axel_desktop\.t3\projects\clockify\.rea-runtime\node_modules\rea-agents\scripts\rea.mjs'
    sources=@('https://github.com/NationalSecurityAgency/ghidra/releases/tag/Ghidra_12.1.4_build','https://raw.githubusercontent.com/NationalSecurityAgency/ghidra/Ghidra_12.1.4_build/GhidraDocs/GettingStarted.md','https://api.adoptium.net/v3/assets/latest/21/hotspot?architecture=x64&image_type=jdk&os=windows&vendor=eclipse')
    changes='New portable files only; no overwrite, global install, registry, persistent PATH, MCP config, package setup, or target execution.'
    pathConstraint='REA rejects dot-prefixed Ghidra project ancestors. Root scope correction authorizes runtimeRoot as an ordinary resolved new runtime/project directory. No short names, junctions, drive mappings or aliases; target admission remains binding.'
}
$plan | ConvertTo-Json -Depth 8
if (-not $Acquire) { return }
foreach ($asset in $assets) {
    foreach ($candidate in @((Join-Path $toolRoot $asset.name),(Join-Path $toolRoot $asset.extract))) {
        if (Test-Path -LiteralPath $candidate) { throw "Refusing to overwrite $candidate" }
    }
}
New-Item -ItemType Directory -Path $toolRoot -Force | Out-Null
New-Item -ItemType Directory -Path $reportRoot -Force | Out-Null
$planPath = Join-Path $reportRoot 'setup-plan.json'
if (-not (Test-Path -LiteralPath $planPath)) {
    $plan | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $planPath -Encoding UTF8
}
foreach ($asset in $assets) {
    $archive = Join-Path $toolRoot $asset.name
    & curl.exe --fail --location --retry 1 --connect-timeout 30 --speed-time 60 --speed-limit 1 --silent --show-error --output $archive $asset.url
    if ($LASTEXITCODE -ne 0) { throw "Download failed: $($asset.name); stop after at most one retry." }
    if ((Get-Item -LiteralPath $archive).Length -ne $asset.bytes) { throw "Size mismatch: $archive" }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $asset.sha256) { throw "SHA256 mismatch: $archive" }
    Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $toolRoot $asset.extract)
}
Write-Output "Portable tools verified and extracted under $toolRoot; runtime/project directory: $runtimeRoot."
