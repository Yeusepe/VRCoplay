# Third-party notices

VRCoplay bundles or depends on the following software. The corresponding license files are copied into the application package under `Tools`.

## FFmpeg

- Build: `2026-05-28-git-7b46c6a2a3-essentials_build-www.gyan.dev`
- License: GNU GPL version 3
- Binary distribution: <https://github.com/GyanD/codexffmpeg/releases/tag/2026-05-28-git-7b46c6a2a3>
- Corresponding FFmpeg source: <https://github.com/FFmpeg/FFmpeg/commit/7b46c6a2a3>
- Build configuration and enabled libraries: `Tools/FFmpeg-README.txt`
- License text: `Tools/FFmpeg-GPL-3.0.txt`

The source revision and complete build configuration needed to reproduce and audit this binary are retained with the package metadata. A distributor of VRCoplay must continue satisfying GPLv3 source-distribution requirements for FFmpeg and its statically linked GPL components.

## MediaMTX

- Version: 1.19.3
- Source: <https://github.com/bluenviron/mediamtx/tree/v1.19.3>
- License: MIT, `Tools/MediaMTX-LICENSE.txt`

## NAudio

- Package: `NAudio.Wasapi` 3.0.0-preview.19 (and its `NAudio.Core` dependency)
- Source revision: <https://github.com/naudio/NAudio/tree/6def00b5a41a7904f3b104eda8f92a1c59be7e5a>
- License: MIT, `Tools/NAudio-LICENSE.txt`

## Microsoft Windows App SDK

- Package: `Microsoft.WindowsAppSDK` 2.3.1
- Project and license information: <https://github.com/microsoft/WindowsAppSDK>

Exact hashes of the native tools and production configuration are in `Tools/SHA256SUMS.txt`.
