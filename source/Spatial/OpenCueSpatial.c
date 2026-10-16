/*
 * Original OpenCue 7.1-to-binaural FIR renderer, Copyright 2026 OpenCue contributors.
 * SPDX-License-Identifier: GPL-3.0-or-later
 * ABI declarations: ../Dynamics/opencue_vst2_abi.h (GPL-2.0-or-later).
 * Measured HRIR data: Copyright 1994 MIT Media Laboratory, Bill Gardner and
 * Keith Martin. Their separate data attribution is in MIT-KEMAR-NOTICE.txt.
 * No proprietary DSP or paid preset is used. No elevation/head tracking.
 */
#include "../Dynamics/opencue_vst2_abi.h"
#include "kemar_hrir.h"
#include <math.h>
#include <stdlib.h>
#include <string.h>

#define OC_CHANNELS 8
#define OC_MAX_TAPS 320
#define OC_PI 3.14159265358979323846
#define OC_MIX_GAIN 0.25f
#define OC_SAMPLE_BOUND 0.8912509f /* at most -1 dBFS after float rounding */

typedef struct {
    OpenCueAEffect effect;
    float coefficients[OC_CHANNELS][2][OC_MAX_TAPS];
    float history[OC_CHANNELS][2 * OC_MAX_TAPS];
    float lfeState;
    float lfeCoefficient;
    float peakGain;
    float peakRelease;
    int cursor;
    int taps;
    int sampleRate;
} Spatial;

static Spatial *state(OpenCueAEffect *effect) {
    return effect ? (Spatial *)effect->object : NULL;
}

static void reset(Spatial *s) {
    memset(s->history, 0, sizeof(s->history));
    s->lfeState = 0.0f;
    s->peakGain = 1.0f;
    s->cursor = 0;
}

static double sinc(double x) {
    return fabs(x) < 1e-12 ? 1.0 : sin(OC_PI * x) / (OC_PI * x);
}

/* Windowed-sinc coefficient interpolation runs on initialization, never in
 * processReplacing. Scaling preserves impulse area across supported sample rates.
 * For downsampling, the interpolation kernel also applies the Nyquist cutoff.
 */
static float resample(const int16_t *data, double position, double ratio) {
    const double cutoff = ratio < 1.0 ? ratio : 1.0;
    const int radius = 16;
    const int center = (int)floor(position);
    double total = 0.0;
    for (int k = center-radius+1; k <= center+radius; ++k) {
        const double distance = position-k;
        if (k < 0 || k >= 128 || fabs(distance) >= radius) continue;
        const double window = 0.5 + 0.5*cos(OC_PI*distance/radius);
        total += (data[k]/32768.0) * cutoff*sinc(distance*cutoff)*window;
    }
    return (float)(total/ratio);
}

static void prepare(Spatial *s, int sampleRate) {
    /* Index order is Windows standard 7.1: FL FR FC LFE BL BR SL SR.
     * Archive azimuth is clockwise from front. Negative angles swap ears.
     */
    static const int direction[OC_CHANNELS] = {1,1,0,-1,3,3,2,2};
    static const int mirror[OC_CHANNELS] = {1,0,0,0,1,0,1,0};
    const double ratio = sampleRate/44100.0;
    s->sampleRate = sampleRate;
    s->taps = (int)ceil(128.0*ratio);
    if (s->taps > OC_MAX_TAPS) s->taps = OC_MAX_TAPS;
    memset(s->coefficients, 0, sizeof(s->coefficients));
    for (int channel=0; channel<OC_CHANNELS; ++channel) {
        if (direction[channel] < 0) continue;
        for (int ear=0; ear<2; ++ear) {
            const int16_t *source = oc_kemar_hrir[direction[channel]][ear ^ mirror[channel]];
            for (int tap=0; tap<s->taps; ++tap)
                s->coefficients[channel][ear][tap] = resample(source, tap/ratio, ratio);
        }
    }
    s->lfeCoefficient = (float)(1.0-exp(-2.0*OC_PI*120.0/sampleRate));
    s->peakRelease = (float)exp(-1.0/(0.05*sampleRate));
    reset(s);
}

static float finiteInput(const float *input, int frame) {
    const float value = input ? input[frame] : 0.0f;
    if (!isfinite(value)) return 0.0f;
    /* Digital overrange is bounded before the FIR to avoid arithmetic overflow. */
    return value > 16.0f ? 16.0f : value < -16.0f ? -16.0f : value;
}

static float bounded(float value) {
    if (!isfinite(value)) return 0.0f;
    return value > OC_SAMPLE_BOUND ? OC_SAMPLE_BOUND
        : value < -OC_SAMPLE_BOUND ? -OC_SAMPLE_BOUND : value;
}

static void processBlock(OpenCueAEffect *effect, float **input, float **output,
                         int32_t frames, int accumulate) {
    Spatial *s = state(effect);
    if (!s || !output || !output[0] || !output[1] || frames <= 0) return;
    for (int32_t frame=0; frame<frames; ++frame) {
        /* Read all inputs before writing either output, for in-place APO buffers. */
        float current[OC_CHANNELS];
        for (int channel=0; channel<OC_CHANNELS; ++channel) {
            current[channel] = finiteInput(input ? input[channel] : NULL, frame);
            s->history[channel][s->cursor] = current[channel];
            s->history[channel][s->cursor+OC_MAX_TAPS] = current[channel];
        }
        s->lfeState += s->lfeCoefficient*(current[3]-s->lfeState);
        if (fabsf(s->lfeState) < 1e-25f) s->lfeState = 0.0f;
        float result[2] = {0.5f*s->lfeState, 0.5f*s->lfeState};
        for (int channel=0; channel<OC_CHANNELS; ++channel) {
            if (channel == 3) continue;
            const float *past = s->history[channel]+s->cursor+OC_MAX_TAPS;
            for (int ear=0; ear<2; ++ear) {
                const float *filter = s->coefficients[channel][ear];
                float sum0=0.0f, sum1=0.0f, sum2=0.0f, sum3=0.0f;
                int tap=0;
                for (; tap+3<s->taps; tap+=4) {
                    sum0 += filter[tap]*past[-tap];
                    sum1 += filter[tap+1]*past[-tap-1];
                    sum2 += filter[tap+2]*past[-tap-2];
                    sum3 += filter[tap+3]*past[-tap-3];
                }
                float sum = (sum0+sum1)+(sum2+sum3);
                for (; tap<s->taps; ++tap) sum += filter[tap]*past[-tap];
                result[ear] += sum;
            }
        }
        float mixed[2] = {OC_MIX_GAIN*result[0], OC_MIX_GAIN*result[1]};
        if (accumulate) {
            mixed[0] += finiteInput(output[0],frame);
            mixed[1] += finiteInput(output[1],frame);
        }
        /* Link the sample guard across ears so a peak does not shift direction. */
        const float peak = fmaxf(fabsf(mixed[0]),fabsf(mixed[1]));
        const float needed = peak > OC_SAMPLE_BOUND ? OC_SAMPLE_BOUND/peak : 1.0f;
        if (needed < s->peakGain) s->peakGain = needed;
        else s->peakGain = fminf(needed,s->peakRelease*s->peakGain+(1.0f-s->peakRelease));
        output[0][frame] = bounded(mixed[0]*s->peakGain);
        output[1][frame] = bounded(mixed[1]*s->peakGain);
        s->cursor = (s->cursor+1) % OC_MAX_TAPS;
    }
}

static void replacing(OpenCueAEffect *effect, float **input, float **output, int32_t frames) {
    processBlock(effect, input, output, frames, 0);
}

static void accumulating(OpenCueAEffect *effect, float **input, float **output, int32_t frames) {
    processBlock(effect, input, output, frames, 1);
}

static void setParameter(OpenCueAEffect *effect, int32_t index, float value) {
    (void)effect; (void)index; (void)value;
}

static float getParameter(OpenCueAEffect *effect, int32_t index) {
    (void)effect; (void)index; return 0.0f;
}

static void text(void *destination, const char *source, size_t capacity) {
    if (!destination || !capacity) return;
    const size_t length = strlen(source);
    const size_t copy = length < capacity-1 ? length : capacity-1;
    memcpy(destination, source, copy);
    ((char *)destination)[copy] = 0;
}

static intptr_t dispatcher(OpenCueAEffect *effect, int32_t opcode, int32_t index,
                           intptr_t value, void *ptr, float opt) {
    Spatial *s = state(effect);
    if (!s) return 0;
    (void)index;
    switch (opcode) {
    case ocEffOpen: return 1;
    case ocEffClose: free(s); return 1;
    case ocEffSetProgram: return value == 0;
    case ocEffGetProgram: return 0;
    case ocEffGetProgramName:
    case ocEffGetProgramNameIndexed: text(ptr,"KEMAR 7.1",24); return 1;
    case ocEffSetSampleRate:
        if (isfinite(opt) && opt >= 32000.0f && opt <= 96000.0f) {
            prepare(s,(int)opt); return 1;
        }
        return 0;
    case ocEffSetBlockSize: return value > 0;
    case ocEffMainsChanged: reset(s); return 1;
    case ocEffGetPlugCategory: return 1; // effect
    case ocEffGetEffectName: text(ptr,"OpenCue Spatial",32); return 1;
    case ocEffGetVendorString: text(ptr,"OpenCue",64); return 1;
    case ocEffGetProductString: text(ptr,"OpenCue Spatial",64); return 1;
    case ocEffGetVendorVersion: return 1000;
    case ocEffGetVstVersion: return 2400;
    case ocEffCanDo: return 0;
    case ocEffStartProcess:
    case ocEffStopProcess: return 1;
    default: return 0;
    }
}

OC_EXPORT OpenCueAEffect *VSTPluginMain(OpenCueAudioMaster host) {
    if (!host || !host(NULL,ocAudioMasterVersion,0,0,NULL,0.0f)) return NULL;
    Spatial *s = calloc(1,sizeof(*s));
    if (!s) return NULL;
    s->effect.magic = ocEffectMagic;
    s->effect.dispatcher = dispatcher;
    s->effect.process = accumulating;
    s->effect.setParameter = setParameter;
    s->effect.getParameter = getParameter;
    s->effect.numPrograms = 1;
    s->effect.numParams = 0;
    s->effect.numInputs = 8;
    s->effect.numOutputs = 2;
    s->effect.flags = ocEffFlagsCanReplacing;
    s->effect.object = s;
    s->effect.ioRatio = 1.0f;
    s->effect.uniqueID = 0x4f435350; // OCSP
    s->effect.version = 1000;
    s->effect.processReplacing = replacing;
    prepare(s,48000);
    return &s->effect;
}
