#Requires -Version 7
# Publishes in the SDK container, then packs and signs the MSIX on the host.
# (Server Core container images lack the AppX packaging infrastructure that
# makeappx requires, so packing cannot run in the container.)
$ErrorActionPreference = 'Stop'

$image = 'mcr.microsoft.com/dotnet/sdk:10.0-windowsservercore-ltsc2022'

docker run --rm --isolation=hyperv `
    -v "${PSScriptRoot}:C:\src" `
    -v "presencelock-nuget:C:\nuget" `
    -e NUGET_PACKAGES=C:\nuget `
    -w C:\src `
    $image `
    dotnet test PresenceLock.Core.Tests\PresenceLock.Core.Tests.fsproj -c Release
if ($LASTEXITCODE) { throw 'dotnet test failed' }

docker run --rm --isolation=hyperv `
    -v "${PSScriptRoot}:C:\src" `
    -v "presencelock-nuget:C:\nuget" `
    -e NUGET_PACKAGES=C:\nuget `
    -w C:\src `
    $image `
    dotnet test PresenceLock.Tests\PresenceLock.Tests.csproj -c Release
if ($LASTEXITCODE) { throw 'dotnet test (shell) failed' }

docker run --rm `
    -v "${PSScriptRoot}:C:\src" `
    -v "presencelock-nuget:C:\nuget" `
    -e NUGET_PACKAGES=C:\nuget `
    -w C:\src `
    $image `
    dotnet publish PresenceLock.csproj -c Release -o C:\src\publish
if ($LASTEXITCODE) { throw 'dotnet publish failed' }

$makeappx = (Get-ChildItem "$PSScriptRoot\tools\bin\*\x64\makeappx.exe" | Select-Object -Last 1).FullName
$signtool = (Get-ChildItem "$PSScriptRoot\tools\bin\*\x64\signtool.exe" | Select-Object -Last 1).FullName

$layout = Join-Path $PSScriptRoot 'msix-layout'
if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
New-Item -ItemType Directory $layout | Out-Null
Copy-Item "$PSScriptRoot\publish\*" $layout -Recurse
Copy-Item "$PSScriptRoot\AppxManifest.xml" $layout
Copy-Item "$PSScriptRoot\Assets" "$layout\Assets" -Recurse

New-Item -ItemType Directory -Force "$PSScriptRoot\out" | Out-Null
& $makeappx pack /o /d $layout /p "$PSScriptRoot\out\PresenceLock.msix"
if ($LASTEXITCODE) { throw 'makeappx failed' }

$pw = (Get-Content "$PSScriptRoot\cert\pfx-password.txt" -Raw).Trim()
& $signtool sign /fd SHA256 /f "$PSScriptRoot\cert\PresenceLock.pfx" /p $pw "$PSScriptRoot\out\PresenceLock.msix"
if ($LASTEXITCODE) { throw 'signtool failed' }

Write-Host "Packed and signed: $PSScriptRoot\out\PresenceLock.msix"
