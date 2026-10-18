[CmdletBinding()]
param(
    [string]$Executable = "$env:LOCALAPPDATA\Programs\SunnyCapturer\SunnyCapturer.exe",
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\..\docs\kairos\reverse\runtime-modules.json')
)

$ErrorActionPreference = 'Stop'
$sunnyPath = (Get-Item -LiteralPath $Executable).FullName
$sunnyRoot = Split-Path -Parent $sunnyPath
$sunnyProcesses = @(Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($sunnyPath)) -ErrorAction Stop |
    Where-Object { $_.Path -eq $sunnyPath })
if ($sunnyProcesses.Count -ne 1) { throw "Expected exactly one process for $sunnyPath; found $($sunnyProcesses.Count)." }
$sunnyProcess = $sunnyProcesses[0]
$sunnyObservation = [ordered]@{
    evidence_id = 'local-runtime-20261018-01'
    observed_at = [DateTime]::UtcNow.ToString('o')
    method = 'PowerShell Get-Process / Process.Modules; no app launch or input'
    target_path = $sunnyPath
    target_sha256 = (Get-FileHash -LiteralPath $sunnyPath -Algorithm SHA256).Hash.ToLowerInvariant()
    process_id = $sunnyProcess.Id
    process_start = $sunnyProcess.StartTime.ToUniversalTime().ToString('o')
    working_set_bytes = $sunnyProcess.WorkingSet64
    private_bytes = $sunnyProcess.PrivateMemorySize64
    main_window_handle = $sunnyProcess.MainWindowHandle.ToInt64()
    target_directory_modules = @($sunnyProcess.Modules |
        Where-Object { $_.FileName.StartsWith($sunnyRoot + '\', [StringComparison]::OrdinalIgnoreCase) } |
        Sort-Object ModuleName | ForEach-Object { [ordered]@{ name = $_.ModuleName; mapped_image_bytes = $_.ModuleMemorySize } })
    limitations = @(
        'One sample of an existing session; neither a cold-start benchmark nor proof of idle state.',
        'Loaded modules do not prove individual functions executed; absent modules may load later.',
        'Local Windows process observation, not a REA Evidence record. No user logs, tokens or memory dumps read.'
    )
}
$sunnyJson = $sunnyObservation | ConvertTo-Json -Depth 6
$sunnyParent = Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath))
[IO.Directory]::CreateDirectory($sunnyParent) | Out-Null
[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath), $sunnyJson, [Text.UTF8Encoding]::new($false))
[pscustomobject]@{
    Path = [IO.Path]::GetFullPath($OutputPath)
    WorkingSetMiB = [Math]::Round($sunnyProcess.WorkingSet64 / 1MB, 2)
    PrivateMiB = [Math]::Round($sunnyProcess.PrivateMemorySize64 / 1MB, 2)
    Modules = $sunnyObservation.target_directory_modules.Count
}
