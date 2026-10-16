# OpenCue Dynamics

Original stereo dynamics plug-in for the free OpenCue app and Equalizer APO's
64-bit VST host. No proprietary VST SDK, RaRa code or ArtIsWar DSP is included.
The small ABI header is adapted from LMMS's GPL-2.0-or-later compatibility
header. The original plug-in and test harness are GPL-3.0-or-later.

Version 2 has two inputs, two outputs, one program, no graphical editor,
zero reported latency, and the exported entry point `VSTPluginMain`.
State lives in `AEffect.object`; `AEffect.user` remains available to the host.

| Index / exact parameter key | Normalized mapping, p in 0–1 | Default |
|---|---|---|
| 0 `Threshold` | -30 + 30p dB | -18 dB; p=0.4 |
| 1 `Ratio` | 1 + 7p | 3:1; p=2/7 |
| 2 `Ceiling` | -12 + 11.5p dBFS | -1 dBFS; p=11/11.5 |
| 3 `CompOn` | Off below 0.5; on at/above 0.5 | On; p=1 |
| 4 `Attack` | 1 + 49p ms | 4 ms; p=3/49 |
| 5 `Release` | 20 + 480p ms | 100 ms; p=1/6 |
| 6 `Makeup` | 12p dB | 0 dB; p=0 |
| 7 `Transient` | Broad onset attenuation amount, maximum 18p dB | Off; p=0 |
| 8 `Ambience` | Downward expansion amount | Off; p=0 |
| 9 `Floor` | -80 + 60p dBFS | -50 dBFS; p=0.5 |
| 10 `Leveling` | Slow RMS leveler amount, maximum ±12p dB | Off; p=0 |
| 11 `Width` | Stereo side gain 2p | Unchanged width 1; p=0.5 |
| 12 `Balance` | Left/right bias 2p-1 | Center 0; p=0.5 |
| 13 `LimitOn` | Off below 0.5; on at/above 0.5 | On; p=1 |

The first three indices and their defaults remain compatible with version 1.
New defaults leave the original compressor/limiter signal behavior intact.
Processing order is ambience → transient → compressor → manual makeup →
leveling → width → balance → **final limiter**. Existing EQ and preamp can
precede this plug-in. There are no allocations or locks in the audio callback.

The compressor uses one linked peak detector for both channels. Attack and
release apply to that detector. Makeup is manual and works independently of
`CompOn`; disabling the compressor does not silently remove a chosen makeup
gain. Ambience attenuates low-level material below Floor by up to 24 dB at
full amount, with 5 ms attack/150 ms release detection; it never becomes a
hard mute gate. Transient compares fast and slow linked peak envelopes,
attenuating broad sudden onsets by at most 18 dB at full amount, with recovery.
Both affect any matching material, including wanted quiet sounds or onsets.

Leveling measures stereo RMS over approximately 150 ms, moves toward a
-24 dBFS target, and limits its gain correction to ±12 dB at full amount.
Positive gain moves over approximately 350 ms; reduction over 150 ms.
Samples with a linked peak below -65 dBFS receive no positive leveling gain,
so silence and very quiet noise are not boosted. It is a broad level control.

Width uses a mid/side matrix: 0 makes mono, 1 is unchanged, 2 doubles side
content. It does not create new spatial directions. Balance attenuates the
opposite channel: -1 leaves left only, 0 is unchanged, +1 leaves right only.

With `LimitOn` enabled, the linked sample limiter catches current peaks
immediately and releases over 100 ms after every other stage. Its final
rounding guard bounds output to Ceiling. With `LimitOn` disabled, signals may
exceed 0 dBFS or Ceiling; only the maximum representable float bound remains.
Invalid input samples become zero; invalid parameters are ignored; finite
parameters clamp to 0–1. The neutral controls with `CompOn=0` and `LimitOn=0`
produce exact identity for finite float input, including samples above 0 dBFS.

The limiter provides **sample-peak protection**, with no lookahead, oversampling or true-peak
claim. It cannot identify footsteps, selectively remove gunfire, restore cues
the game did not render, or create multichannel spatial information.

Equalizer APO accepts the exact parameter names above in its config. It uses a
256-byte parameter-name buffer, which accommodates `Threshold`. Generic VST
hosts that impose the historical eight-byte short-name limit are not the
target of this build.

## Build and run tests

From the workspace root in PowerShell, with a portable Zig compiler:

```powershell
$zig = 'C:\path\to\zig.exe'
$source = 'outputs\OpenCue\source\Dynamics'
New-Item -ItemType Directory -Force 'outputs\OpenCue\engine', 'work\dynamics-build' | Out-Null
& $zig cc -std=c11 -O2 -fno-fast-math -shared -o outputs\OpenCue\engine\OpenCueDynamics.dll "$source\OpenCueDynamics.c" -lm
& $zig cc -std=c11 -O2 -fno-fast-math -o work\dynamics-build\OpenCueDynamicsTests.exe "$source\test_dynamics.c" -lm
& work\dynamics-build\OpenCueDynamicsTests.exe (Resolve-Path outputs\OpenCue\engine\OpenCueDynamics.dll)
```

Use a Windows x64 Zig build/target for the intended Equalizer APO host. Do not
use `-ffast-math`, which can invalidate finite-value checks. The dynamic test
harness exercises the actual DLL: ABI and exact control names, host-owned user
state, parameter changes, silence, compression, stereo linkage, sample ceiling,
invalid/extreme values, in-place buffers and block continuity. Expanded tests
verify timing, makeup, transient reduction/recovery, downward expansion, gated
bounded leveling, width, balance, final limiting after boosts and exact bypass
at 44.1, 48 and 96 kHz. An optional second library path compares neutral output
directly with the original version 1 DLL. The harness does not duplicate DSP.

## Configuration example

```text
Channel: L R
VSTPlugin: Library "C:\path\to\OpenCueDynamics-v2.dll" Threshold 0.4 Ratio 0.2857143 Ceiling 0.9565217 CompOn 1 Attack 0.06122449 Release 0.16666667 Makeup 0 Transient 0 Ambience 0 Floor 0.5 Leveling 0 Width 0.5 Balance 0.5 LimitOn 1
```

Keep the plug-in after EQ and any makeup/output gain if its ceiling should be
the final sample bound. Gain or effects placed after it can exceed its ceiling.
The caller must target the intended device and preserve existing APO config.

## Provenance

The ABI field layout and public opcode identifiers were adapted from
[LMMS/VeSTige's GPL compatibility header as vendored by Equalizer APO](https://sourceforge.net/p/equalizerapo/code/HEAD/tree/trunk/helpers/aeffectx.h).
Its original copyright notice remains in `opencue_vst2_abi.h`. Equalizer APO's
public `VSTPluginInstance.cpp` was examined to verify `user` ownership and
parameter-key loading. No processing implementation was copied from it.
