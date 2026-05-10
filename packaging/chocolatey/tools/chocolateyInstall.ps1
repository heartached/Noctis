$ErrorActionPreference = 'Stop'

$packageName  = 'noctis'
$version      = $env:ChocolateyPackageVersion
$url64        = "https://github.com/heartached/Noctis/releases/download/v$version/Noctis-v$version-Setup.exe"

$packageArgs = @{
  packageName    = $packageName
  fileType       = 'exe'
  url64bit       = $url64
  checksum64     = '865b0cb503dd7001788770d5da74922ca87ea36a173a1cdfb581a2967d841376'
  checksumType64 = 'sha256'
  silentArgs     = '/SILENT /SUPPRESSMSGBOXES /NORESTART /SP-'
  validExitCodes = @(0)
}

Install-ChocolateyPackage @packageArgs
