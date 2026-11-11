/* Test preparation only. Never executes the payload or exits boot services. */
#include "UefiLab.h"
#include "bearssl.h"
#include <string.h>
#include "expected.h"
#include "trust.h"
#ifdef CXL_NATIVE_CAPSULE
#include "NativePreparationOwner.h"
int NativeRetireForRam(int);
#endif
typedef struct {U32 A;U16 B,C;U8 D[8];} GUID;
typedef struct {U32 Revision;void *Parent;SYSTEM *System;void *Device,*Path,*Reserved;U32 OptionsSize;void *Options;} LOADED;
typedef STATUS (__cdecl *HANDLE_PROTOCOL)(void *,GUID *,void **);
typedef STATUS (__cdecl *ALLOCATE_PAGES)(U32,U32,U64,U64 *);
typedef STATUS (__cdecl *FREE_PAGES)(U64,U64);
typedef STATUS (__cdecl *MEMORY_MAP)(U64 *,void *,U64 *,U64 *,U32 *);
typedef STATUS (__cdecl *STALL)(U64);
static U8 mapBuffer[65536];
static U64 read64(const U8 *p){U64 v=0;int i;for(i=7;i>=0;i--)v=(v<<8)|p[i];return v;}
static U32 read32(const U8 *p){return (U32)p[0]|((U32)p[1]<<8)|((U32)p[2]<<16)|((U32)p[3]<<24);}
static void put32(U8 *p,U32 v){int i;for(i=0;i<4;i++){p[i]=(U8)v;v>>=8;}}
static void put64(U8 *p,U64 v){int i;for(i=0;i<8;i++){p[i]=(U8)v;v>>=8;}}
static void clear(U8 *p,U64 n){U64 i;for(i=0;i<n;i++)((volatile U8 *)p)[i]=0;}
static int iszero(const U8 *p,U64 n){U64 i;U8 d=0;for(i=0;i<n;i++)d|=((volatile const U8 *)p)[i];return !d;}
static void hash(const void *p,U64 n,U8 out[48]){br_sha384_context c;br_sha384_init(&c);br_sha384_update(&c,p,n);br_sha384_out(&c,out);}
static U32 update(U32 c,const U8 *p,U64 n){U64 i;int j;for(i=0;i<n;i++){c^=p[i];for(j=0;j<8;j++)c=(c>>1)^((0U-(c&1))&0x82f63b78U);}return c;}
static U32 infoCrc(const U8 *p,U64 n){return ~update(update(0xffffffffU,p,112),p+116,n-116);}
/* The security record describes this verifier's actual development trust decision.
   It is immutable evidence only: it does not grant runtime endpoint or RAM authority. */
static int bootInfo(U8 *p,const U8 *platform,U64 imageGeneration)
{
    U8 actual[48],check[160];clear(p,160);
    if(!imageGeneration||imageGeneration<TRUSTED_IMAGE_GENERATION_FLOOR)return 0;
    put32(p,0x31494248U);p[4]=1;put32(p+8,120);put32(p+12,160);
    memcpy(p+24,platform,16);put32(p+40,1);put32(p+44,1);put32(p+52,1);put64(p+56,1);
    /* BootEvidenceKind.Security, Required | EvidenceOnly, 32-byte V1 payload. */
    p[120]=3;p[122]=3;put32(p+124,40);put32(p+128,2); /* Development, reserved=0 */
    put64(p+136,1);put64(p+144,TRUSTED_IMAGE_GENERATION_FLOOR);put64(p+152,imageGeneration);
    hash(p,160,actual);memcpy(p+64,actual,48);put32(p+112,infoCrc(p,160));
    memcpy(check,p,160);clear(check+64,48);clear(check+112,4);hash(check,160,actual);
    return memcmp(p+64,actual,48)==0&&read32(p+112)==infoCrc(p,160);
}
static int token(const U16 *s,U64 n,const U16 *t)
{
    U64 i=0;while(t[i]){if(i>=n||s[i]!=t[i])return 0;i++;}return i==n;
}
int NativeRamMode(void *image,SYSTEM *system)
{
    GUID id={0x5b1b31a1,0x9562,0x11d2,{0x8e,0x3f,0x00,0xa0,0xc9,0x69,0x72,0x3b}};
    LOADED *loaded=0;U64 i,j,n;const U16 *s;int result=0;
    if(((HANDLE_PROTOCOL)system->BootServices->Functions[16])(image,&id,(void **)&loaded)||!loaded)return -1;
    if(!loaded->OptionsSize)return 0;
    if(!loaded->Options||loaded->OptionsSize>1024||(loaded->OptionsSize&1))return -1;
    s=(const U16 *)loaded->Options;n=loaded->OptionsSize/2;
    for(i=0;i<n&&s[i];i=j){while(i<n&&s[i]==' ')i++;j=i;while(j<n&&s[j]&&s[j]!=' ')j++;
        if(token(s+i,j-i,L"--ram-copy"))result=1;
        if(token(s+i,j-i,L"--ram-copy-fail-alloc"))result=2;
        if(token(s+i,j-i,L"--ram-copy-fail-copy"))result=3;
        if(token(s+i,j-i,L"--ram-copy-fail-info"))result=4;
        if(token(s+i,j-i,L"--ram-copy-fail-retire"))result=5;
        if(token(s+i,j-i,L"--ram-copy-fail-commit"))result=6;
        if(token(s+i,j-i,L"--ram-copy-fail-clear"))result=7;
#ifdef CXL_LIFECYCLE_MAPPING
        if(token(s+i,j-i,L"--ram-copy-hold-observer"))result=8;
#endif
        if(j==i)break;
    }return result;
}
static void hex(TEXT *out,U64 v){U16 text[17];int i;for(i=15;i>=0;i--){text[i]=L"0123456789abcdef"[v&15];v>>=4;}text[16]=0;out->OutputString(out,text);}
static int loaderData(SYSTEM *system,U64 address,U64 pages)
{
    U64 size=sizeof(mapBuffer),key=0,stride=0,i;U32 version=0;
    if(((MEMORY_MAP)system->BootServices->Functions[4])(&size,mapBuffer,&key,&stride,&version)||version!=1||stride<40||stride>256||size>sizeof(mapBuffer)||size%stride)return 0;
    for(i=0;i<size;i+=stride){const U8 *d=mapBuffer+i;U64 start=read64(d+8),count=read64(d+24);
        if(read32(d)==2&&start<=address&&(address-start)%4096==0&&(address-start)/4096<=count&&pages<=count-(address-start)/4096)return 1;
    }return 0;
}
STATUS RamStage(void *image,SYSTEM *system,const U8 *snapshot)
{
    U64 address=0,count=read64(snapshot+4096+216),pages=(count+4095)/4096+1,infoAddress,i;
    U8 *ram,*info,digest[48];TEXT *out=system->ConOut;int selected,success=0,acknowledged=0;STATUS release;
    if(!system->BootServices)return 0x8000000000000002ULL;
    selected=NativeRamMode(image,system);if(!selected)return 0;if(selected<0)return 0x8000000000000002ULL;
    if(selected==2){out->OutputString(out,L"CXL_RAM_ALLOC_FAILURE_INJECTED\r\n");return 0x8000000000000009ULL;}
    if(!count||count>65536||
#ifdef CXL_NATIVE_CAPSULE
       !NativePreparationAllocateRam(NativePreparationCurrent(),snapshot,pages,&address)
#else
       ((ALLOCATE_PAGES)system->BootServices->Functions[2])(0,2,pages,&address)
#endif
       ){
        out->OutputString(out,L"CXL_RAM_ALLOC_FAILED\r\n");return 0x8000000000000009ULL;}
    if((address&4095)||address>0xffffffffffffffffULL-pages*4096||!loaderData(system,address,pages)){
#ifdef CXL_NATIVE_CAPSULE
        NativePreparationQuarantine(NativePreparationCurrent());
#endif
        out->OutputString(out,L"CXL_RAM_QUARANTINED GEOMETRY_UNKNOWN FREE_ATTEMPTS=0\r\n");return 0x8000000000000002ULL;}
#ifdef CXL_NATIVE_CAPSULE
    {
        NATIVE_PREPARATION *owner=NativePreparationCurrent();
        if(!NativePreparationAdmitRam(owner,address,pages)||
           NativePreparationAdmitRam((NATIVE_PREPARATION *)((U8 *)owner+8),address,pages)||
           NativePreparationFreeRam(owner,address+4096,pages)||owner->RamFreeAttempts){
            NativePreparationQuarantine(owner);return 0x800000000000001aULL;
        }
        out->OutputString(out,L"CXL_NATIVE_OWNER_FOREIGN_RAM_DENIED FREE_ATTEMPTS=0\r\n");
    }
#endif
    ram=(U8 *)address;infoAddress=address+(pages-1)*4096;info=(U8 *)infoAddress;clear(ram,pages*4096);
    for(i=0;i<count;i++)((volatile U8 *)ram)[i]=snapshot[8192+i];
    if(selected==3)ram[0]^=1;
    hash(ram,count,digest);
    if(memcmp(digest,snapshot+4096+224,48))out->OutputString(out,L"CXL_RAM_COPY_HASH_REJECTED\r\n");
    else if(!bootInfo(info,snapshot+4096+96,read64(snapshot+4096+88)))out->OutputString(out,L"CXL_RAM_BOOTINFO_FAILED\r\n");
    else{
        if(selected==4)info[112]^=1;
        if(read32(info+112)!=infoCrc(info,160))out->OutputString(out,L"CXL_RAM_BOOTINFO_CRC_REJECTED\r\n");
        else success=1;
    }
#ifdef CXL_NATIVE_CAPSULE
    if(!NativeRetireForRam(selected==5)){
        /* Allocation remains firmware-owned LoaderData. No publication, read,
           clear or FreePages after ambiguous source retirement. Only destruction
           of this exact scratch VM ends these lab pins; no remote closure claim. */
        out->OutputString(out,L"CXL_NATIVE_RAM_SOURCE_RETIRE_FAILED\r\nCXL_RAM_QUARANTINED RETAINED=1 FREE_ATTEMPTS=0 RAM=");hex(out,address);
        out->OutputString(out,L" PAGES=");hex(out,pages);out->OutputString(out,L"\r\n");
        return 0x800000000000001aULL;
    }
    else{
        out->OutputString(out,L"CXL_NATIVE_RAM_SOURCE_RETIRED\r\n");
        if(!NativePreparationAdmitRam(NativePreparationCurrent(),address,pages))return 0x800000000000001aULL;
        if(success){hash(ram,count,digest);if(memcmp(digest,snapshot+4096+224,48)||read32(info+112)!=infoCrc(info,160))success=0;}
    }
#endif
#ifdef CXL_NATIVE_CAPSULE
    if(!NativePreparationAdmitRam(NativePreparationCurrent(),address,pages))return 0x800000000000001aULL;
#endif
    if(!success)clear(ram,pages*4096);
#ifdef CXL_LIFECYCLE_MAPPING
    if(!NativePreparationPublishRam(NativePreparationCurrent(),success))return 0x800000000000001aULL;
#endif
    out->OutputString(out,success?L"CXL_RAM_READY STATE=VALID RAM=":L"CXL_RAM_READY STATE=CLEARED RAM=");hex(out,address);
    out->OutputString(out,L" PAYLOAD=");hex(out,count);out->OutputString(out,L" INFO=");hex(out,infoAddress);out->OutputString(out,L" PAGES=");hex(out,pages);out->OutputString(out,L"\r\n");
    if(system->ConIn)for(i=0;i<
#ifdef CXL_LIFECYCLE_MAPPING
        (selected==8?120000ULL:30000ULL)
#else
        30000ULL
#endif
        ;i++){INPUT_KEY key;if(!system->ConIn->ReadKeyStroke(system->ConIn,&key)&&key.Character=='c'){acknowledged=1;break;}((STALL)system->BootServices->Functions[28])(1000);}
    if(!acknowledged){
#ifdef CXL_NATIVE_CAPSULE
        NativePreparationQuarantine(NativePreparationCurrent());
#endif
        out->OutputString(out,L"CXL_RAM_OBSERVER_TIMEOUT\r\nCXL_RAM_QUARANTINED ACK_MISSING FREE_ATTEMPTS=0\r\n");return 0x800000000000001aULL;}
#ifdef CXL_NATIVE_CAPSULE
    if(!NativePreparationAdmitRam(NativePreparationCurrent(),address,pages))return 0x800000000000001aULL;
#endif
    clear(ram,pages*4096);
    if(selected==7){ram[0]=1;out->OutputString(out,L"CXL_RAM_CLEAR_FAILURE_INJECTED\r\n");}
    if(!iszero(ram,pages*4096)){
#ifdef CXL_NATIVE_CAPSULE
        NativePreparationQuarantine(NativePreparationCurrent());
#endif
        out->OutputString(out,L"CXL_RAM_QUARANTINED CLEAR_FAILED FREE_ATTEMPTS=0\r\n");return 0x800000000000001aULL;}
#ifdef CXL_NATIVE_CAPSULE
    release=NativePreparationFreeRam(NativePreparationCurrent(),address,pages)?0:0x800000000000001aULL;
#else
    release=((FREE_PAGES)system->BootServices->Functions[3])(address,pages);
#endif
    out->OutputString(out,release?L"CXL_RAM_FREE_FAILED\r\n":L"CXL_RAM_RELEASED CLEARED=1\r\n");
    if(!acknowledged)out->OutputString(out,L"CXL_RAM_OBSERVER_TIMEOUT\r\n");
    return success&&!release?0:0x800000000000001aULL;
}
