/* Authorized bounded termination lab. This channel never invents DMA authority. */
#include "NativePreparationOwner.h"
#include "bearssl_hash.h"
#include "bearssl_ec.h"
#include "lifecycle-key.h"
#include <string.h>
unsigned char __inbyte(unsigned short);
void __outbyte(unsigned short,unsigned char);
#pragma intrinsic(__inbyte,__outbyte)
#define UART 0x2f8
typedef STATUS (__cdecl *STALL)(U64);
typedef struct {U32 A;U16 B,C;U8 D[8];} GUID;
typedef STATUS (__cdecl *LOCATE)(U32,GUID *,void *,U64 *,void ***);
typedef STATUS (__cdecl *HANDLE)(void *,GUID *,void **);
typedef STATUS (__cdecl *DISCONNECT)(void *,void *,void *);
typedef STATUS (__cdecl *FREE)(void *);
static U64 sequence;
#ifdef CXL_LIFECYCLE_ISOLATION
static NATIVE_PREPARATION *boundOwner;
static U64 dmarBase,endpointLease,portLease,isolationGeneration;
#ifdef CXL_LIFECYCLE_MAPPING
static U64 mappingOperation,mappingGeneration;
#endif
int NativePlatformDmar(SYSTEM *,U64 *,U64,U64);
#endif
static int detachConsole(SYSTEM *system)
{
    GUID serial={0xbb25cf6f,0xf1d4,0x11d2,{0x9a,0x0c,0x00,0x90,0x27,0x3f,0xc1,0xfd}};
    GUID pathId={0x09576e91,0x6d3f,0x11d2,{0x8e,0x39,0x00,0xa0,0xc9,0x69,0x72,0x3b}};
    void **handles=0,*target=0;U64 count=0,i;int found=0,ok=0;
    if(((LOCATE)system->BootServices->Functions[36])(2,&serial,0,&count,&handles)||!handles||count>16)goto done;
    for(i=0;i<count;i++){
        U8 *path=0;U32 offset=0;
        if(((HANDLE)system->BootServices->Functions[16])(handles[i],&pathId,(void **)&path)||!path)goto done;
        while(offset<512){U32 length=path[offset+2]|((U32)path[offset+3]<<8);
            if(length<4||length>512-offset)goto done;
            if(path[offset]==0x7f)break;
            if(path[offset]==2&&path[offset+1]==1&&length==12){
                U32 hid,uid;memcpy(&hid,path+offset+4,4);memcpy(&uid,path+offset+8,4);
                if(hid==0x050141d0&&uid==1){target=handles[i];found++;}
            }offset+=length;
        }
    }
    if(found!=1)goto done;
    /* Remove TerminalDxe's COM2 children; COM1 ConsoleSplitter remains intact.
       No firmware rebuild or persistent VARS change. Fail closed on unknown path. */
    if(((DISCONNECT)system->BootServices->Functions[31])(target,0,0))goto done;
    ok=1;
done:
    if(handles)((FREE)system->BootServices->Functions[6])(handles);
    return ok;
}
static int byteio(SYSTEM *system,U8 *value,int transmit,U32 *budget)
{
    while(*budget){
        U8 status=__inbyte(UART+5);
        if(status&0x1e)return 0;
        if(status&(transmit?0x20:1)){if(transmit)__outbyte(UART,*value);else *value=__inbyte(UART);return 1;}
        (*budget)--;((STALL)system->BootServices->Functions[28])(100);
    }return 0;
}
int NativeLifecycleOpen(NATIVE_PREPARATION *owner,SYSTEM *system)
{
#ifdef CXL_LIFECYCLE_ISOLATION
    if(boundOwner||!detachConsole(system)||!NativePlatformDmar(system,&dmarBase,owner->PlatformBuffers,owner->PlatformPages))return 0;
    __outbyte(UART+1,0);__outbyte(UART+3,0x80);__outbyte(UART,1);
    __outbyte(UART+1,0);__outbyte(UART+3,3);__outbyte(UART+2,7);__outbyte(UART+4,3);
    boundOwner=owner;
    return NativeLifecycleCheck(owner,1);
#else
    U64 request[8]={0x314c434c4f4e53ULL,1,0,0,0,0,0,0};
    U8 response[160],digest[48];U64 i;U32 budget=50000;const U64 *words=(const U64 *)response;
    br_ec_public_key key;br_sha384_context hash;
    request[2]=++sequence;request[3]=owner->Self;request[4]=owner->Image;
    request[5]=owner->System;request[6]=(sizeof(*owner)+4095)/4096;
    if(!detachConsole(system))goto unavailable;
    __outbyte(UART+1,0);__outbyte(UART+3,0x80);__outbyte(UART,1);
    __outbyte(UART+1,0);__outbyte(UART+3,3);__outbyte(UART+2,7);__outbyte(UART+4,3);
    system->ConOut->OutputString(system->ConOut,L"CXL_LIFECYCLE_REQUEST BEFORE_PCI=1\r\n");
    for(i=0;i<sizeof(request);i++)if(!byteio(system,((U8 *)request)+i,1,&budget))goto unavailable;
    for(i=0;i<sizeof(response);i++)if(!byteio(system,response+i,0,&budget))goto unavailable;
    key.curve=BR_EC_secp384r1;key.q=(U8 *)lifecycle_point;key.qlen=97;
    br_sha384_init(&hash);br_sha384_update(&hash,response,64);br_sha384_out(&hash,digest);
    if(!br_ecdsa_i31_vrfy_raw(&br_ec_prime_i31,digest,48,&key,response+64,96))goto invalid;
    /* Exact request correlation and per-VM pinned key. No counter or pointer is authority. */
    if(words[0]!=request[0]||words[1]!=1||words[2]!=request[2]||words[3]!=request[3]||
       words[4]!=request[4]||words[5]!=request[5]||words[6]!=request[6]||words[7]!=1)goto invalid;
    system->ConOut->OutputString(system->ConOut,L"CXL_LIFECYCLE_PROVENANCE_VERIFIED DENY=ISOLATION_PROVIDER_MISSING\r\n");
    return 0;
invalid:
    system->ConOut->OutputString(system->ConOut,L"CXL_LIFECYCLE_INVALID_RESPONSE DENIED=1\r\n");return 0;
unavailable:
    system->ConOut->OutputString(system->ConOut,L"CXL_LIFECYCLE_UNAVAILABLE DENIED=1\r\n");return 0;
#endif
}
#ifdef CXL_LIFECYCLE_ISOLATION
int NativeLifecycleCheck(NATIVE_PREPARATION *owner,U64 operation)
{
    U64 request[16]={0x324c434c4f4e53ULL,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0};
    U8 response[232],digest[48];const U64 *words=(const U64 *)response;
    U32 budget=300000;U64 i;SYSTEM *system;br_ec_public_key key;br_sha384_context hash;
#ifdef CXL_LIFECYCLE_MAPPING
    request[0]=0x334c434c4f4e53ULL;
    if(owner!=boundOwner||!owner||operation<1||operation>16)return 0;
#else
    if(owner!=boundOwner||!owner||operation<1||operation>4)return 0;
#endif
    system=(SYSTEM *)owner->System;
    request[1]=operation;request[2]=++sequence;request[3]=owner->Self;
    request[4]=owner->Image;request[5]=owner->System;request[6]=(sizeof(*owner)+4095)/4096;
    request[7]=owner->PlatformBuffers;request[8]=owner->PlatformPages;request[9]=dmarBase;
    request[10]=endpointLease;request[11]=portLease;request[12]=isolationGeneration?1:0;
    request[13]=isolationGeneration;
#ifdef CXL_LIFECYCLE_MAPPING
    request[14]=mappingGeneration;request[15]=mappingOperation;
#endif
    for(i=0;i<sizeof(request);i++)if(!byteio(system,((U8 *)request)+i,1,&budget))goto failure;
    for(i=0;i<sizeof(response);i++)if(!byteio(system,response+i,0,&budget))goto failure;
    key.curve=BR_EC_secp384r1;key.q=(U8 *)lifecycle_point;key.qlen=97;
    br_sha384_init(&hash);br_sha384_update(&hash,response,136);br_sha384_out(&hash,digest);
    if(!br_ecdsa_i31_vrfy_raw(&br_ec_prime_i31,digest,48,&key,response+136,96))goto failure;
    for(i=0;i<10;i++)if(words[i]!=request[i])goto failure;
    if(words[16]||words[12]!=1||words[13]!=1)goto failure;
#ifdef CXL_LIFECYCLE_MAPPING
    if(operation==5){if(mappingOperation||words[15]!=1||words[14])goto failure;mappingOperation=words[15];}
    else if(operation==7){if(mappingGeneration||words[14]!=1||words[15]!=mappingOperation)goto failure;mappingGeneration=words[14];}
    else if(words[14]!=mappingGeneration||words[15]!=mappingOperation)goto failure;
#else
    if(words[14]||words[15])goto failure;
#endif
    if(operation==1){endpointLease=words[10];portLease=words[11];isolationGeneration=words[13];
        if(!endpointLease||!portLease||endpointLease==portLease)goto failure;
        system->ConOut->OutputString(system->ConOut,L"CXL_LIFECYCLE_ISOLATION_ADMITTED RESET_POLICY=TERMINATE MAPPING_GENERATION=0\r\n");
    }else if(words[10]!=endpointLease||words[11]!=portLease||words[13]!=isolationGeneration)goto failure;
    if(operation==2&&(owner->EndpointBdf!=endpointLease||owner->PortBdf!=portLease))goto failure;
    return 1;
failure:
    NativePreparationQuarantine(owner);
    system->ConOut->OutputString(system->ConOut,L"CXL_LIFECYCLE_CURRENTNESS_LOST TERMINATE_NO_REUSE=1\r\n");
    if(isolationGeneration){
        /* No return to allocator/Shell after ambiguous admitted operation. */
        typedef struct {HEADER Hdr;void *Functions[14];} RUNTIME;
        typedef void (__cdecl *RESET)(U32,STATUS,U64,void *);
        ((RESET)((RUNTIME *)system->RuntimeServices)->Functions[10])(1,0x800000000000001aULL,0,0);
        for(;;)((STALL)system->BootServices->Functions[28])(1000000);
    }
    return 0;
}
#ifdef CXL_LIFECYCLE_MAPPING
void NativeLifecycleAbort(NATIVE_PREPARATION *owner)
{
    SYSTEM *system;
    if(!owner||owner!=boundOwner||!isolationGeneration)return;
    system=(SYSTEM *)owner->System;
    system->ConOut->OutputString(system->ConOut,L"CXL_LIFECYCLE_QUARANTINE_TERMINATE NO_REUSE=1\r\n");
    {typedef struct {HEADER Hdr;void *Functions[14];} RUNTIME;
     typedef void (__cdecl *RESET)(U32,STATUS,U64,void *);
     ((RESET)((RUNTIME *)system->RuntimeServices)->Functions[10])(1,0x800000000000001aULL,0,0);}
    for(;;)((STALL)system->BootServices->Functions[28])(1000000);
}
#endif
#endif
