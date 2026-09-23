<#
.SYNOPSIS
    Creates the Noctis for Android UPLOAD key. The owner runs this once, by hand, in an
    interactive terminal. Nothing else in the repo creates or reads this key's password.

.DESCRIPTION
    With Play App Signing, Google holds the key that signs what users install (the app
    signing key). This script makes the other one: the upload key, which signs the AAB you
    upload so Google knows it came from you. If it is lost or leaked, the Play Console can
    reset it (Setup > App signing > Request upload key reset), but that takes days and a
    support request, so back it up.

    keytool itself asks for the password (twice, nothing echoed). The script never sees,
    stores or prints it. The keystore is PKCS12, so the key password is the keystore
    password: one password to remember.

.PARAMETER Keystore
    Where to write the keystore. Default %USERPROFILE%\.noctis\noctis-upload.jks. Refuses
    any path inside the repository and refuses to overwrite an existing file.

.PARAMETER Alias
    Key alias. Default noctis-upload (build-release.ps1 expects it).

.EXAMPLE
    pwsh -File scripts/android/create-upload-key.ps1
#>
param(
    [string]$Keystore = (Join-Path $env:USERPROFILE '.noctis\noctis-upload.jks'),
    [string]$Alias = 'noctis-upload'
)

$ErrorActionPreference = 'Stop'
$keytool = 'C:\Program Files\Android\Android Studio\jbr\bin\keytool.exe'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path.TrimEnd('\') + '\'
$full = [IO.Path]::GetFullPath($Keystore)

if (-not (Test-Path $keytool)) { throw "keytool not found at $keytool (it ships with Android Studio)." }
if ($full.StartsWith($repo, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to create the upload key inside the repository ($full). Keep it outside, e.g. the default %USERPROFILE%\.noctis\noctis-upload.jks."
}
if (Test-Path $full) {
    throw "A keystore already exists at $full. Refusing to overwrite it: replacing an upload key Play already knows means an upload-key reset. Move the file away yourself if you really mean to start over."
}
if ([Console]::IsInputRedirected -or [Console]::IsOutputRedirected) {
    throw 'Run this in an interactive terminal (no pipes or redirection): keytool must read the password from the console so it is not echoed.'
}

New-Item -ItemType Directory -Force (Split-Path $full) | Out-Null

Write-Host ''
Write-Host 'keytool will ask for a NEW keystore password twice. Nothing is shown while you type.'
Write-Host 'Use a long random password and save it in your password manager NOW, next to a note'
Write-Host "saying 'Noctis Android upload key, alias $Alias'."
Write-Host ''

# RSA 4096, 10950 days (30 years; Android's app-signing guide asks for at least 25). The distinguished
# name is only seen by Google; it does not appear on the store listing.
& $keytool -genkeypair -keystore $full -storetype PKCS12 -alias $Alias `
    -keyalg RSA -keysize 4096 -sigalg SHA256withRSA -validity 10950 `
    -dname 'CN=Noctis Upload Key, O=heartached'
if ($LASTEXITCODE -ne 0) { throw "keytool -genkeypair failed (exit $LASTEXITCODE); nothing usable was created." }

Write-Host ''
Write-Host 'Created. keytool now asks for the same password once more to print the certificate:'
& $keytool -list -v -keystore $full -alias $Alias
# A warning, not a throw: the keystore already exists, and a mistyped password here must not
# skip the backup banner below. Every build-release.ps1 run prints the fingerprint too.
if ($LASTEXITCODE -ne 0) {
    Write-Warning "keytool -list failed (exit $LASTEXITCODE), probably a mistyped password. The keystore was created at $full; build-release.ps1 prints its SHA-256 fingerprint on every run."
}

Write-Host ''
Write-Host '============================================================================' -ForegroundColor Yellow
Write-Host " UPLOAD KEY CREATED: $full" -ForegroundColor Yellow
Write-Host ' BACK IT UP NOW: copy this file to at least two places you control that are' -ForegroundColor Yellow
Write-Host ' NOT this PC and NOT the git repository (password manager attachment, an' -ForegroundColor Yellow
Write-Host ' encrypted USB stick). Keep the password with it. Never commit it, never' -ForegroundColor Yellow
Write-Host ' paste it into chat, CI logs or issues.' -ForegroundColor Yellow
Write-Host ' The SHA-256 fingerprint above is public information: Play Console shows the' -ForegroundColor Yellow
Write-Host ' same value under Setup > App signing > Upload key certificate once the first' -ForegroundColor Yellow
Write-Host ' bundle is uploaded. They must match.' -ForegroundColor Yellow
Write-Host '============================================================================' -ForegroundColor Yellow
