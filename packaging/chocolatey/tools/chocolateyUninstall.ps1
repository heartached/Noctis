$ErrorActionPreference = 'Stop'

$packageName = 'noctis'
# Inno Setup writes its uninstall entry under the AppId + "_is1".
$key = Get-UninstallRegistryKey -SoftwareName 'Noctis*'

if ($key.Count -eq 1) {
  $uninstallString = $key.UninstallString
  # UninstallString looks like: "C:\...\unins000.exe" /...
  $exe = $uninstallString -replace '^"([^"]+)".*$', '$1'
  Uninstall-ChocolateyPackage -PackageName $packageName `
    -FileType 'exe' `
    -SilentArgs '/SILENT /SUPPRESSMSGBOXES /NORESTART' `
    -File $exe `
    -ValidExitCodes @(0)
} elseif ($key.Count -eq 0) {
  Write-Warning "$packageName is not installed (no matching uninstall registry key)."
} else {
  Write-Warning "Multiple matches for '$packageName' found; not uninstalling automatically. Remove via Settings > Apps."
  $key | ForEach-Object { Write-Warning "  $($_.DisplayName) - $($_.UninstallString)" }
}
