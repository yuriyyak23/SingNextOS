/* Opt-in QEMU-only scratch diagnostic. Not a driver or a production boot gate. */
#include "UefiLab.h"
#ifdef CXL_NATIVE_CAPSULE
#include "NativePreparationOwner.h"
#endif
int NativeAcpiGeometry(U64 *,U64 *,U64 *,U64 *);
typedef STATUS (__cdecl *STALL)(U64);
#ifdef CXL_NATIVE_CAPSULE
int NativeProvisionCapsule(void *,SYSTEM *,U64);
STATUS NativeCapsuleVerify(void *,SYSTEM *);
static U64 mappedBase;
U64 NativeMappedBase(void){return mappedBase;}
#ifdef CXL_RAM_COPY
static U64 ramHdm[3];
static int ramSourceRetired;
int NativeRetireForRam(int);
int NativeRamMode(void *,SYSTEM *);
#endif
#endif
static U32 read32(U64 p){return *(volatile U32 *)p;}
static void write32(U64 p,U32 v){*(volatile U32 *)p=v;}
static U64 pair(U64 p){return read32(p)|((U64)read32(p+4)<<32);}
static void hex(TEXT *out,U64 v){U16 s[17];int i;s[16]=0;for(i=15;i>=0;i--){s[i]=L"0123456789abcdef"[v&15];v>>=4;}out->OutputString(out,s);}
static int capability(U64 base,U64 *hdm)
{
    U64 cache;U32 header,count,i;int found=0;
    if(!base||(base&0xffff)||base>0xffffffffffffffffULL-65536)return 0;
    cache=base+4096;header=read32(cache);count=header>>24;
    if((header&65535)!=1||!count||count>32)return 0;
    for(i=1;i<=count;i++){U32 cap=read32(cache+i*4),offset=cap>>20;
        if((cap&65535)==5){if(found++||offset<4*(count+1)||offset>0xefd0||(offset&3))return 0;*hdm=cache+offset;}}
    return found==1;
}
static int committed(U64 h,U64 base)
{
    U32 c=read32(h+0x20);
    return (c&0xfff)==0x600&&(read32(h+4)&2)&&pair(h+0x10)==base&&pair(h+0x18)==0x10000000ULL&&pair(h+0x24)==0;
}
static int retire(U64 *h)
{
    int i,ok=1;for(i=2;i>=0;i--){write32(h[i]+0x20,read32(h[i]+0x20)&~0xe00U);if(read32(h[i]+0x20)&0x600)ok=0;}return ok;
}
#if defined(CXL_NATIVE_CAPSULE) && defined(CXL_RAM_COPY)
int NativeRetireForRam(int partialFailure)
{
    /* Explicit lab fault: host route removed, endpoint/port retirement incomplete.
       Final compensation may remove the local route, but cannot authorize RAM reuse. */
    if(partialFailure){write32(ramHdm[2]+0x20,read32(ramHdm[2]+0x20)&~0xe00U);mappedBase=0;
#ifdef CXL_LIFECYCLE_MAPPING
        ((SYSTEM *)NativePreparationCurrent()->System)->ConOut->OutputString(((SYSTEM *)NativePreparationCurrent()->System)->ConOut,L"CXL_LIFECYCLE_PARTIAL_RETIRE_INJECTED\r\n");
#endif
        NativePreparationRetired(NativePreparationCurrent(),0);return 0;}
    if(!NativePreparationRetired(NativePreparationCurrent(),retire(ramHdm)))return 0;
    ramSourceRetired=1;mappedBase=0;return 1;
}
#endif
static int map(U64 *h,U64 base,int partialCommit)
{
    int i;for(i=0;i<3;i++){
        write32(h[i]+0x10,(U32)base);write32(h[i]+0x14,(U32)(base>>32));
        write32(h[i]+0x18,0x10000000);write32(h[i]+0x1c,0);
        write32(h[i]+0x24,0);write32(h[i]+0x28,0);write32(h[i]+4,2);write32(h[i]+0x20,0x1000);
    }
    for(i=0;i<3;i++){write32(h[i]+0x20,0x1200);if(!committed(h[i],base))return 0;if(partialCommit&&i==0)return 2;}return 1;
}
int NativeHdmExercise(void *image,SYSTEM *system,U64 endpointBase,U64 portBase)
{
    U64 base,bytes,host,hostBytes,h[3]={0},address;U32 i;int touched=0,ok=0,ack=0,partialCommit=0,mapResult;
    const U64 original=0x619b32d4e7a058cfULL,attempt=0xbfa0314965d2e807ULL;
    TEXT *out=system->ConOut;
    (void)image;
    out->OutputString(out,L"CXL_NATIVE_HDM_LAB_ENTER POLICY_QUALIFIED=0\r\n");
#if defined(CXL_NATIVE_CAPSULE) && defined(CXL_RAM_COPY)
    {int selected=NativeRamMode(image,system);if(selected<0)goto done;partialCommit=selected==6;}
#endif
    if(!NativeAcpiGeometry(&base,&bytes,&host,&hostBytes)||bytes!=0x100000000ULL||hostBytes<65536||
       base>0xffffffffffffffffULL-bytes||!capability(endpointBase,&h[0])||!capability(portBase,&h[1])||!capability(host,&h[2]))goto done;
    for(i=0;i<3;i++)if(read32(h[i]+0x20)&0xf00)goto done;
    address=base+0x100000;
    out->OutputString(out,L"CXL_NATIVE_HDM_REGS EP=");hex(out,h[0]);out->OutputString(out,L" RP=");hex(out,h[1]);out->OutputString(out,L" HB=");hex(out,h[2]);out->OutputString(out,L"\r\n");
#ifdef CXL_NATIVE_CAPSULE
    if(!NativePreparationMapBegin(NativePreparationCurrent(),base,h))goto done;
#endif
    touched=1;mapResult=map(h,base,partialCommit);if(mapResult!=1){
        if(mapResult==2)out->OutputString(out,L"CXL_NATIVE_HDM_PARTIAL_COMMIT_INJECTED COMMITTED_DECODERS=1\r\n");
        goto done;
    }
#ifdef CXL_NATIVE_CAPSULE
    if(!NativePreparationMapped(NativePreparationCurrent()))goto done;
#endif
    if(*(volatile U64 *)address!=0)goto done;
    *(volatile U64 *)address=original;if(*(volatile U64 *)address!=original)goto done;
#ifdef CXL_LIFECYCLE_MAPPING
    if(!NativePreparationRouteVerified(NativePreparationCurrent()))goto done;
#endif
#ifdef CXL_NATIVE_CAPSULE
    mappedBase=base;
#ifdef CXL_RAM_COPY
    ramSourceRetired=0;for(i=0;i<3;i++)ramHdm[i]=h[i];
#endif
    if(!NativeProvisionCapsule(image,system,base)||NativeCapsuleVerify(image,system))goto done;
    out->OutputString(out,L"CXL_NATIVE_CAPSULE_AUTHENTICATED\r\n");
#ifdef CXL_RAM_COPY
    if(ramSourceRetired){ok=1;goto done;}
#endif
#endif
    out->OutputString(out,L"CXL_NATIVE_HDM_MAPPED HPA=");hex(out,address);out->OutputString(out,L" VALUE=");hex(out,original);out->OutputString(out,L"\r\n");
    if(system->ConIn)for(i=0;i<30000;i++){INPUT_KEY key;if(!system->ConIn->ReadKeyStroke(system->ConIn,&key)&&key.Character=='c'){ack=1;break;}((STALL)system->BootServices->Functions[28])(1000);}
    if(!ack){out->OutputString(out,L"CXL_NATIVE_HDM_OBSERVER_TIMEOUT\r\n");goto done;}
    if(!retire(h))goto done;
    *(volatile U64 *)address=attempt;
    if(!map(h,base,0)||*(volatile U64 *)address!=original)goto done;
    ok=1;
done:
#ifdef CXL_LIFECYCLE_MAPPING
    /* The original RAM retirement already completed these decoder writes.
       Preserve its receipt/pins; do not dispatch a second retirement effect. */
    if(touched&&!ramSourceRetired){int localRetired=retire(h);if(!localRetired)ok=0;
#else
    if(touched){int localRetired=retire(h);if(!localRetired)ok=0;
#endif
#ifdef CXL_NATIVE_CAPSULE
        /* Compensation cannot clear a prior quarantine. */
        NativePreparationRetired(NativePreparationCurrent(),localRetired);
#endif
    }
#ifdef CXL_NATIVE_CAPSULE
    mappedBase=0;
#endif
#if defined(CXL_NATIVE_CAPSULE) && defined(CXL_RAM_COPY)
    if(ok&&ramSourceRetired){out->OutputString(out,L"CXL_NATIVE_RAM_PIPELINE_PASS FINAL_RETIRED=1\r\n");return 1;}
#endif
    out->OutputString(out,ok?L"CXL_NATIVE_HDM_PASS WRITE_BLOCKED=1 FINAL_RETIRED=1\r\n":L"CXL_NATIVE_HDM_FAILED\r\n");return ok;
}
