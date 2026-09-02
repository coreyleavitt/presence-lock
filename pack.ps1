#Requires -Version 7
# Builds, tests, packs, and signs PresenceLock.
#
# Topology (each tool in the least-privileged place that can run it):
#   - Core tests + publish : Linux SDK container via podman (fast; cross-compiles
#                            the WinExe with EnableWindowsTargeting)
#   - Shell tests          : Windows SDK container via docker (net10.0-windows
#                            tests cannot execute on Linux)
#   - MSIX pack            : makemsix in a Linux container (tools/linux, built by
#                            build-makemsix.ps1 from a pinned commit)
#   - Signing              : host signtool only — the private key never enters
#                            any container
#
# podman note: invoked directly inside the machine distro (wsl -d ... -u user)
# because the podman-remote API socket is unreliable on this machine.
$ErrorActionPreference = 'Stop'

$winImage = 'mcr.microsoft.com/dotnet/sdk:10.0-windowsservercore-ltsc2022'
$linuxImage = 'mcr.microsoft.com/dotnet/sdk:10.0'
$packImage = 'localhost/presencelock-pack'
$repoLinux = '/mnt/' + $PSScriptRoot.Replace('\', '/').Replace(':', '').ToLower()[0] + $PSScriptRoot.Replace('\', '/').Substring(2)

function Invoke-LinuxPodman {
    param([string[]]$PodmanArgs)
    wsl -d podman-machine-default -u user -- podman @PodmanArgs
    if ($LASTEXITCODE) { throw "podman failed: $($PodmanArgs[0]) ..." }
}

if (-not (Test-Path "$PSScriptRoot\tools\linux\makemsix")) {
    throw 'tools\linux\makemsix missing — run build-makemsix.ps1 first'
}

# 1. Core tests (Linux)
Invoke-LinuxPodman @('run','--rm',
    '-v',"${repoLinux}:/src",'-v','presencelock-nuget-linux:/nuget',
    '-e','NUGET_PACKAGES=/nuget','-w','/src',$linuxImage,
    'dotnet','test','PresenceLock.Core.Tests/PresenceLock.Core.Tests.fsproj','-c','Release')

# 2. Shell tests (Windows container — the one remaining Windows-container step)
docker run --rm --isolation=hyperv `
    -v "${PSScriptRoot}:C:\src" `
    -v "presencelock-nuget:C:\nuget" `
    -e NUGET_PACKAGES=C:\nuget `
    -w C:\src `
    $winImage `
    dotnet test PresenceLock.Tests\PresenceLock.Tests.csproj -c Release
if ($LASTEXITCODE) { throw 'dotnet test (shell) failed' }

# 3. Publish the WinExe (Linux cross-compile; output verified byte-faithful to
#    the Windows-container build)
Invoke-LinuxPodman @('run','--rm',
    '-v',"${repoLinux}:/src",'-v','presencelock-nuget-linux:/nuget',
    '-e','NUGET_PACKAGES=/nuget','-w','/src',$linuxImage,
    'dotnet','publish','PresenceLock.csproj','-c','Release','-o','/src/publish')

# 4. Assemble the MSIX layout
$layout = Join-Path $PSScriptRoot 'msix-layout'
if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
New-Item -ItemType Directory $layout | Out-Null
Copy-Item "$PSScriptRoot\publish\*" $layout -Recurse
Copy-Item "$PSScriptRoot\AppxManifest.xml" $layout
Copy-Item "$PSScriptRoot\Assets" "$layout\Assets" -Recurse

# 5. Pack with makemsix (build the small runtime image on first use)
wsl -d podman-machine-default -u user -- podman image exists $packImage
if ($LASTEXITCODE) {
    Invoke-LinuxPodman @('build','-t',$packImage,'-f',"${repoLinux}/containers/pack.Containerfile")
}
New-Item -ItemType Directory -Force "$PSScriptRoot\out" | Out-Null
if (Test-Path "$PSScriptRoot\out\PresenceLock.msix") { Remove-Item "$PSScriptRoot\out\PresenceLock.msix" }
Invoke-LinuxPodman @('run','--rm',
    '-v',"${repoLinux}:/src",
    '-e','LD_LIBRARY_PATH=/src/tools/linux','-w','/src',$packImage,
    '/src/tools/linux/makemsix','pack','-d','/src/msix-layout','-p','/src/out/PresenceLock.msix')

# 6. Sign on the host — the key stays in exactly one trust domain
$signtool = (Get-ChildItem "$PSScriptRoot\tools\bin\*\x64\signtool.exe" | Select-Object -Last 1).FullName
# PFX password precedence: $env:PRESENCELOCK_PFX_PASSWORD, if set, wins over the
# on-disk fallback. This lets a host operator export the password for the
# session instead of leaving it at rest in cert\pfx-password.txt. Either way
# the value lives only under cert\, which is git-ignored (never reaches source
# control) and used solely by this host-only signing step.
if ($env:PRESENCELOCK_PFX_PASSWORD) {
    $pw = $env:PRESENCELOCK_PFX_PASSWORD
} else {
    $pw = (Get-Content "$PSScriptRoot\cert\pfx-password.txt" -Raw).Trim()
}
& $signtool sign /fd SHA256 /f "$PSScriptRoot\cert\PresenceLock.pfx" /p $pw "$PSScriptRoot\out\PresenceLock.msix"
if ($LASTEXITCODE) { throw 'signtool failed' }

Write-Host "Packed and signed: $PSScriptRoot\out\PresenceLock.msix"
