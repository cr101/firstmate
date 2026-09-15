# Build an isolated test assembly and home; never install the native provider.
# Usage: powershell -NoProfile -File tests/fixtures/native-owner/Build.ps1
# Requires native Git symlinks and the pre-existing Docker validation image.
$ErrorActionPreference = 'Stop'
$env:MSYS = 'winsymlinks:nativestrict'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$root = Join-Path ([IO.Path]::GetTempPath()) ('fm-native-candidate-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $root | Out-Null
$binary = Join-Path $root 'SessionProbe.exe'
$sources = @((Join-Path $repo 'bin/native-owner/NativeOwner.cs'), (Join-Path $repo 'bin/native-owner/NativeHomeLease.cs'), (Join-Path $PSScriptRoot 'NativeDriver.cs'))
Add-Type -Path $sources -OutputAssembly $binary -OutputType ConsoleApplication -ReferencedAssemblies System.dll,System.Core.dll,System.Web.Extensions.dll
$copy = Join-Path $root 'firstmate'
& git -c core.symlinks=true clone --quiet --no-local --single-branch $repo $copy
if ($LASTEXITCODE -ne 0) { throw 'Disposable clone failed' }
& git -C $copy apply --whitespace=error (Join-Path $PSScriptRoot 'consumer-adapter.patch')
if ($LASTEXITCODE -ne 0) { throw 'Consumer test integration no longer applies; reconcile it explicitly' }
foreach ($name in @('exercise.sh','notification-check.sh','notification-ack.sh')) { Copy-Item (Join-Path $PSScriptRoot $name) (Join-Path $copy $name) }
Copy-Item (Join-Path $PSScriptRoot 'AppHost.mjs') (Join-Path $root 'AppHost.mjs')
Copy-Item (Join-Path $repo 'bin/native-owner/codex-tool-gate.mjs') (Join-Path $root 'codex-tool-gate.mjs')
Copy-Item $binary (Join-Path $copy 'bin/fm-native-owner.exe')
New-Item -ItemType Directory (Join-Path $root 'tools') | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'jq') (Join-Path $root 'tools/jq')
$state = Join-Path $repo 'data/native-candidate-validation'
New-Item -ItemType Directory -Force $state | Out-Null
@{ root=$root; binary=$binary; repo=$repo; hashes=@($sources | ForEach-Object { @{ file=$_; hash=(Get-FileHash $_).Hash } }) } | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 (Join-Path $state 'build.json')
Write-Output $binary
