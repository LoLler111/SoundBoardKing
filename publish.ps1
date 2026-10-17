# Builds a self-contained package (no .NET install needed on the target PC):
#   artifacts\publish\SoundBoardKing\        app, mic installer and SbkMicFx.dll
#   artifacts\publish\SoundBoardKing-win-x64.zip
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'artifacts\publish\SoundBoardKing'
$dotnet = "$env:ProgramFiles\dotnet\dotnet.exe"
$onPath = Get-Command dotnet -ErrorAction SilentlyContinue
if ($onPath) { $dotnet = $onPath.Source }

& "$root\build-native.ps1" -Configuration Release

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
foreach ($project in 'src\SoundBoardKing.App', 'src\SoundBoardKing.MicSetup') {
    & $dotnet publish (Join-Path $root $project) -c Release -r win-x64 --self-contained -o $out -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $project" }
}
Copy-Item (Join-Path $root 'artifacts\native\x64\Release\SbkMicFx.dll') $out -Force

# Android remote: needs Android Studio (its bundled JDK) and the Android SDK.
$studioJdk = "$env:ProgramFiles\Android\Android Studio\jbr"
if (Test-Path $studioJdk) {
    $env:JAVA_HOME = $studioJdk
    & (Join-Path $root 'android\gradlew.bat') -p (Join-Path $root 'android') assembleRelease --console=plain -q
    if ($LASTEXITCODE -ne 0) { throw 'Android build failed.' }
    Copy-Item (Join-Path $root 'android\app\build\outputs\apk\release\app-release.apk') `
        (Join-Path $root 'artifacts\publish\SoundBoardKing-android.apk') -Force
    Write-Host "APK: artifacts\publish\SoundBoardKing-android.apk"
} else {
    Write-Warning 'Android Studio non trovato: APK non compilato.'
}

$zip = Join-Path $root 'artifacts\publish\SoundBoardKing-win-x64.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$out\*" -DestinationPath $zip
Write-Host "Pacchetto: $zip"
