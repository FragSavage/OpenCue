# OpenCue 0.2.0

Free Windows x64 audio controls with original processing and a native interface. No account or subscription is required.

## Download and first use

Download **`OpenCue-0.2.0-Windows-x64.zip`**, extract the whole ZIP, and open `OpenCue.exe`. The application includes its .NET runtime. Use the attached release ZIP for the runnable app; GitHub's source archives do not include the app or optional LEQ executable.

For normal stereo headphones or IEMs, open **Setup → Start setup**, install the verified Equalizer APO release, and attach it to your playback output. Leave microphones unselected and restart if requested. Pass Device Selector's installation test, then choose that same output in **Sound**, select **Neutral**, and select **Apply sound**. Full instructions are in `START-HERE.txt` inside the ZIP.

## Included in this release

- Ten-band tone EQ, conservative headroom control and compatible numeric parametric/GraphicEQ headphone-correction import.
- Compression, attack/release, makeup gain, gated auto leveling and independent sample peak protection.
- General transient and low-level ambience reduction, stereo width and balance.
- Natural, Compressed and Boost modes; editable presets; JSON import/export; optional live apply; bypass and configuration removal/recovery.
- An original measured MIT KEMAR 7.1-to-binaural renderer, plus a WASAPI virtual-cable-to-headphone relay with level, buffer and outgoing peak controls.
- Verified installer download flows for Equalizer APO and the base VB-CABLE package. Installers run only through the user's setup actions.
- Optional launching of the supplied official LEQ Control Panel v1.0.0, with its licence, attribution and corresponding source archive.
- Loading of a supported, unmodified local BO7 V5 16-channel preset for an already installed official ArtTune engine. The matching Bravo engine is required separately and is not bundled.

OpenCue's original 7.1 route requires a virtual playback output configured for eight-channel surround, Equalizer APO attached to that output, and the matching processed capture input. Official 16-channel routing requires the separately installed matching official components. Neither route becomes usable merely by selecting a mode; complete device setup and a real listening check are required.

## Verification

- Packaged app: 33 integration checks passed.
- Original dynamics: 227 numerical/ABI checks passed.
- Original spatial renderer: 64,463 assertions passed across supported sample rates.
- Correction importer: 49 smoke checks passed.
- Official installer downloads matched pinned SHA-256 values; these checks did not execute the installers.
- Native UI, device enumeration, scratch configuration/recovery behavior and isolated relay handling were checked.

The ZIP contains `TEST-RESULTS.txt` and detailed reports in `verification/`.

## Known limits

Live game audio, end-to-end latency, completed driver setup, hardware compatibility and perceptual improvement were **not verified on the development PC**. Equalizer APO and the required virtual devices were not installed there. Use Device Selector's attachment test and verify the full virtual playback/capture/headphone route on your own setup.

Transient reduction acts on sharp onsets generally; ambience reduction can suppress quiet cues. In the original 7.1 route, transient reduction processes front left/right before rendering. Peak protection limits sample peaks, not true peaks. No game-specific sound classifier or proprietary-product equivalence is claimed.

## Source and licensing

Original OpenCue code and DSP are GPL-3.0-or-later. Source, build instructions, native DSP and third-party notices are available in the repository and supplied package. Dependencies and third-party tools/data retain their own licences. The large executable and optional LEQ executable are supplied in the release ZIP rather than repository history.
