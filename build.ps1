#Requires -Version 7
$ErrorActionPreference = 'Stop'

$image = 'mcr.microsoft.com/dotnet/sdk:10.0-windowsservercore-ltsc2022'

docker run --rm `
    -v "${PSScriptRoot}:C:\src" `
    -v "presencelock-nuget:C:\nuget" `
    -e NUGET_PACKAGES=C:\nuget `
    -w C:\src `
    $image `
    dotnet publish PresenceLock.csproj -c Release -o C:\src\publish
