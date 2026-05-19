$ErrorActionPreference = 'Stop'

$packageName  = 'noctis'
$version      = $env:ChocolateyPackageVersion
$url64        = "https://github.com/heartached/Noctis/releases/download/v$version/Noctis-v$version-Setup.exe"

$packageArgs = @{
  packageName    = $packageName
  fileType       = 'exe'
  url64bit       = $url64
  checksum64     = '9ca7b23e8344690de770794c3e5340539d1198554bfff7169f127d861dce91a4'
  checksumType64 = 'sha256'
  silentArgs     = '/SILENT /SUPPRESSMSGBOXES /NORESTART /SP-'
  validExitCodes = @(0)
}

Install-ChocolateyPackage @packageArgs
