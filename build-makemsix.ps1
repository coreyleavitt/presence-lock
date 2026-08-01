#Requires -Version 7
# Builds makemsix (Microsoft's open-source cross-platform MSIX packer) from a
# pinned commit, inside a Linux container, and installs the binaries to
# tools/linux/ (gitignored, like the Windows SDK tools in tools/bin/).
#
# Pack support is a build-time option: --pack sets MSIX_PACK=on and couples
# USE_VALIDATION_PARSER=on. Zlib/Xerces-C/OpenSSL are vendored subtrees and
# statically linked; ICU is NOT (hence the libicu74 runtime image, see
# containers/pack.Containerfile). Ubuntu 24.04's apt cmake (3.28) is older than
# the repo's requirement (>= 3.29), so cmake comes from PyPI.
$ErrorActionPreference = 'Stop'

# Pinned 2026-08-01; verified: clean build, 0/295 payload hash mismatches vs
# makeappx, correct SHA256 block map, manifest not rewritten, signable, installable.
$MakemsixCommit = 'efeb9dad695a200c2beaddcba54a52c8320bd135'

$repoLinux = '/mnt/' + $PSScriptRoot.Replace('\', '/').Replace(':', '').ToLower()[0] + $PSScriptRoot.Replace('\', '/').Substring(2)
New-Item -ItemType Directory -Force "$PSScriptRoot\tools\linux" | Out-Null

$build = @"
set -e
apt-get update -qq
apt-get install -y -qq clang git ca-certificates pkg-config uuid-dev libssl-dev zlib1g-dev build-essential python3-pip >/dev/null
python3 -m pip install --break-system-packages -q --upgrade cmake
hash -r
git clone https://github.com/microsoft/msix-packaging /tmp/msix-packaging
cd /tmp/msix-packaging
git checkout $MakemsixCommit
./makelinux.sh --pack -sb --skip-tests --skip-samples -b Release
cp .vs/bin/makemsix .vs/lib/libmsix.so /out/
"@

wsl -d podman-machine-default -u user -- podman run --rm `
    -v "${repoLinux}/tools/linux:/out" `
    docker.io/library/ubuntu:24.04 bash -c $build
if ($LASTEXITCODE) { throw 'makemsix build failed' }

Get-FileHash "$PSScriptRoot\tools\linux\makemsix", "$PSScriptRoot\tools\linux\libmsix.so" -Algorithm SHA256
Write-Host "makemsix built from $MakemsixCommit into tools\linux"
