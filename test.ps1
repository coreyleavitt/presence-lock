#Requires -Version 7
# Fast inner-loop test runner: executes the F# core test suite in a Linux SDK
# container via podman (seconds, vs. minutes for the Hyper-V Windows container).
# The Windows container remains the release gate — pack.ps1 runs both suites there.
#
# Invokes podman directly inside the machine distro; the podman-remote API socket
# is unreliable on this setup (rootless user session does not start under WSL).
$ErrorActionPreference = 'Stop'

$repoLinux = '/mnt/' + $PSScriptRoot.Replace('\', '/').Replace(':', '').ToLower()[0] + $PSScriptRoot.Replace('\', '/').Substring(2)
$image = 'mcr.microsoft.com/dotnet/sdk:10.0'

wsl -d podman-machine-default -u user -- podman run --rm `
    -v "${repoLinux}:/src" -v presencelock-nuget-linux:/nuget `
    -e NUGET_PACKAGES=/nuget -w /src $image `
    dotnet test PresenceLock.Core.Tests/PresenceLock.Core.Tests.fsproj -c Release
if ($LASTEXITCODE) { throw 'core tests failed' }
