/* Native synthetic tests only. No audio endpoint is opened. GPL-3.0-or-later. */
#include "../Dynamics/opencue_vst2_abi.h"
#include "kemar_hrir.h"
#include <windows.h>
#include <float.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define FRAMES 2048
#define BOUND 0.89125095f
typedef OpenCueAEffect *(*Entry)(OpenCueAudioMaster);
typedef struct { float input[8][FRAMES]; float output[2][FRAMES]; } Buffers;
static Entry entry;
static int assertions;

static void require(int condition,const char *message) {
    ++assertions;
    if (!condition) { fprintf(stderr,"FAIL: %s\n",message); exit(1); }
}

static intptr_t host(OpenCueAEffect *e,int32_t opcode,int32_t index,intptr_t value,void *ptr,float opt) {
    (void)e; (void)index; (void)value; (void)ptr; (void)opt;
    return opcode == ocAudioMasterVersion ? 2400 : 0;
}

static OpenCueAEffect *create(int rate) {
    OpenCueAEffect *e=entry(host);
    require(e && e->magic==ocEffectMagic,"native entry and magic");
    require(e->numInputs==8 && e->numOutputs==2 && e->numParams==0,"actual fixed 8-in/2-out contract");
    require(e->flags & ocEffFlagsCanReplacing,"processReplacing advertised");
    e->user=(void *)(uintptr_t)0x12345678;
    require(e->dispatcher(e,ocEffOpen,0,0,NULL,0)==1,"open");
    require(e->dispatcher(e,ocEffSetSampleRate,0,0,NULL,(float)rate)==1,"supported sample rate");
    e->dispatcher(e,ocEffSetBlockSize,0,FRAMES,NULL,0);
    e->dispatcher(e,ocEffMainsChanged,0,1,NULL,0);
    e->dispatcher(e,ocEffStartProcess,0,0,NULL,0);
    return e;
}

static void closeEffect(OpenCueAEffect *e) {
    require(e->user==(void *)(uintptr_t)0x12345678,"host-owned user field preserved");
    e->dispatcher(e,ocEffStopProcess,0,0,NULL,0);
    e->dispatcher(e,ocEffClose,0,0,NULL,0);
}

static void render(OpenCueAEffect *e,Buffers *b,int offset,int frames,int inPlace) {
    float *input[8],*output[2];
    for (int c=0;c<8;++c) input[c]=b->input[c]+offset;
    for (int ear=0;ear<2;++ear) output[ear]=inPlace?input[ear]:b->output[ear]+offset;
    e->processReplacing(e,input,output,frames);
}

static double energy(const float *data,int frames) {
    double result=0;
    for (int i=0;i<frames;++i) result+=(double)data[i]*data[i];
    return result;
}

static int peakIndex(const float *data,int frames) {
    int best=0;
    for (int i=1;i<frames;++i) if (fabsf(data[i])>fabsf(data[best])) best=i;
    return best;
}

static void measuredImpulseTests(void) {
    static const int direction[8]={1,1,0,-1,3,3,2,2};
    static const int mirror[8]={1,0,0,0,1,0,1,0};
    Buffers *b=calloc(1,sizeof(*b));
    require(b!=NULL,"test buffers allocated");
    for (int c=0;c<8;++c) {
        OpenCueAEffect *e=create(44100);
        memset(b,0,sizeof(*b)); b->input[c][0]=0.1f;
        render(e,b,0,512,0);
        for (int ear=0;ear<2;++ear) {
            require(energy(b->output[ear],512)>0,"each input reaches each ear");
            if (c!=3) {
                for (int i=0;i<128;++i) {
                    const float measured=0.025f*oc_kemar_hrir[direction[c]][ear^mirror[c]][i]/32768.0f;
                    require(fabsf(b->output[ear][i]-measured)<2e-7f,"impulse matches independently supplied measured HRIR");
                }
                require(energy(b->output[ear]+128,384)<1e-18,"finite FIR tail length");
            }
        }
        if (c==1 || c==7 || c==5)
            require(energy(b->output[1],512)>energy(b->output[0],512),"right directions have greater right-ear energy");
        if (c==0 || c==6 || c==4)
            require(energy(b->output[0],512)>energy(b->output[1],512),"left directions have greater left-ear energy");
        if (c==1) require(peakIndex(b->output[1],128)<peakIndex(b->output[0],128),"right source reaches near ear earlier");
        if (c==2 || c==3)
            for (int i=0;i<512;++i) require(fabsf(b->output[0][i]-b->output[1][i])<1e-8f,"center/LFE are centered");
        closeEffect(e);
    }
    free(b);
}

static void continuityAndSafety(int rate) {
    Buffers *whole=calloc(1,sizeof(*whole)),*split=calloc(1,sizeof(*split));
    Buffers *inPlace=calloc(1,sizeof(*inPlace));
    require(whole && split && inPlace,"continuity buffers allocated");
    unsigned seed=1234567;
    for (int c=0;c<8;++c) for (int i=0;i<FRAMES;++i) {
        seed=1664525u*seed+1013904223u;
        whole->input[c][i]=((seed>>9)/8388608.0f-0.5f)*0.02f;
    }
    memcpy(split,whole,sizeof(*whole)); memcpy(inPlace,whole,sizeof(*whole));
    OpenCueAEffect *a=create(rate),*b=create(rate),*c=create(rate);
    render(a,whole,0,FRAMES,0);
    for (int offset=0;offset<FRAMES;) {
        int frames=(offset*17+13)%257+1;
        if (frames>FRAMES-offset) frames=FRAMES-offset;
        render(b,split,offset,frames,0); offset+=frames;
    }
    render(c,inPlace,0,FRAMES,1);
    for (int ear=0;ear<2;++ear) for (int i=0;i<FRAMES;++i) {
        require(fabsf(whole->output[ear][i]-split->output[ear][i])<1e-7f,"FIR state continuous across arbitrary blocks");
        require(fabsf(whole->output[ear][i]-inPlace->input[ear][i])<1e-7f,"in-place rendering equals separate buffers");
    }
    closeEffect(b); closeEffect(c);
    a->dispatcher(a,ocEffMainsChanged,0,0,NULL,0);
    memset(whole,0,sizeof(*whole));
    render(a,whole,0,FRAMES,0);
    require(energy(whole->output[0],FRAMES)==0 && energy(whole->output[1],FRAMES)==0,"reset and silence");
    render(a,whole,0,0,0); render(a,whole,0,-1,0);
    for (int channel=0;channel<8;++channel) for (int i=0;i<FRAMES;++i) {
        switch(i%6) { case 0: whole->input[channel][i]=NAN;break;
        case 1: whole->input[channel][i]=INFINITY;break;
        case 2: whole->input[channel][i]=-INFINITY;break;
        case 3: whole->input[channel][i]=FLT_MAX;break;
        case 4: whole->input[channel][i]=-FLT_MAX;break;
        default: whole->input[channel][i]=16; }
    }
    render(a,whole,0,FRAMES,0);
    for (int ear=0;ear<2;++ear) for (int i=0;i<FRAMES;++i)
        require(isfinite(whole->output[ear][i]) && fabsf(whole->output[ear][i])<=BOUND,"finite and bounded extreme inputs");
    require(a->dispatcher(a,ocEffSetSampleRate,0,0,NULL,NAN)==0,"invalid rate rejected");
    require(a->dispatcher(a,ocEffSetSampleRate,0,0,NULL,192000)==0,"unsupported rate rejected");
    closeEffect(a); free(whole); free(split); free(inPlace);
}

static void performance(void) {
    Buffers *b=calloc(1,sizeof(*b)); require(b!=NULL,"performance buffer");
    for (int c=0;c<8;++c) for (int i=0;i<256;++i) b->input[c][i]=0.001f*sinf(i*0.1f+c);
    OpenCueAEffect *e=create(48000);
    LARGE_INTEGER frequency,start,end;
    QueryPerformanceFrequency(&frequency); QueryPerformanceCounter(&start);
    for (int block=0;block<1000;++block) render(e,b,0,256,0);
    QueryPerformanceCounter(&end);
    const double elapsed=(end.QuadPart-start.QuadPart)/(double)frequency.QuadPart;
    const double duration=256000.0/48000;
    printf("PERF: %.3f s compute for %.3f s of 8-channel 48-kHz audio (%.1f%% of one core, this host).\n",elapsed,duration,100*elapsed/duration);
    require(elapsed<duration,"48-kHz spatial rendering completes faster than real time");
    closeEffect(e); free(b);
}

int main(int argc,char **argv) {
    if (argc!=2) { fprintf(stderr,"Usage: OpenCueSpatialTests.exe path-to-DLL\n");return 2; }
    HMODULE library=LoadLibraryA(argv[1]);
    require(library!=NULL,"load actual built DLL");
    entry=(Entry)(void *)GetProcAddress(library,"VSTPluginMain");
    require(entry!=NULL,"VSTPluginMain exported");
    require(entry(NULL)==NULL,"missing host rejected");
    measuredImpulseTests();
    const int rates[]={32000,44100,48000,88200,96000};
    for (int i=0;i<5;++i) { continuityAndSafety(rates[i]);printf("PASS: continuity, in-place, reset, finite limits at %d Hz.\n",rates[i]); }
    performance(); FreeLibrary(library);
    printf("PASS: %d assertions. No audio device opened.\n",assertions);
    return 0;
}
