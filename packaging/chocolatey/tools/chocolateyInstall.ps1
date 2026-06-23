$ErrorActionPreference = 'Stop'

$packageName  = 'noctis'
$version      = $env:ChocolateyPackageVersion
$url64        = "https://github.com/heartached/Noctis/releases/download/v$version/Noctis-v$version-Setup.exe"

$packageArgs = @{
  packageName    = $packageName
  fileType       = 'exe'
  url64bit       = $url64
  checksum64     = 'f84c467f71aaec7d12787171b5e86167d1bad8047b8a3dcd52546d109d6f0743'
  checksumType64 = 'sha256'
  silentArgs     = '/SILENT /SUPPRESSMSGBOXES /NORESTART /SP-'
  validExitCodes = @(0)
}

Install-ChocolateyPackage @packageArgs
