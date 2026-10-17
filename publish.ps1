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

$zip = Join-Path $root 'artifacts\publish\SoundBoardKing-win-x64.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$out\*" -DestinationPath $zip
Write-Host "Pacchetto: $zip"
