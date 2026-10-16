/*
 * Minimal VST2-compatible ABI declarations for OpenCue Dynamics.
 * Adapted from aeffectx.h, the GPL VeSTige/LMMS compatibility header:
 * Copyright (c) 2006 Javier Serrano Polo <jasp00/at/users.sourceforge.net>
 * Original project: Linux MultiMedia Studio - http://lmms.sourceforge.net
 * Source examined: Equalizer APO, helpers/aeffectx.h, public source repository.
 * https://sourceforge.net/p/equalizerapo/code/HEAD/tree/trunk/helpers/aeffectx.h
 *
 * This header is free software: you can redistribute it and/or modify it
 * under the GNU General Public License as published by the Free Software
 * Foundation, either version 2, or (at your option) any later version.
 * It is distributed WITHOUT ANY WARRANTY; without even the implied warranty
 * of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See COPYING.txt.
 *
 * OpenCue modifications (2026): C-compatible declarations; standard field
 * names including object; reserved VST2.4 tail; only the opcodes used here.
 * No proprietary SDK is included. No DSP is derived from ArtIsWar or RaRa.
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#ifndef OPENCUE_VST2_ABI_H
#define OPENCUE_VST2_ABI_H

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct OpenCueAEffect OpenCueAEffect;
typedef intptr_t (*OpenCueAudioMaster)(OpenCueAEffect *, int32_t, int32_t,
                                       intptr_t, void *, float);
typedef intptr_t (*OpenCueDispatcher)(OpenCueAEffect *, int32_t, int32_t,
                                      intptr_t, void *, float);
typedef void (*OpenCueProcess)(OpenCueAEffect *, float **, float **, int32_t);

struct OpenCueAEffect {
    int32_t magic;
    OpenCueDispatcher dispatcher;
    OpenCueProcess process;
    void (*setParameter)(OpenCueAEffect *, int32_t, float);
    float (*getParameter)(OpenCueAEffect *, int32_t);
    int32_t numPrograms;
    int32_t numParams;
    int32_t numInputs;
    int32_t numOutputs;
    int32_t flags;
    void *reserved1;
    void *reserved2;
    int32_t initialDelay;
    int32_t realQualities;
    int32_t offQualities;
    float ioRatio;
    void *object; /* Owned by the plug-in. */
    void *user;   /* Owned by the host: Equalizer APO stores its instance here. */
    int32_t uniqueID;
    int32_t version;
    OpenCueProcess processReplacing;
    void (*processDoubleReplacing)(OpenCueAEffect *, double **, double **,
                                    int32_t);
    char future[56];
};

enum {
    ocAudioMasterVersion = 1,
    ocEffOpen = 0,
    ocEffClose = 1,
    ocEffSetProgram = 2,
    ocEffGetProgram = 3,
    ocEffSetProgramName = 4,
    ocEffGetProgramName = 5,
    ocEffGetParamLabel = 6,
    ocEffGetParamDisplay = 7,
    ocEffGetParamName = 8,
    ocEffSetSampleRate = 10,
    ocEffSetBlockSize = 11,
    ocEffMainsChanged = 12,
    ocEffCanBeAutomated = 26,
    ocEffGetProgramNameIndexed = 29,
    ocEffGetPlugCategory = 35,
    ocEffGetEffectName = 45,
    ocEffGetVendorString = 47,
    ocEffGetProductString = 48,
    ocEffGetVendorVersion = 49,
    ocEffCanDo = 51,
    ocEffGetVstVersion = 58,
    ocEffStartProcess = 71,
    ocEffStopProcess = 72,
    ocEffFlagsCanReplacing = 1 << 4,
    ocEffectMagic = 0x56737450 /* 'VstP' */
};

#if INTPTR_MAX == INT64_MAX
_Static_assert(offsetof(OpenCueAEffect, object) == 96, "x64 ABI object offset");
_Static_assert(offsetof(OpenCueAEffect, user) == 104, "x64 ABI user offset");
_Static_assert(offsetof(OpenCueAEffect, processReplacing) == 120,
               "x64 ABI processReplacing offset");
_Static_assert(sizeof(OpenCueAEffect) == 192, "x64 ABI size");
#endif

#if defined(_WIN32)
#define OC_EXPORT __declspec(dllexport)
#else
#define OC_EXPORT __attribute__((visibility("default")))
#endif

OC_EXPORT OpenCueAEffect *VSTPluginMain(OpenCueAudioMaster host);

#ifdef __cplusplus
}
#endif
#endif
