# Runtime image for makemsix packing (see build-makemsix.ps1 for the tool build).
# libmsix.so dynamically links ICU 74 (Xerces-C transcoding), so the pack step
# runs on ubuntu:24.04 rather than the Debian-based .NET SDK image (ICU 72).
FROM docker.io/library/ubuntu:24.04
RUN apt-get update -qq \
    && apt-get install -y -qq --no-install-recommends libicu74 \
    && rm -rf /var/lib/apt/lists/*
