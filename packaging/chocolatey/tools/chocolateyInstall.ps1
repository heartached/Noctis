$ErrorActionPreference = 'Stop'

$packageName  = 'noctis'
$version      = $env:ChocolateyPackageVersion
$url64        = "https://github.com/heartached/Noctis/releases/download/v$version/Noctis-v$version-Setup.exe"

# The URL is version-templated but the checksum is not: packing a new version without
# updating BOTH produces a package that downloads the new installer and then always fails
# the hash check. It fails closed, which is right, but it was eight releases stale and
# silent about it. Assert the pairing here so the mismatch is named, not guessed at.
$checksumForVersion = '1.1.14'
$checksum64         = 'f84c467f71aaec7d12787171b5e86167d1bad8047b8a3dcd52546d109d6f0743'

if ($version -ne $checksumForVersion) {
  throw ("This Chocolatey package's checksum is pinned to Noctis v$checksumForVersion but " +
         "the package version is v$version. Update `checksum64` and `checksumForVersion` in " +
         "tools/chocolateyInstall.ps1 (sha256 of Noctis-v$version-Setup.exe, published in " +
         "the release's SHA256SUMS) before packing.")
}

$packageArgs = @{
  packageName    = $packageName
  fileType       = 'exe'
  url64bit       = $url64
  checksum64     = $checksum64
  checksumType64 = 'sha256'
  silentArgs     = '/SILENT /SUPPRESSMSGBOXES /NORESTART /SP-'
  validExitCodes = @(0)
}

Install-ChocolateyPackage @packageArgs
