# Builds the SbkMicFx APO with the Visual Studio C++ tools.
#   artifacts\native\x64\<Configuration>\SbkMicFx.dll
#   artifacts\native-tests\x64\MicFxHarness.exe   (with -Tests)
param(
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    [switch]$Tests
)

$ErrorActionPreference = 'Stop'
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -find 'MSBuild\**\Bin\amd64\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'Visual Studio C++ build tools not found.' }

function Build([string]$project, [string]$configuration) {
    & $msbuild $project -p:Configuration=$configuration -p:Platform=x64 -nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $project ($LASTEXITCODE)." }
}

Build "$PSScriptRoot\native\SbkMicFx\SbkMicFx.vcxproj" $Configuration
if ($Tests) {
    Build "$PSScriptRoot\native\SbkMicFx.Tests\MicFxHarness.vcxproj" 'Release'
}
