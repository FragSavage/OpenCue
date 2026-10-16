# OpenCue

Free Windows audio controls for headphones, IEMs and speakers. OpenCue combines original EQ and dynamics, measured 7.1 headphone rendering, and a virtual-cable audio relay in one native app. There is no account, subscription or paid unlock.

**Windows x64 · Version 0.2.0 · GPL-3.0-or-later**

[Download OpenCue 0.2.0 for Windows x64](https://github.com/FragSavage/OpenCue/releases/download/v0.2.0/OpenCue-0.2.0-Windows-x64.zip) · [All releases](https://github.com/FragSavage/OpenCue/releases)

Download **`OpenCue-0.2.0-Windows-x64.zip`** from Releases and extract the whole ZIP. The supplied `OpenCue.exe` includes its .NET runtime. GitHub's automatic source archives contain source, not the ready-to-run application or optional LEQ executable.

## Start with normal stereo audio

1. Open `OpenCue.exe`, choose **Setup**, and select **Start setup**. OpenCue downloads a pinned, SHA-256-checked Equalizer APO installer from its official project.
2. Approve the Windows installer, keep its default folder, and select your **playback output** in Device Selector. Leave microphones unselected. Restart if requested.
3. Run Device Selector's installation test for that output. Keep its Windows audio enhancements enabled.
4. In **Sound**, select the same output, choose **Neutral**, and select **Apply sound**. Begin at a comfortable volume.
5. Compare with **Bypass**. Turn on **Apply changes live** if you want subsequent adjustments written automatically. **Remove tuning** removes OpenCue's integration.

See [START-HERE.txt](START-HERE.txt) for the complete setup and troubleshooting guide. OpenCue saves configuration; the engine's attachment test and a real listening check establish whether your selected audio path is being processed. Exclusive-mode and ASIO paths can bypass Equalizer APO.

## What is implemented

- Ten-band parametric tone EQ, preamp/headroom control, and numeric headphone correction import from compatible parametric or GraphicEQ text.
- Compression with threshold, ratio, attack, release and makeup gain; independent sample peak protection; gated automatic leveling.
- Transient reduction, low-level ambience reduction, stereo width and balance.
- Natural, Compressed and Boost modes; editable controls; JSON preset import/export; optional live apply; bypass and configuration removal/recovery.
- Original 7.1-to-binaural rendering using measured MIT KEMAR responses.
- A WASAPI relay from supported virtual-cable recording devices to physical headphones, with playback level, buffer control and an actual outgoing peak meter.
- Optional launching of the supplied official LEQ Control Panel and integration of a separately installed official ArtTune 16-channel profile.

Transient reduction affects sharp onsets generally, including gunfire and other sounds. Ambience reduction can also reduce quiet cues. These processors do not classify game-specific sources. Peak protection limits sample peaks rather than true peaks. Stereo width alone does not create surround channels.

## Use actual 7.1 game audio

This route needs an eight-channel virtual playback output, Equalizer APO attached to it, and the matching virtual recording input.

1. In **Spatial & routing**, select **Set up virtual cable**. The app downloads and verifies VB-Audio's base VB-CABLE package, then opens its installer. Install the driver and restart if requested.
2. In Windows sound settings, configure **CABLE Input** as standard **7.1 / 48 kHz**. Attach Equalizer APO to it in Device Selector and pass that device's installation test.
3. Refresh devices in OpenCue, select that virtual processing output, and select **Apply 7.1 rendering**.
4. Set the game's output to **CABLE Input**. Keep voice chat on your normal headphones.
5. Choose **OpenCue 7.1** relay mode, the matching **CABLE Output** recording input, and your physical headphones. Select **Start game audio** and keep OpenCue open.

If playback breaks up, stop the relay, increase the buffer, and start again. **Bypass tone** preserves spatial rendering while bypassing tone/dynamics adjustments. **Return to stereo** stops the relay and targets the physical output; select that physical output in the game too.

The capture endpoint can expose a different shared channel count from the playback endpoint. It must carry that same cable's processed front left/right pair. Match the cable endpoints manually and check the real audio route. In OpenCue 7.1 mode, transient reduction runs on front left/right before rendering; other channels retain their transients.

## Optional official tools

**Windows loudness equalization:** the release ZIP includes ArtIsWar's signed, portable LEQ Control Panel v1.0.0 under `tools/`. Open it from **Setup** and choose the output in that utility. Windows requests administrator approval. OpenCue opens the GUI without automatic toggle arguments. Compare Windows LEQ and OpenCue compression separately first. The utility's licence, attribution and corresponding source archive accompany the release.

**Official ArtTune 16-channel route:** install the matching components using the [official ArtTuneDB guide](https://github.com/ArtIsWar/ArtTuneDB). OpenCue does **not** distribute its restricted Bravo engine. **Load installed 16ch preset** accepts the supported unmodified BO7 V5 profile format and requires its matching local x64 Bravo DLL. Apply it to the matching 16-channel virtual playback output, choose **Official ArtTune 16ch** relay mode, and select that cable's recording input and your headphones. Follow the official route's licence and setup requirements; keep Windows LEQ off for that V5 route.

OpenCue's original 7.1 renderer and the official 16-channel engine are separate processing routes. This is an independent project; it does not claim to reproduce proprietary audio products.

## Configuration and recovery

OpenCue scopes its changes to the selected playback output, or to detected playback outputs when explicitly requested. Existing effects can stack with those changes. It keeps backups of its integration. For a configuration with complex routing or an unsupported text format, it asks before replacing the main Equalizer APO configuration and creates exact recovery copies. That replacement affects every device handled by the main file. Declining keeps the previous controller in place.

Avoid competing controllers editing the same configuration. When leaving a virtual route, also restore the game's physical output selection.

## Build from source

Use Windows with the .NET 8 SDK or a compatible newer SDK. Internet access is needed to restore NAudio and the self-contained publish runtime.

```powershell
.\build.ps1
```

This embeds the supplied x64 native DSP DLLs and writes `build/OpenCue.exe`. To rebuild the original native DSP from C as well, supply Zig:

```powershell
.\build.ps1 -ZigPath 'C:\path\to\zig.exe'
```

Place the rebuilt EXE beside the release's `tools` and `licenses` folders to retain optional LEQ discovery. The large Windows app and LEQ executable are distributed through release ZIPs rather than committed to this repository. No audio installers run during the build.

The repository contains `source/`, `engine/`, `licenses/`, `verification/`, the build script, setup guide, test report and licence. Original DSP tests are in `source/Dynamics/` and `source/Spatial/`.

## Verification and current limits

The release passed 33 packaged integration checks, 227 dynamics checks, 64,463 spatial assertions and 49 correction-import smoke checks. Installer downloads matched pinned SHA-256 values. The native UI and read-only Windows device enumeration were checked. See [TEST-RESULTS.txt](TEST-RESULTS.txt) and `verification/` for scope and results.

**Live hardware routing remains unverified.** Equalizer APO and the required virtual devices were not installed during development. Completed driver setup, live game audio, end-to-end latency, device compatibility and perceptual improvement were not verified on the development PC. Numerical/configuration tests do not establish those outcomes.

## Licence and credits

Original OpenCue code and DSP are [GPL-3.0-or-later](LICENSE.txt). NAudio is MIT licensed. MIT KEMAR measurement data and the separately supplied LEQ tool retain their own notices and licences; see `licenses/` and the native source directories. The official ArtTune engine remains separately obtained and subject to its own licence.
