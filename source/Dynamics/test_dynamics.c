/*
 * External ABI and signal tests. Loads the actual built library and tests
 * observable behavior, without including or reimplementing its DSP.
 * Copyright (c) 2026 OpenCue contributors.
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
#include "opencue_vst2_abi.h"
#include <float.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifdef _WIN32
#include <windows.h>
#else
#include <dlfcn.h>
#endif

typedef OpenCueAEffect *(*EntryPoint)(OpenCueAudioMaster);
static EntryPoint entry;
static int checks, failures;

#define CHECK(condition, description) do { \
    ++checks; \
    if (!(condition)) { \
        ++failures; \
        fprintf(stderr, "FAIL line %d: %s\n", __LINE__, description); \
    } \
} while (0)

static intptr_t host(OpenCueAEffect *effect, int32_t opcode, int32_t index,
                      intptr_t value, void *ptr, float opt) {
    (void)effect; (void)index; (void)value; (void)ptr; (void)opt;
    return opcode == ocAudioMasterVersion ? 2400 : 0;
}

static OpenCueAEffect *make(float rate, double threshold, double ratio,
                           double ceiling) {
    OpenCueAEffect *e = entry(host);
    if (!e) { fprintf(stderr, "Cannot instantiate effect\n"); exit(2); }
    /* Equalizer APO owns user; this must not destroy the plug-in's state. */
    e->user = (void *)(uintptr_t)0x12345678;
    e->dispatcher(e, ocEffOpen, 0, 0, NULL, 0.0f);
    e->dispatcher(e, ocEffSetSampleRate, 0, 0, NULL, rate);
    e->dispatcher(e, ocEffSetBlockSize, 0, 512, NULL, 0.0f);
    e->setParameter(e, 0, (float)((threshold + 30.0) / 30.0));
    e->setParameter(e, 1, (float)((ratio - 1.0) / 7.0));
    e->setParameter(e, 2, (float)((ceiling + 12.0) / 11.5));
    e->dispatcher(e, ocEffMainsChanged, 0, 1, NULL, 0.0f);
    return e;
}

static void closeEffect(OpenCueAEffect *e) {
    CHECK(e->user == (void *)(uintptr_t)0x12345678,
          "host-owned user field remains unchanged");
    e->dispatcher(e, ocEffMainsChanged, 0, 0, NULL, 0.0f);
    e->dispatcher(e, ocEffClose, 0, 0, NULL, 0.0f);
}

static void process(OpenCueAEffect *e, float *l, float *r, float *ol,
                     float *oright, int count, int block) {
    for (int offset = 0; offset < count;) {
        int size = count - offset;
        if (size > block) size = block;
        float *inputs[] = {l + offset, r + offset};
        float *outputs[] = {ol + offset, oright + offset};
        e->processReplacing(e, inputs, outputs, size);
        offset += size;
    }
}

static double rms(const float *data, int from, int to) {
    double sum = 0.0;
    for (int i = from; i < to; ++i) sum += (double)data[i] * data[i];
    return sqrt(sum / (to - from));
}

static void fillTone(float *l, float *r, int count, float rate,
                      double amplitude, double balance) {
    for (int i = 0; i < count; ++i) {
        l[i] = (float)(amplitude * sin(6.2831853071795864769 * 440.0 * i / rate));
        r[i] = (float)(l[i] * balance);
    }
}

static void testAbiAndParameters(void) {
    OpenCueAEffect *e = make(48000.0f, -18.0, 3.0, -1.0);
    CHECK(e->magic == ocEffectMagic, "VST effect magic");
    CHECK(e->numInputs == 2 && e->numOutputs == 2, "stereo input and output");
    CHECK(e->numParams == 14 && e->initialDelay == 0, "fourteen controls and zero reported delay");
    CHECK((e->flags & ocEffFlagsCanReplacing) != 0, "replacing process advertised");
    CHECK(e->object != NULL && e->object != e->user, "state resides in object");
    const char *names[] = {"Threshold", "Ratio", "Ceiling"};
    for (int i = 0; i < 3; ++i) {
        char name[64] = {0};
        e->dispatcher(e, ocEffGetParamName, i, 0, name, 0.0f);
        CHECK(strcmp(name, names[i]) == 0, "exact APO parameter key");
        e->setParameter(e, i, -2.0f);
        CHECK(e->getParameter(e, i) == 0.0f, "normalized lower clamp");
        e->setParameter(e, i, 3.0f);
        CHECK(e->getParameter(e, i) == 1.0f, "normalized upper clamp");
        e->setParameter(e, i, NAN);
        CHECK(e->getParameter(e, i) == 1.0f, "NaN parameter rejected");
        e->setParameter(e, i, INFINITY);
        CHECK(e->getParameter(e, i) == 1.0f, "infinite parameter rejected");
    }
    CHECK(e->dispatcher(e, ocEffSetSampleRate, 0, 0, NULL, NAN) == 0,
          "invalid sample rate rejected");
    closeEffect(e);
}

static void testAtRate(float rate) {
    const int count = (int)rate;
    float *memory = calloc((size_t)count * 8, sizeof(float));
    if (!memory) exit(2);
    float *l = memory, *r = l + count, *ol = r + count, *oright = ol + count;
    float *refL = oright + count, *refR = refL + count;
    float *partL = refR + count, *partR = partL + count;

    OpenCueAEffect *e = make(rate, -24.0, 6.0, -1.0);
    process(e, l, r, ol, oright, count, 127);
    int silent = 1;
    for (int i = 0; i < count; ++i)
        if (ol[i] != 0.0f || oright[i] != 0.0f || !isfinite(ol[i]) || !isfinite(oright[i])) silent = 0;
    CHECK(silent, "one second of silence stays finite and silent");

    fillTone(l, r, count, rate, 0.8, 0.25);
    e->dispatcher(e, ocEffMainsChanged, 0, 1, NULL, 0.0f);
    process(e, l, r, ol, oright, count, 256);
    const double compressed = rms(ol, count / 2, count);
    const double original = rms(l, count / 2, count);
    CHECK(compressed < original * 0.3, "compressor measurably lowers loud steady signal");
    int linked = 1;
    for (int i = 0; i < count; ++i)
        if (fabs(oright[i] - 0.25 * ol[i]) > 1e-6) linked = 0;
    CHECK(linked, "compression preserves interchannel amplitude ratio");

    /* Change the live ratio to unity; a non-limiting tone must pass through. */
    e->setParameter(e, 1, 0.0f);
    process(e, l, r, refL, refR, count, 512);
    CHECK(rms(refL, count / 2, count) > compressed * 2.0,
          "live ratio change alters the actual signal");
    CHECK(fabs(rms(refL, count / 2, count) - original) < original * 0.001,
          "unity ratio passes signal below ceiling without makeup");
    closeEffect(e);

    /* Separate fresh instances establish the threshold is effective. */
    e = make(rate, -6.0, 6.0, -0.5);
    process(e, l, r, refL, refR, count, 512);
    CHECK(rms(refL, count / 2, count) > compressed * 2.0,
          "higher threshold produces less compression");
    closeEffect(e);

    /* Actual DLL must be independent of host block partitioning. */
    e = make(rate, -18.0, 4.0, -3.0);
    process(e, l, r, refL, refR, count, count);
    closeEffect(e);
    e = make(rate, -18.0, 4.0, -3.0);
    process(e, l, r, partL, partR, count, 37);
    CHECK(memcmp(refL, partL, (size_t)count * sizeof(float)) == 0 &&
          memcmp(refR, partR, (size_t)count * sizeof(float)) == 0,
          "continuous state across arbitrary audio blocks");
    closeEffect(e);

    /* Adversarial mixed transients, invalid input and float-range samples. */
    e = make(rate, 0.0, 1.0, -9.0);
    const float pattern[] = {0.0f, 4.0f, -4.0f, FLT_MAX, -FLT_MAX,
                             NAN, INFINITY, -INFINITY, 0.2f, -0.2f};
    for (int i = 0; i < count; ++i) {
        l[i] = pattern[i % 10];
        r[i] = pattern[(i + 3) % 10];
    }
    process(e, l, r, ol, oright, count, 131);
    const double actualCeiling = -12.0 + 11.5 * e->getParameter(e, 2);
    const double bound = pow(10.0, actualCeiling / 20.0);
    int bounded = 1;
    for (int i = 0; i < count; ++i)
        if (!isfinite(ol[i]) || !isfinite(oright[i]) ||
            fabs(ol[i]) > bound || fabs(oright[i]) > bound) bounded = 0;
    CHECK(bounded, "finite output obeys sample ceiling for extreme and invalid inputs");
    closeEffect(e);

    /* Linked limiter rather than independent per-channel clipping. */
    e = make(rate, 0.0, 1.0, -6.0);
    for (int i = 0; i < count; ++i) { l[i] = 4.0f; r[i] = 1.0f; }
    process(e, l, r, ol, oright, count, 257);
    linked = 1;
    for (int i = 0; i < count; ++i)
        if (fabs(oright[i] - 0.25 * ol[i]) > 1e-6) linked = 0;
    CHECK(linked && ol[0] > 0.0f && ol[0] < 0.51f,
          "first-sample limiter preserves stereo ratio and catches transient");
    closeEffect(e);

    /* In-place VST buffers are allowed and must match separate buffers. */
    fillTone(l, r, count, rate, 0.7, -0.5);
    e = make(rate, -20.0, 3.0, -2.0);
    process(e, l, r, refL, refR, count, 64);
    closeEffect(e);
    e = make(rate, -20.0, 3.0, -2.0);
    process(e, l, r, l, r, count, 64);
    CHECK(memcmp(refL, l, (size_t)count * sizeof(float)) == 0 &&
          memcmp(refR, r, (size_t)count * sizeof(float)) == 0,
          "in-place buffers preserve exact output");
    closeEffect(e);
    free(memory);
    printf("Verified %.0f Hz\n", rate);
}

static void disableCore(OpenCueAEffect *e) {
    e->setParameter(e, 3, 0.0f);  /* CompOn */
    e->setParameter(e, 13, 0.0f); /* LimitOn */
}

static void testNewParameterContract(void) {
    OpenCueAEffect *e = make(48000.0f, -18.0, 3.0, -1.0);
    const char *names[] = {"CompOn", "Attack", "Release", "Makeup", "Transient",
                           "Ambience", "Floor", "Leveling", "Width", "Balance", "LimitOn"};
    for (int i = 3; i < 14; ++i) {
        char name[64] = {0};
        e->dispatcher(e, ocEffGetParamName, i, 0, name, 0.0f);
        CHECK(strcmp(name, names[i - 3]) == 0, "new exact APO parameter key");
        e->setParameter(e, i, -1.0f);
        CHECK(e->getParameter(e, i) == 0.0f, "new normalized control lower bound");
        e->setParameter(e, i, 2.0f);
        CHECK(e->getParameter(e, i) == 1.0f, "new normalized control upper bound");
        e->setParameter(e, i, NAN);
        CHECK(e->getParameter(e, i) == 1.0f, "new control rejects NaN");
    }
    char display[64] = {0};
    e->setParameter(e, 4, 0.0f);
    e->dispatcher(e, ocEffGetParamDisplay, 4, 0, display, 0.0f);
    CHECK(strcmp(display, "1.0") == 0, "attack range starts at 1 ms");
    e->setParameter(e, 4, 1.0f);
    e->dispatcher(e, ocEffGetParamDisplay, 4, 0, display, 0.0f);
    CHECK(strcmp(display, "50.0") == 0, "attack range ends at 50 ms");
    e->setParameter(e, 5, 0.0f);
    e->dispatcher(e, ocEffGetParamDisplay, 5, 0, display, 0.0f);
    CHECK(strcmp(display, "20.0") == 0, "release range starts at 20 ms");
    e->setParameter(e, 5, 1.0f);
    e->dispatcher(e, ocEffGetParamDisplay, 5, 0, display, 0.0f);
    CHECK(strcmp(display, "500.0") == 0, "release range ends at 500 ms");
    closeEffect(e);
}

static void testExpandedAtRate(float rate) {
    const int second = (int)rate;
    const int count = second * 3;
    float *memory = calloc((size_t)count * 6, sizeof(float));
    if (!memory) exit(2);
    float *l = memory, *r = l + count, *ol = r + count, *oright = ol + count;
    float *refL = oright + count, *refR = refL + count;
    OpenCueAEffect *e = make(rate, -18.0, 3.0, -1.0);
    disableCore(e);
    const float identity[] = {0.0f, -0.0f, 0.1f, -0.1f, 1.0f, -1.0f,
                              4.0f, -4.0f, FLT_MAX, -FLT_MAX, FLT_MIN,
                              -FLT_MIN, 1.40129846e-45f};
    for (int i = 0; i < count; ++i) {
        l[i] = identity[i % 13];
        r[i] = identity[(i + 3) % 13];
    }
    process(e, l, r, ol, oright, count, 79);
    CHECK(memcmp(l, ol, (size_t)count * sizeof(float)) == 0 &&
          memcmp(r, oright, (size_t)count * sizeof(float)) == 0,
          "disabled compressor/limiter with neutral controls is exact finite identity");
    closeEffect(e);

    fillTone(l, r, count, rate, 0.05, 0.5);
    e = make(rate, -18.0, 3.0, -1.0);
    disableCore(e);
    e->setParameter(e, 6, 0.5f); /* 6 dB manual makeup. */
    process(e, l, r, ol, oright, count, 128);
    double gain = rms(ol, second, count) / rms(l, second, count);
    CHECK(gain > 1.99 && gain < 2.0, "makeup provides real 6 dB gain below ceiling");
    CHECK(fabs(rms(oright, second, count) / rms(ol, second, count) - 0.5) < 1e-6,
          "makeup is stereo linked");
    closeEffect(e);

    /* Timing controls must affect a real step response, not just labels. */
    for (int i = 0; i < count; ++i) l[i] = r[i] = 0.5f;
    e = make(rate, -24.0, 8.0, -0.5);
    e->setParameter(e, 13, 0.0f);
    e->setParameter(e, 4, 0.0f);
    process(e, l, r, ol, oright, count, 256);
    closeEffect(e);
    e = make(rate, -24.0, 8.0, -0.5);
    e->setParameter(e, 13, 0.0f);
    e->setParameter(e, 4, 1.0f);
    process(e, l, r, refL, refR, count, 256);
    CHECK(rms(ol, 0, second / 100) < rms(refL, 0, second / 100) * 0.65,
          "1 ms attack reduces the onset sooner than 50 ms");
    closeEffect(e);
    for (int i = second; i < count; ++i) l[i] = r[i] = 0.02f;
    e = make(rate, -24.0, 8.0, -0.5);
    e->setParameter(e, 13, 0.0f);
    e->setParameter(e, 5, 0.0f);
    process(e, l, r, ol, oright, count, 256);
    closeEffect(e);
    e = make(rate, -24.0, 8.0, -0.5);
    e->setParameter(e, 13, 0.0f);
    e->setParameter(e, 5, 1.0f);
    process(e, l, r, refL, refR, count, 256);
    CHECK(rms(ol, second + second / 10, second + second / 5) >
          rms(refL, second + second / 10, second + second / 5) * 2.0,
          "20 ms release recovers sooner than 500 ms after loud sound");
    closeEffect(e);

    /* Quiet sustained background, followed by a short loud onset. */
    fillTone(l, r, count, rate, 0.02, 0.5);
    for (int i = second; i < second + second / 50; ++i) {
        l[i] += (float)(0.5 * sin(6.2831853071795864769 * 1500.0 * (i - second) / rate));
        r[i] = l[i] * 0.5f;
    }
    e = make(rate, -18.0, 3.0, -1.0);
    disableCore(e);
    e->setParameter(e, 7, 1.0f);
    process(e, l, r, ol, oright, count, 512);
    CHECK(rms(ol, second, second + second / 100) <
          rms(l, second, second + second / 100) * 0.8,
          "transient control reduces short onset energy");
    CHECK(rms(ol, second * 5 / 2, count) > rms(l, second * 5 / 2, count) * 0.95,
          "transient attenuation recovers on sustained background");
    closeEffect(e);

    fillTone(l, r, count, rate, 0.001, 0.5);
    e = make(rate, -18.0, 3.0, -1.0);
    disableCore(e);
    e->setParameter(e, 8, 1.0f);
    process(e, l, r, ol, oright, count, 311);
    gain = rms(ol, second, count) / rms(l, second, count);
    CHECK(gain > 0.05 && gain < 0.7, "ambience reduces low sounds without muting them");
    closeEffect(e);
    fillTone(l, r, count, rate, 0.1, 0.5);
    e = make(rate, -18.0, 3.0, -1.0);
    disableCore(e);
    e->setParameter(e, 8, 1.0f);
    process(e, l, r, ol, oright, count, 311);
    CHECK(fabs(rms(ol, second, count) / rms(l, second, count) - 1.0) < 0.001,
          "ambience leaves a sustained sound above Floor unchanged");
    closeEffect(e);

    fillTone(l, r, count, rate, 0.01, 0.5);
    e = make(rate, -18.0, 3.0, -1.0);
    disableCore(e);
    e->setParameter(e, 10, 1.0f);
    process(e, l, r, ol, oright, count, 197);
    gain = rms(ol, second * 5 / 2, count) / rms(l, second * 5 / 2, count);
    CHECK(gain > 2.5 && gain <= pow(10.0, 12.0 / 20.0) + 1e-5,
          "leveler raises quiet sustained signal within 12 dB gain bound");
    for (int i = 0; i < count; ++i) { l[i] = 1e-5f; r[i] = 5e-6f; }
    process(e, l, r, ol, oright, count, 197);
    int gated = 1;
    for (int i = 0; i < count; ++i)
        if (!isfinite(ol[i]) || fabs(ol[i]) > fabs(l[i]) + 1e-10) gated = 0;
    CHECK(gated, "leveler does not amplify below-gate noise even after active boost");
    memset(l, 0, (size_t)count * sizeof(float));
    memset(r, 0, (size_t)count * sizeof(float));
    process(e, l, r, ol, oright, count, 197);
    int silent = 1;
    for (int i = 0; i < count; ++i)
        if (ol[i] != 0.0f || oright[i] != 0.0f || !isfinite(ol[i])) silent = 0;
    CHECK(silent, "leveler silence gate remains finite and silent");
    closeEffect(e);

    fillTone(l, r, count, rate, 0.1, -1.0);
    e = make(rate, -18.0, 3.0, -1.0);
    disableCore(e);
    e->setParameter(e, 11, 0.0f);
    process(e, l, r, ol, oright, count, 512);
    CHECK(rms(ol, 0, count) == 0.0 && rms(oright, 0, count) == 0.0,
          "zero width cancels anti-phase side content to mono");
    e->setParameter(e, 11, 1.0f);
    process(e, l, r, ol, oright, count, 512);
    CHECK(fabs(rms(ol, 0, count) / rms(l, 0, count) - 2.0) < 1e-6,
          "full width doubles side content");
    closeEffect(e);
    for (int i = 0; i < count; ++i) l[i] = r[i] = 0.2f;
    e = make(rate, -18.0, 3.0, -1.0);
    disableCore(e);
    e->setParameter(e, 11, 1.0f);
    process(e, l, r, ol, oright, count, 512);
    CHECK(memcmp(l, ol, (size_t)count * sizeof(float)) == 0,
          "width preserves center content");
    e->setParameter(e, 12, 1.0f);
    process(e, l, r, ol, oright, count, 512);
    CHECK(rms(ol, 0, count) == 0.0 && fabs(rms(oright, 0, count) - 0.2) < 1e-6,
          "right balance attenuates left channel only");
    e->setParameter(e, 12, 0.0f);
    process(e, l, r, ol, oright, count, 512);
    CHECK(rms(oright, 0, count) == 0.0 && fabs(rms(ol, 0, count) - 0.2) < 1e-6,
          "left balance attenuates right channel only");
    closeEffect(e);

    fillTone(l, r, count, rate, 0.8, -0.5);
    for (int i = 0; i < count; i += 997) l[i] = i % 2 ? -FLT_MAX : FLT_MAX;
    e = make(rate, -18.0, 3.0, -9.0);
    e->setParameter(e, 3, 0.0f);
    e->setParameter(e, 6, 1.0f);
    e->setParameter(e, 7, 0.4f);
    e->setParameter(e, 8, 0.4f);
    e->setParameter(e, 10, 1.0f);
    e->setParameter(e, 11, 1.0f);
    process(e, l, r, ol, oright, count, 83);
    const double bound = pow(10.0, (-12.0 + 11.5 * e->getParameter(e, 2)) / 20.0);
    int bounded = 1;
    for (int i = 0; i < count; ++i)
        if (!isfinite(ol[i]) || !isfinite(oright[i]) ||
            fabs(ol[i]) > bound || fabs(oright[i]) > bound) bounded = 0;
    CHECK(bounded, "final limiter bounds all enabled effects, makeup and width boosts");
    closeEffect(e);
    free(memory);
    printf("Verified expanded controls at %.0f Hz\n", rate);
}

static void testLegacyCompatibility(const char *path) {
#ifdef _WIN32
    HMODULE library = LoadLibraryA(path);
    EntryPoint legacy = library ? (EntryPoint)(uintptr_t)GetProcAddress(library, "VSTPluginMain") : NULL;
#else
    void *library = dlopen(path, RTLD_NOW);
    EntryPoint legacy = library ? (EntryPoint)dlsym(library, "VSTPluginMain") : NULL;
#endif
    if (!legacy) { CHECK(0, "load original v1 reference DLL"); return; }
    EntryPoint current = entry;
    const float rates[] = {44100, 48000, 96000};
    for (int k = 0; k < 3; ++k) {
        const int count = (int)rates[k];
        float *memory = calloc((size_t)count * 6, sizeof(float));
        if (!memory) exit(2);
        float *l = memory, *r = l + count, *ol = r + count, *oright = ol + count;
        float *refL = oright + count, *refR = refL + count;
        fillTone(l, r, count, rates[k], 0.9, 0.37);
        for (int i = 0; i < count; i += 1000) { l[i] = 2.0f; r[i] = -0.5f; }
        entry = legacy;
        OpenCueAEffect *e = make(rates[k], -18.0, 3.0, -1.0);
        process(e, l, r, refL, refR, count, 127);
        closeEffect(e);
        entry = current;
        e = make(rates[k], -18.0, 3.0, -1.0);
        process(e, l, r, ol, oright, count, 127);
        closeEffect(e);
        double error = 0;
        for (int i = 0; i < count; ++i)
            error = fmax(error, fmax(fabs(ol[i] - refL[i]), fabs(oright[i] - refR[i])));
        CHECK(error < 1e-6, "new neutral defaults preserve original v1 signal behavior");
        free(memory);
    }
    entry = current;
#ifdef _WIN32
    FreeLibrary(library);
#else
    dlclose(library);
#endif
}

int main(int argc, char **argv) {
    if (argc < 2 || argc > 3) { fprintf(stderr, "Usage: OpenCueDynamicsTests <plugin-path> [original-v1-path]\n"); return 2; }
#ifdef _WIN32
    HMODULE library = LoadLibraryA(argv[1]);
    if (!library) { fprintf(stderr, "LoadLibrary failed: %lu\n", GetLastError()); return 2; }
    entry = (EntryPoint)(uintptr_t)GetProcAddress(library, "VSTPluginMain");
#else
    void *library = dlopen(argv[1], RTLD_NOW);
    if (!library) { fprintf(stderr, "%s\n", dlerror()); return 2; }
    entry = (EntryPoint)dlsym(library, "VSTPluginMain");
#endif
    if (!entry) { fprintf(stderr, "Missing VSTPluginMain export\n"); return 2; }
    testAbiAndParameters();
    testAtRate(44100.0f);
    testAtRate(48000.0f);
    testAtRate(96000.0f);
    testNewParameterContract();
    testExpandedAtRate(44100.0f);
    testExpandedAtRate(48000.0f);
    testExpandedAtRate(96000.0f);
    if (argc == 3) testLegacyCompatibility(argv[2]);
    printf("%d checks, %d failures\n", checks, failures);
#ifdef _WIN32
    FreeLibrary(library);
#else
    dlclose(library);
#endif
    return failures ? 1 : 0;
}
