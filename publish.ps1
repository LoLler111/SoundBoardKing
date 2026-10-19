# Builds a release of both apps, with the version in version.txt:
#   artifacts\publish\SoundBoardKing\          app, mic installer and SbkMicFx.dll (self-contained)
#   artifacts\releases\                         what goes on the GitHub release:
#       SoundBoardKing-win-Setup.exe            installer (updates itself from the GitHub releases)
#       SoundBoardKing-win-Portable.zip         same app to unpack anywhere, updates itself too
#       SoundBoardKing-<version>-full.nupkg, releases.win.json, ...   read by the Windows updater
#       SoundBoardKing-android.apk              Android remote, signed with the release key
# -DownloadPrevious fetches the latest release first so the Windows update also gets a small delta package.
param([switch]$DownloadPrevious)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$version = (Get-Content (Join-Path $root 'version.txt') -Raw).Trim()
$out = Join-Path $root 'artifacts\publish\SoundBoardKing'
$releases = Join-Path $root 'artifacts\releases'
$repoUrl = 'https://github.com/LoLler111/SoundBoardKing'
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

if (Test-Path $releases) { Remove-Item $releases -Recurse -Force }
New-Item -ItemType Directory $releases | Out-Null

# Android remote: needs Android Studio (its bundled JDK) and the Android SDK, unless JAVA_HOME is set.
$studioJdk = "$env:ProgramFiles\Android\Android Studio\jbr"
if (-not $env:JAVA_HOME -and (Test-Path $studioJdk)) { $env:JAVA_HOME = $studioJdk }
if ($env:JAVA_HOME) {
    $signing = Join-Path $env:USERPROFILE '.soundboardking\signing.properties'
    if (-not $env:SBK_KEYSTORE -and -not (Test-Path $signing)) {
        Write-Warning "Chiave di rilascio Android non trovata ($signing): l'APK viene firmato con la chiave di debug e non potrà aggiornare quello pubblicato."
    }
    & (Join-Path $root 'android\gradlew.bat') -p (Join-Path $root 'android') assembleRelease --console=plain -q
    if ($LASTEXITCODE -ne 0) { throw 'Android build failed.' }
    $apk = Join-Path $root 'artifacts\publish\SoundBoardKing-android.apk'
    Copy-Item (Join-Path $root 'android\app\build\outputs\apk\release\app-release.apk') $apk -Force
    Copy-Item $apk $releases
    Write-Host "APK: $apk"
} else {
    Write-Warning 'Android Studio non trovato: APK non compilato.'
}

# Windows installer and update packages (Velopack: dotnet tool install -g vpk).
if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) { throw 'vpk non trovato: dotnet tool install -g vpk' }
if ($DownloadPrevious) {
    $token = if ($env:GITHUB_TOKEN) { @('--token', $env:GITHUB_TOKEN) } else { @() }
    vpk download github --repoUrl $repoUrl --outputDir $releases @token
    if ($LASTEXITCODE -ne 0) { Write-Warning 'Nessuna release precedente scaricata: niente pacchetto delta.' }
}
vpk pack --packId SoundBoardKing --packVersion $version --packDir $out --mainExe SoundBoardKing.exe --runtime win-x64 `
    --packTitle SoundBoardKing --packAuthors LoLler111 `
    --icon (Join-Path $root 'src\SoundBoardKing.App\Assets\SoundBoardKing.ico') --outputDir $releases
if ($LASTEXITCODE -ne 0) { throw 'vpk pack failed.' }
Write-Host "Release $version`: $releases"
