<#
.SYNOPSIS
    Builds the signed Noctis for Android App Bundle (AAB) that is uploaded to Google Play.

.DESCRIPTION
    Runs a Release build of src/Noctis.Android (AAB, full AOT, partial trimming, 16 KB
    aligned native libraries), signed with the upload key, then proves the result:
    jarsigner verification, the signer's SHA-256 matches the keystore, versionCode /
    versionName / targetSdk read back from the bundle, and the 16 KB check. The finished
    bundle is copied to artifacts/android/ (gitignored) with the version in its name.

    The keystore password is read with a hidden prompt into an environment variable of
    this process only (NOCTIS_UPLOAD_STORE_PASS) and handed to jarsigner/keytool as
    "env:NOCTIS_UPLOAD_STORE_PASS", so it never appears on a command line, in a build log
    or on disk. The variable is removed when the script ends, success or failure.

    -UseDebugKey signs with the .NET Android debug keystore instead. That exercises the
    exact same signing path without the real key (overnight verification) and additionally
    produces device APKs from the bundle for an emulator. Google Play REJECTS a bundle
    signed with the debug certificate, and the output is named so nobody uploads it.

.PARAMETER VersionCode
    Play versionCode. Must be higher than every versionCode already uploaded. 0 = the
    csproj value (ApplicationVersion).

.PARAMETER VersionName
    User-visible version string. Empty = the csproj value (ApplicationDisplayVersion).

.PARAMETER Keystore
    Upload keystore. Default: %USERPROFILE%\.noctis\noctis-upload.jks (what
    create-upload-key.ps1 writes).

.PARAMETER KeyAlias
    Key alias inside the keystore. Default: noctis-upload.

.PARAMETER UseDebugKey
    Sign with %LOCALAPPDATA%\Xamarin\Mono for Android\debug.keystore (alias
    androiddebugkey, password "android" - public, not a secret). Pipeline test only.

.PARAMETER DeviceSerial
    With -UseDebugKey: the adb serial the device APKs are built for. Default emulator-5554.

.PARAMETER MsBuildArgs
    Extra arguments passed to dotnet build unchanged. Arrays only bind when the script is
    called in-process (& .\build-release.ps1 -MsBuildArgs @(...)), not through pwsh -File.

.EXAMPLE
    pwsh -File scripts/android/build-release.ps1
.EXAMPLE
    pwsh -File scripts/android/build-release.ps1 -VersionCode 2 -VersionName 1.0.1
.EXAMPLE
    pwsh -File scripts/android/build-release.ps1 -UseDebugKey
#>
param(
    [int]$VersionCode = 0,
    [string]$VersionName = '',
    [string]$Keystore = (Join-Path $env:USERPROFILE '.noctis\noctis-upload.jks'),
    [string]$KeyAlias = 'noctis-upload',
    [switch]$UseDebugKey,
    [string]$DeviceSerial = 'emulator-5554',
    [string[]]$MsBuildArgs = @()
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$jbr = 'C:\Program Files\Android\Android Studio\jbr'
$java = Join-Path $jbr 'bin\java.exe'
$keytool = Join-Path $jbr 'bin\keytool.exe'
$jarsigner = Join-Path $jbr 'bin\jarsigner.exe'
$sdk = Join-Path $env:LOCALAPPDATA 'Android\Sdk'
$aapt2 = Join-Path $sdk 'build-tools\36.0.0\aapt2.exe'
$adb = Join-Path $sdk 'platform-tools\adb.exe'
$bundletool = Get-ChildItem 'C:\Program Files\dotnet\packs\Microsoft.Android.Sdk.Windows\*\tools\bundletool.jar' |
    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
$project = Join-Path $repo 'src\Noctis.Android\Noctis.Android.csproj'
$outDir = Join-Path $repo 'src\Noctis.Android\bin\Release\net10.0-android'
$signedAab = Join-Path $outDir 'com.heartached.noctis-Signed.aab'
$artifacts = Join-Path $repo 'artifacts\android'
$passVar = 'NOCTIS_UPLOAD_STORE_PASS'

foreach ($tool in @($java, $keytool, $jarsigner, $aapt2, $bundletool)) {
    if (-not $tool -or -not (Test-Path $tool)) { throw "Required tool not found: $tool" }
}

function Get-Sha256([string[]]$lines) {
    $m = [regex]::Match(($lines -join "`n"), '(?:[0-9A-F]{2}:){31}[0-9A-F]{2}')
    if (-not $m.Success) { throw "No SHA-256 fingerprint in:`n$($lines -join "`n")" }
    return $m.Value
}

if ($UseDebugKey) {
    $Keystore = Join-Path $env:LOCALAPPDATA 'Xamarin\Mono for Android\debug.keystore'
    $KeyAlias = 'androiddebugkey'
    if (-not (Test-Path $Keystore)) { throw "Debug keystore not found at $Keystore (build the Debug APK once to create it)." }
    Set-Item "Env:$passVar" 'android'
} else {
    if (-not (Test-Path $Keystore)) {
        throw "Upload keystore not found at $Keystore. Create it first: pwsh -File scripts/android/create-upload-key.ps1"
    }
    $secure = Read-Host -AsSecureString "Password for $Keystore"
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { Set-Item "Env:$passVar" ([Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

try {
    # Wrong password or alias fails here, in one second, instead of after a two-minute build.
    $ksLines = & $keytool -list -keystore $Keystore -alias $KeyAlias -storepass:env $passVar 2>&1
    if ($LASTEXITCODE -ne 0) { throw "keytool could not open alias '$KeyAlias' in $Keystore (wrong password?):`n$($ksLines -join "`n")" }
    $keySha = Get-Sha256 $ksLines

    # _Sign is incremental on the unsigned bundle only, not on the key: without this delete a
    # rebuild after a -UseDebugKey run leaves the debug-signed bundle in place (observed:
    # "Skipping target _Sign because all output files are up-to-date").
    Remove-Item $signedAab -ErrorAction SilentlyContinue

    $buildArgs = @(
        'build', $project, '-c', 'Release', '-nodeReuse:false', '-v:m',
        "-p:JavaSdkDirectory=$jbr", "-p:AndroidSdkDirectory=$sdk",
        '-p:AndroidKeyStore=true',
        "-p:AndroidSigningKeyStore=$Keystore",
        "-p:AndroidSigningKeyAlias=$KeyAlias",
        "-p:AndroidSigningStorePass=env:$passVar",
        "-p:AndroidSigningKeyPass=env:$passVar"
    )
    if ($VersionCode -gt 0) { $buildArgs += "-p:ApplicationVersion=$VersionCode" }
    if ($VersionName) { $buildArgs += "-p:ApplicationDisplayVersion=$VersionName" }
    $buildArgs += $MsBuildArgs
    Write-Host "dotnet $($buildArgs -join ' ')"
    & dotnet @buildArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)" }
    if (-not (Test-Path $signedAab)) { throw "Build succeeded but $signedAab is missing (is AndroidPackageFormat aab in Release?)" }

    # 1. Signature verifies and was made by the key we meant.
    $verify = & $jarsigner -verify $signedAab 2>&1
    if (-not ($verify -match '^jar verified\.')) { throw "jarsigner -verify failed:`n$($verify -join "`n")" }
    $aabSha = Get-Sha256 (& $keytool -printcert -jarfile $signedAab 2>&1)
    if ($aabSha -ne $keySha) { throw "Bundle is signed by $aabSha but $Keystore/$KeyAlias is $keySha" }
    $owner = (& $keytool -printcert -jarfile $signedAab 2>&1 | Select-String '^Owner:' | Select-Object -First 1).Line
    if (-not $UseDebugKey -and $owner -match 'CN=Android Debug') { throw "Bundle is signed with a debug certificate ($owner); Play rejects it." }

    # 2. What Play will read from the manifest and the bundle config.
    $vc = (& $java -jar $bundletool dump manifest "--bundle=$signedAab" '--xpath=/manifest/@android:versionCode').Trim()
    $vn = (& $java -jar $bundletool dump manifest "--bundle=$signedAab" '--xpath=/manifest/@android:versionName').Trim()
    $tsdk = (& $java -jar $bundletool dump manifest "--bundle=$signedAab" '--xpath=/manifest/uses-sdk/@android:targetSdkVersion').Trim()
    $config = (& $java -jar $bundletool dump config "--bundle=$signedAab") -join "`n"
    if ($config -notmatch 'PAGE_ALIGNMENT_16K') { throw 'Bundle config does not request PAGE_ALIGNMENT_16K (src/Noctis.Android/BundleConfig.json not applied?)' }

    # 3. 16 KB page size, ELF side (the bundle) - see scripts/android/check-16kb.py.
    & python (Join-Path $PSScriptRoot 'check-16kb.py') $signedAab
    if ($LASTEXITCODE -ne 0) { throw '16 KB check failed' }

    New-Item -ItemType Directory -Force $artifacts | Out-Null
    $suffix = if ($UseDebugKey) { '-DEBUGKEY-NOT-FOR-UPLOAD' } else { '' }
    $final = Join-Path $artifacts "noctis-$vn-$vc$suffix.aab"
    Copy-Item $signedAab $final -Force

    if ($UseDebugKey) {
        # Device APKs straight from the bundle, the way Play will split it, for the emulator.
        $apks = Join-Path $artifacts "noctis-$vn-$vc$suffix.apks"
        Remove-Item $apks -ErrorAction SilentlyContinue
        & $java -jar $bundletool build-apks "--bundle=$signedAab" "--output=$apks" --connected-device "--device-id=$DeviceSerial" `
            "--adb=$adb" "--aapt2=$aapt2" "--ks=$Keystore" "--ks-key-alias=$KeyAlias" '--ks-pass=pass:android' '--key-pass=pass:android'
        if ($LASTEXITCODE -ne 0) { throw "bundletool build-apks failed (is $DeviceSerial running? adb devices)" }
        & python (Join-Path $PSScriptRoot 'check-16kb.py') $apks
        if ($LASTEXITCODE -ne 0) { throw '16 KB check failed on the device APKs' }
        Write-Host "Device APKs: $apks"
        Write-Host "Install:     & '$java' -jar '$bundletool' install-apks --apks='$apks' --device-id=$DeviceSerial --adb='$adb'"
    }

    $sizeMb = [math]::Round((Get-Item $final).Length / 1MB, 1)
    Write-Host ''
    Write-Host "AAB:          $final ($sizeMb MB)"
    Write-Host "versionCode:  $vc    versionName: $vn    targetSdk: $tsdk"
    Write-Host "Signer:       $owner"
    Write-Host "SHA-256:      $aabSha"
    if ($UseDebugKey) { Write-Warning 'Signed with the DEBUG key. Google Play rejects this bundle. Pipeline test only.' }
}
finally {
    Remove-Item "Env:$passVar" -ErrorAction SilentlyContinue
}
