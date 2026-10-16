# OpenCue Spatial

An original fixed 7.1-to-binaural headphone renderer using measured MIT KEMAR
impulse responses by Bill Gardner and Keith Martin. Native DSP is GPL-3.0-or-later;
data use their separately attributed unrestricted-use terms in MIT-KEMAR-NOTICE.txt.
The ABI is the public GPL VeSTige/LMMS compatibility ABI already used by OpenCue.

## Signal contract

* Windows x64 native DLL, exported VSTPluginMain; 8 inputs and 2 outputs.
* Input order: FL, FR, FC, LFE, BL, BR, SL, SR, standard 7.1 mask `0x63F`.
* Output: binaural headphone L/R. This requires actual multichannel game input.
* Fixed source azimuths: -30, +30, 0, centered LFE, -150, +150, -90, +90 degrees.
* Original direct FIR convolution; measured 128-tap responses at 44.1 kHz.
  Initialization resamples with a windowed-sinc kernel to 32–96 kHz; no FFT block delay.
* Fixed summing gain is 0.25 (-12.04 dB). LFE uses a 120-Hz one-pole low-pass
  and 0.5 centered feed. A linked zero-lookahead sample limiter follows the sum,
  with 50-ms recovery and a -1 dBFS sample ceiling. This is not a true-peak limiter.
* No user parameters: enabling the mode performs a specific measured convolution.
  Generic KEMAR cues fit different listeners differently. No head tracking,
  elevation reconstruction, object extraction, or trained gunshot classifier.

## Equalizer APO configuration

The selected endpoint must supply exactly 8 standard 7.1 channels at 32–96 kHz.
Applying the renderer to a stereo physical output cannot recover missing channels.

```text
Device: {ACTUAL-VIRTUAL-RENDER-ENDPOINT-GUID}
Channel: L R C LFE RL RR SL SR
VSTPlugin: Library "C:\absolute\readable\OpenCueSpatial-v1.dll"
Channel: L R
# stereo headphone correction and output dynamics follow
```

Equalizer APO's public VSTPluginFilter implementation creates an instance for
the selected 8 channels, supplies 8 input buffers, and writes the 2 output buffers
to the first two selected channels. It explicitly clears selected channels 3–8
for an 8-in/2-out effect. The endpoint format still has 8 channels. A virtual
cable capture/relay must take resulting first-two-channel L/R to actual stereo
headphones. The renderer does not install a virtual endpoint or change routing.

VSTPluginFilter::initialize returns the existing channel names; do not assume
the endpoint becomes a two-channel device because the DLL has two outputs.
Channel selection retains the native channel order, so check the 0x63F mask.

Official host source:
https://sourceforge.net/p/equalizerapo/code/ci/main/tree/filters/VSTPluginFilter.cpp
https://sourceforge.net/p/equalizerapo/code/ci/main/tree/filters/ChannelFilter.cpp

## Build and verify

From the OpenCue workspace root, using the portable Windows x64 Zig compiler:

```powershell
$zig = 'work\zig\zig-x86_64-windows-0.17.0\zig.exe'
& $zig cc -std=c11 -O2 -Wall -Wextra -fno-fast-math -shared -o outputs\OpenCue\engine\OpenCueSpatial.dll outputs\OpenCue\source\Spatial\OpenCueSpatial.c -lm
& $zig cc -std=c11 -O2 -Wall -Wextra -fno-fast-math -o work\spatial-build\OpenCueSpatialTests.exe outputs\OpenCue\source\Spatial\test_spatial.c -lm
& work\spatial-build\OpenCueSpatialTests.exe (Resolve-Path outputs\OpenCue\engine\OpenCueSpatial.dll)
```

The harness dynamically loads the actual DLL and opens no audio device. It
checks the public ABI, all 8 channel impulse responses against the original
measurements, direction-dependent ear levels and arrival timing, centered
FC/LFE, state continuity across arbitrary blocks, in-place buffers, silence,
reset, invalid/extreme samples, sample bounds, and supported sample rates.
It also measures synthetic 48-kHz rendering against real time on the current host.
See TEST-RESULTS.txt for the recorded run; this is not an audible hardware test.

To regenerate the embedded data from the supplied raw WAVs:

```powershell
& outputs\OpenCue\source\Spatial\generate_hrir.ps1 -KemarDirectory outputs\OpenCue\source\Spatial\data
```

No allocation, lock, file access, device call, or coefficient resampling occurs
inside processReplacing. Coefficient and delay state is private to each instance.
