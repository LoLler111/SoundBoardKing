# Copies the Android release key from %USERPROFILE%\.soundboardking into the GitHub secrets the
# Release workflow signs the APK with. Needs gh logged in with access to the repository.
param([string]$Repo = 'LoLler111/SoundBoardKing')

$ErrorActionPreference = 'Stop'
$folder = Join-Path $env:USERPROFILE '.soundboardking'
$properties = @{}
foreach ($line in Get-Content (Join-Path $folder 'signing.properties')) {
    if ($line -match '^\s*([^#=]+?)\s*=\s*(.*)$') { $properties[$Matches[1]] = $Matches[2] }
}

$keystore = [Convert]::ToBase64String([IO.File]::ReadAllBytes($properties['storeFile']))
# --body, not a pipe: Windows PowerShell adds a line break to what it pipes to a program.
gh secret set ANDROID_KEYSTORE_BASE64 --repo $Repo --body $keystore
gh secret set ANDROID_KEYSTORE_PASSWORD --repo $Repo --body $properties['storePassword']
gh secret set ANDROID_KEY_ALIAS --repo $Repo --body $properties['keyAlias']
gh secret set ANDROID_KEY_PASSWORD --repo $Repo --body $properties['keyPassword']
gh secret list --repo $Repo
