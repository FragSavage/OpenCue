/*
 * OpenCue Dynamics: original linked stereo compressor and sample peak limiter.
 * Copyright (c) 2026 OpenCue contributors.
 * SPDX-License-Identifier: GPL-3.0-or-later
 * See COPYING.txt. This program is provided without any warranty.
 *
 * This is a general audio dynamics effect. It does not recognize footsteps,
 * separate gunshots, reconstruct missing game audio, or implement a spatial
 * engine. It contains no ArtIsWar or RaRa DSP, presets, assets, or binaries.
 */
#include "opencue_vst2_abi.h"

#include <math.h>
#include <float.h>
#include <stdatomic.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* The first three indices remain compatible with version 1 configs. */
enum {
    PARAM_THRESHOLD, PARAM_RATIO, PARAM_CEILING, PARAM_COMP_ON,
    PARAM_ATTACK, PARAM_RELEASE, PARAM_MAKEUP, PARAM_TRANSIENT,
    PARAM_AMBIENCE, PARAM_FLOOR, PARAM_LEVELING, PARAM_WIDTH,
    PARAM_BALANCE, PARAM_LIMIT_ON, PARAM_COUNT
};

static const char *parameterNames[PARAM_COUNT] = {
    "Threshold", "Ratio", "Ceiling", "CompOn", "Attack", "Release", "Makeup",
    "Transient", "Ambience", "Floor", "Leveling", "Width", "Balance", "LimitOn"
};

static const float parameterDefaults[PARAM_COUNT] = {
    0.4f, 2.0f / 7.0f, 11.0f / 11.5f, 1.0f,
    3.0f / 49.0f, 80.0f / 480.0f, 0.0f, 0.0f,
    0.0f, 0.5f, 0.0f, 0.5f, 0.5f, 1.0f
};

typedef struct {
    OpenCueAEffect effect;
    _Atomic float parameters[PARAM_COUNT];
    _Atomic float sampleRate;
    double envelope;
    double limiterGain;
    double ambienceEnvelope;
    double transientFast;
    double transientSlow;
    double transientGain;
    double levelPower;
    double levelGainDb;
} Dynamics;

static Dynamics *state(OpenCueAEffect *effect) {
    return effect ? (Dynamics *)effect->object : NULL;
}

static double clamp(double value, double low, double high) {
    return value < low ? low : value > high ? high : value;
}

static void reset(Dynamics *d) {
    d->envelope = 0.0;
    d->limiterGain = 1.0;
    d->ambienceEnvelope = 0.0;
    d->transientFast = 0.0;
    d->transientSlow = 0.0;
    d->transientGain = 1.0;
    d->levelPower = 0.0;
    d->levelGainDb = 0.0;
}

static void setParameter(OpenCueAEffect *effect, int32_t index, float value) {
    Dynamics *d = state(effect);
    if (d && index >= 0 && index < PARAM_COUNT && isfinite(value))
        atomic_store_explicit(&d->parameters[index],
                              (float)clamp(value, 0.0, 1.0),
                              memory_order_relaxed);
}

static float getParameter(OpenCueAEffect *effect, int32_t index) {
    Dynamics *d = state(effect);
    return d && index >= 0 && index < PARAM_COUNT
        ? atomic_load_explicit(&d->parameters[index], memory_order_relaxed)
        : 0.0f;
}

static void processBlock(OpenCueAEffect *effect, float **input,
                         float **output, int32_t count, int accumulate) {
    Dynamics *d = state(effect);
    if (!d || !output || !output[0] || !output[1] || count <= 0) return;

    const double threshold = -30.0 + 30.0 * getParameter(effect, PARAM_THRESHOLD);
    const double ratio = 1.0 + 7.0 * getParameter(effect, PARAM_RATIO);
    const double ceilingDb = -12.0 + 11.5 * getParameter(effect, PARAM_CEILING);
    const double ceiling = pow(10.0, ceilingDb / 20.0);
    const int compOn = getParameter(effect, PARAM_COMP_ON) >= 0.5f;
    const int limitOn = getParameter(effect, PARAM_LIMIT_ON) >= 0.5f;
    const double attackMs = 1.0 + 49.0 * getParameter(effect, PARAM_ATTACK);
    const double releaseMs = 20.0 + 480.0 * getParameter(effect, PARAM_RELEASE);
    const double makeup = pow(10.0, 12.0 * getParameter(effect, PARAM_MAKEUP) / 20.0);
    const double transient = getParameter(effect, PARAM_TRANSIENT);
    const double ambience = getParameter(effect, PARAM_AMBIENCE);
    const double floorDb = -80.0 + 60.0 * getParameter(effect, PARAM_FLOOR);
    const double leveling = getParameter(effect, PARAM_LEVELING);
    const double width = 2.0 * getParameter(effect, PARAM_WIDTH);
    const double balance = 2.0 * getParameter(effect, PARAM_BALANCE) - 1.0;
    /* Round the final float guard toward zero, never above requested ceiling. */
    float finalBound = (float)ceiling;
    if ((double)finalBound > ceiling) finalBound = nextafterf(finalBound, 0.0f);
    const double rate = atomic_load_explicit(&d->sampleRate, memory_order_relaxed);
    const double attack = exp(-1.0 / (0.001 * attackMs * rate));
    const double release = exp(-1.0 / (0.001 * releaseMs * rate));
    const double limiterRelease = exp(-1.0 / (0.100 * rate));
    const double ambienceAttack = exp(-1.0 / (0.005 * rate));
    const double ambienceRelease = exp(-1.0 / (0.150 * rate));
    const double transientFastAttack = exp(-1.0 / (0.0002 * rate));
    const double transientFastRelease = exp(-1.0 / (0.010 * rate));
    const double transientSlowAttack = exp(-1.0 / (0.020 * rate));
    const double transientSlowRelease = exp(-1.0 / (0.150 * rate));
    const double transientGainAttack = exp(-1.0 / (0.0005 * rate));
    const double transientGainRelease = exp(-1.0 / (0.035 * rate));
    const double rmsCoefficient = exp(-1.0 / (0.150 * rate));
    const double levelBoostCoefficient = exp(-1.0 / (0.350 * rate));
    const double levelCutCoefficient = exp(-1.0 / (0.150 * rate));
    const double silenceGate = pow(10.0, -65.0 / 20.0);
    const float *left = input ? input[0] : NULL;
    const float *right = input ? input[1] : NULL;

    for (int32_t i = 0; i < count; ++i) {
        /* Read both channels before writing, including for in-place hosts. */
        const double l = left && isfinite(left[i]) ? left[i] : 0.0;
        const double r = right && isfinite(right[i]) ? right[i] : 0.0;
        double outL = l;
        double outR = r;
        double peak = fmax(fabs(outL), fabs(outR));

        /* Stereo-linked downward expansion. At full amount it attenuates by
         * 0.8 dB per dB below Floor, bounded to 24 dB; it is never a mute gate. */
        if (ambience > 0.0) {
            const double c = peak > d->ambienceEnvelope ? ambienceAttack : ambienceRelease;
            d->ambienceEnvelope = c * d->ambienceEnvelope + (1.0 - c) * peak;
            if (d->ambienceEnvelope < 1e-30) d->ambienceEnvelope = 0.0;
            const double db = 20.0 * log10(fmax(d->ambienceEnvelope, 1e-30));
            const double gain = pow(10.0, -ambience * clamp((floorDb - db) * 0.8, 0.0, 24.0) / 20.0);
            outL *= gain;
            outR *= gain;
            peak *= gain;
        } else {
            d->ambienceEnvelope = 0.0;
        }

        /* A broad onset detector, not a sound classifier: rising linked peak
         * energy compared with its slower envelope triggers up to 18 dB cut. */
        if (transient > 0.0) {
            double c = peak > d->transientFast ? transientFastAttack : transientFastRelease;
            d->transientFast = c * d->transientFast + (1.0 - c) * peak;
            c = peak > d->transientSlow ? transientSlowAttack : transientSlowRelease;
            d->transientSlow = c * d->transientSlow + (1.0 - c) * peak;
            if (d->transientFast < 1e-30) d->transientFast = 0.0;
            if (d->transientSlow < 1e-30) d->transientSlow = 0.0;
            const double contrastDb = 20.0 * log10(
                fmax(d->transientFast, 1e-30) / fmax(d->transientSlow, 1e-30));
            const double wanted = pow(10.0, -transient * clamp(contrastDb - 3.0, 0.0, 18.0) / 20.0);
            c = wanted < d->transientGain ? transientGainAttack : transientGainRelease;
            d->transientGain = c * d->transientGain + (1.0 - c) * wanted;
            outL *= d->transientGain;
            outR *= d->transientGain;
            peak *= d->transientGain;
        } else {
            d->transientGain = 1.0;
            d->transientFast = d->transientSlow = 0.0;
        }

        const double coefficient = peak > d->envelope ? attack : release;
        d->envelope = coefficient * d->envelope + (1.0 - coefficient) * peak;
        if (d->envelope < 1e-30) d->envelope = 0.0;

        const double level = 20.0 * log10(fmax(d->envelope, 1e-30));
        const double reductionDb = fmax(0.0, level - threshold) * (1.0 - 1.0 / ratio);
        const double compressorGain = compOn ? pow(10.0, -reductionDb / 20.0) : 1.0;
        outL *= compressorGain * makeup;
        outR *= compressorGain * makeup;

        /* Slow RMS leveler, bounded to +/-12 dB at full amount. Samples whose
         * linked peak is below -65 dBFS never receive positive leveler gain. */
        if (leveling > 0.0) {
            const double power = 0.5 * (outL * outL + outR * outR);
            d->levelPower = rmsCoefficient * d->levelPower + (1.0 - rmsCoefficient) * power;
            if (d->levelPower < 1e-60) d->levelPower = 0.0;
            const double rmsDb = 10.0 * log10(fmax(d->levelPower, 1e-60));
            const double wantedDb = rmsDb >= -65.0 ? leveling * clamp(-24.0 - rmsDb, -12.0, 12.0) : 0.0;
            const double c = wantedDb < d->levelGainDb ? levelCutCoefficient : levelBoostCoefficient;
            d->levelGainDb = c * d->levelGainDb + (1.0 - c) * wantedDb;
            if (fabs(d->levelGainDb) < 1e-12) d->levelGainDb = 0.0;
            double appliedDb = d->levelGainDb;
            if (fmax(fabs(outL), fabs(outR)) < silenceGate) appliedDb = fmin(appliedDb, 0.0);
            const double gain = pow(10.0, appliedDb / 20.0);
            outL *= gain;
            outR *= gain;
        } else {
            d->levelGainDb = 0.0;
            d->levelPower = 0.0;
        }

        if (width != 1.0) {
            const double mid = 0.5 * (outL + outR);
            const double side = 0.5 * (outL - outR) * width;
            outL = mid + side;
            outR = mid - side;
        }
        if (balance > 0.0) outL *= 1.0 - balance;
        else if (balance < 0.0) outR *= 1.0 + balance;

        if (accumulate) {
            outL += isfinite(output[0][i]) ? output[0][i] : 0.0;
            outR += isfinite(output[1][i]) ? output[1][i] : 0.0;
        }

        /* The linked limiter is FINAL, after every gain/spatial control. */
        double bound = FLT_MAX;
        if (limitOn) {
            const double finalPeak = fmax(fabs(outL), fabs(outR));
            const double requiredLimit = finalPeak > ceiling ? ceiling / finalPeak : 1.0;
            if (requiredLimit < d->limiterGain) d->limiterGain = requiredLimit;
            else d->limiterGain = fmin(requiredLimit,
                limiterRelease * d->limiterGain + (1.0 - limiterRelease));
            outL *= d->limiterGain;
            outR *= d->limiterGain;
            bound = finalBound;
        } else {
            d->limiterGain = 1.0;
        }
        /* Finite protection remains even with the limiter disabled. */
        output[0][i] = isfinite(outL)
            ? (float)clamp(outL, -bound, bound) : 0.0f;
        output[1][i] = isfinite(outR)
            ? (float)clamp(outR, -bound, bound) : 0.0f;
    }
}

static void processReplacing(OpenCueAEffect *effect, float **input,
                             float **output, int32_t count) {
    processBlock(effect, input, output, count, 0);
}

static void processAccumulating(OpenCueAEffect *effect, float **input,
                                float **output, int32_t count) {
    processBlock(effect, input, output, count, 1);
}

static void copyText(void *destination, const char *text, size_t limit) {
    if (destination && limit) {
        char *out = destination;
        const size_t size = strlen(text);
        const size_t n = size < limit - 1 ? size : limit - 1;
        memcpy(out, text, n);
        out[n] = '\0';
    }
}

static intptr_t dispatcher(OpenCueAEffect *effect, int32_t opcode,
                           int32_t index, intptr_t value, void *ptr, float opt) {
    Dynamics *d = state(effect);
    if (!d) return 0;
    switch (opcode) {
    case ocEffOpen: return 1;
    case ocEffClose: free(d); return 1;
    case ocEffSetProgram: return value == 0;
    case ocEffGetProgram: return 0;
    case ocEffSetProgramName: return 0;
    case ocEffGetProgramName:
    case ocEffGetProgramNameIndexed: copyText(ptr, "OpenCue", 24); return 1;
    case ocEffGetParamName:
        /* APO supplies a 256-byte name buffer; exact names are its config keys. */
        if (index < 0 || index >= PARAM_COUNT) return 0;
        copyText(ptr, parameterNames[index], strlen(parameterNames[index]) + 1);
        return 1;
    case ocEffGetParamLabel:
        if (index < 0 || index >= PARAM_COUNT) return 0;
        copyText(ptr, index == PARAM_RATIO ? ":1" :
            index == PARAM_ATTACK || index == PARAM_RELEASE ? "ms" :
            index == PARAM_THRESHOLD || index == PARAM_CEILING || index == PARAM_MAKEUP || index == PARAM_FLOOR
                ? "dB" : "", 8); return 1;
    case ocEffGetParamDisplay: {
        if (!ptr || index < 0 || index >= PARAM_COUNT) return 0;
        const double p = getParameter(effect, index);
        if (index == PARAM_COMP_ON || index == PARAM_LIMIT_ON) {
            copyText(ptr, p >= 0.5 ? "On" : "Off", 8); return 1;
        }
        const double amount = index == PARAM_THRESHOLD ? -30.0 + 30.0 * p
            : index == PARAM_RATIO ? 1.0 + 7.0 * p
            : index == PARAM_CEILING ? -12.0 + 11.5 * p
            : index == PARAM_ATTACK ? 1.0 + 49.0 * p
            : index == PARAM_RELEASE ? 20.0 + 480.0 * p
            : index == PARAM_MAKEUP ? 12.0 * p
            : index == PARAM_FLOOR ? -80.0 + 60.0 * p
            : index == PARAM_WIDTH ? 2.0 * p
            : index == PARAM_BALANCE ? 2.0 * p - 1.0 : p * 100.0;
        char display[8];
        snprintf(display, sizeof(display), "%.1f", amount);
        copyText(ptr, display, 8); return 1;
    }
    case ocEffSetSampleRate:
        if (isfinite(opt) && opt >= 8000.0f && opt <= 384000.0f) {
            atomic_store_explicit(&d->sampleRate, opt, memory_order_relaxed);
            reset(d);
            return 1;
        }
        return 0;
    case ocEffSetBlockSize: return value > 0;
    case ocEffMainsChanged: reset(d); return 1;
    case ocEffCanBeAutomated: return index >= 0 && index < PARAM_COUNT;
    case ocEffGetPlugCategory: return 4; /* Mastering/dynamics. */
    case ocEffGetEffectName: copyText(ptr, "OpenCue Dynamics", 32); return 1;
    case ocEffGetVendorString: copyText(ptr, "OpenCue", 64); return 1;
    case ocEffGetProductString: copyText(ptr, "OpenCue Dynamics", 64); return 1;
    case ocEffGetVendorVersion: return 2000;
    case ocEffGetVstVersion: return 2400;
    case ocEffCanDo: return 0;
    case ocEffStartProcess:
    case ocEffStopProcess: return 1;
    default: return 0;
    }
}

OC_EXPORT OpenCueAEffect *VSTPluginMain(OpenCueAudioMaster host) {
    if (!host || !host(NULL, ocAudioMasterVersion, 0, 0, NULL, 0.0f)) return NULL;
    Dynamics *d = calloc(1, sizeof(*d));
    if (!d) return NULL;
    for (int i = 0; i < PARAM_COUNT; ++i)
        atomic_init(&d->parameters[i], parameterDefaults[i]);
    atomic_init(&d->sampleRate, 48000.0f);
    reset(d);
    d->effect.magic = ocEffectMagic;
    d->effect.dispatcher = dispatcher;
    d->effect.process = processAccumulating;
    d->effect.setParameter = setParameter;
    d->effect.getParameter = getParameter;
    d->effect.numPrograms = 1;
    d->effect.numParams = PARAM_COUNT;
    d->effect.numInputs = 2;
    d->effect.numOutputs = 2;
    d->effect.flags = ocEffFlagsCanReplacing;
    d->effect.initialDelay = 0;
    d->effect.ioRatio = 1.0f;
    d->effect.object = d;
    d->effect.user = NULL;
    d->effect.uniqueID = 0x4f434459; /* 'OCDY' */
    d->effect.version = 2000;
    d->effect.processReplacing = processReplacing;
    return &d->effect;
}
