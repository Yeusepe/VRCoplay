# Third-party notices

VRCoplay bundles or depends on the following software. License files are copied into the application package under `Tools` or `ThirdParty`.
VRCoplay itself is licensed under GPL-3.0-or-later; see the repository's `LICENSE` file.

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

## Vanara

- Packages: `Vanara.PInvoke.DwmApi`, `Vanara.PInvoke.Gdi32`, and `Vanara.Core` 5.0.5
- Source revision: <https://github.com/dahall/Vanara/tree/624318e1e7e294192581291a1e5f5ee3c3d146e2>
- License: MIT, `Tools/Vanara-LICENSE.txt`

## OpenVR

- SDK: OpenVR SDK 2.15.6, tag `v2.15.6`, commit `0924064316de3effbcd1acf1e309182a2deb1c05`
- Source: <https://github.com/ValveSoftware/openvr/tree/v2.15.6>
- Bundled files and exact hashes: `ThirdParty/OpenVR/PIN.json`
- License: BSD 3-Clause, `ThirdParty/OpenVR/LICENSE`

## VIIPER

- In-process library: libVIIPER 0.7.0, commit `6b71b148a2243fab77ee1a46f4e22e00bd7d5a04`
- Source: <https://github.com/Alia5/VIIPER/tree/v0.7.0>
- License: GPL-3.0-or-later, `ThirdParty/VIIPER/LICENSE.txt`
- Library dependency licenses: `ThirdParty/VIIPER/licenses.txt`
- Official C header, release asset, and exact hashes: `ThirdParty/VIIPER/`
- Runtime requirement: separately installed `usbip-win2` 0.9.7.7; it is not bundled by VRCoplay.

VRCoplay links libVIIPER in-process and is therefore distributed under GPL-3.0-or-later.

## Microsoft Windows App SDK

- Package: `Microsoft.WindowsAppSDK` 2.3.1
- Project and license information: <https://github.com/microsoft/WindowsAppSDK>

Exact hashes of the native tools and production configuration are in `Tools/SHA256SUMS.txt`.
