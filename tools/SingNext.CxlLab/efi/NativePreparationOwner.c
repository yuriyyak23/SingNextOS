/* Existing CXLHDM.EFI diagnostic owner; never a boot isolation provider. */
#include "NativePreparationOwner.h"
#ifdef CXL_LIFECYCLE_ISOLATION
#include "DmaBufferLayout.h"
#endif
#define PREP_MAGIC 0x31524e574f4c5843ULL
#define PREP_PAGES ((sizeof(NATIVE_PREPARATION)+4095)/4096)
typedef STATUS (__cdecl *ALLOCATE_PAGES)(U32,U32,U64,U64 *);
typedef STATUS (__cdecl *FREE_PAGES)(U64,U64);
static NATIVE_PREPARATION *current;
#ifdef CXL_LIFECYCLE_LAB
int NativeLifecycleOpen(NATIVE_PREPARATION *,SYSTEM *);
#endif
static void zero(U8 *p,U64 n){U64 i;for(i=0;i<n;i++)((volatile U8 *)p)[i]=0;}
static void hex(TEXT *out,U64 v){U16 s[17];int i;s[16]=0;for(i=15;i>=0;i--){s[i]=L"0123456789abcdef"[v&15];v>>=4;}out->OutputString(out,s);}
static int original(NATIVE_PREPARATION *p)
{
    return p&&p==current&&p->Magic==PREP_MAGIC&&p->Self==(U64)p&&!(p->Flags&(PREP_QUARANTINED|PREP_FINISHED));
}
NATIVE_PREPARATION *NativePreparationCurrent(void){return current;}
void NativePreparationQuarantine(NATIVE_PREPARATION *p)
{
    if(p&&p==current&&p->Self==(U64)p){p->Flags|=PREP_QUARANTINED;
#ifdef CXL_LIFECYCLE_MAPPING
        NativeLifecycleAbort(p);
#endif
    }
}
int NativePreparationBegin(void *image,SYSTEM *system)
{
    U64 address=0;
    if(current||!image||!system||!system->BootServices)return 0;
    if(((ALLOCATE_PAGES)system->BootServices->Functions[2])(0,2,PREP_PAGES,&address))return 0;
    if(!address||(address&4095)||address>0xffffffffffffffffULL-PREP_PAGES*4096)return 0;
    current=(NATIVE_PREPARATION *)address;zero((U8 *)current,PREP_PAGES*4096);
    current->Magic=PREP_MAGIC;current->Self=address;current->Image=(U64)image;current->System=(U64)system;
    system->ConOut->OutputString(system->ConOut,L"CXL_NATIVE_OWNER_BEGIN RECORD=");hex(system->ConOut,address);
    system->ConOut->OutputString(system->ConOut,L" PAGES=");hex(system->ConOut,PREP_PAGES);
    system->ConOut->OutputString(system->ConOut,L" RESET_AUTHORITY=0\r\n");
#ifdef CXL_LIFECYCLE_LAB
#ifdef CXL_LIFECYCLE_ISOLATION
    current->PlatformBuffers=0x0fffffffULL;current->PlatformPages=12;current->PlatformAllocations=1;
    if(((ALLOCATE_PAGES)system->BootServices->Functions[2])(1,2,current->PlatformPages,&current->PlatformBuffers)||
       !current->PlatformBuffers||(current->PlatformBuffers&4095)||current->PlatformBuffers>0x10000000ULL-12*4096){
        current->Flags|=PREP_QUARANTINED|PREP_FINISHED;current=0;return 0;
    }
    DmaBufferLayoutInitialize((U8 *)current->PlatformBuffers,current->PlatformPages);
#endif
    if(!NativeLifecycleOpen(current,system)){
        /* Register original obligation before PCI. No provider isolation => no effects. */
        current->Flags|=PREP_QUARANTINED|PREP_FINISHED;
        system->ConOut->OutputString(system->ConOut,L"CXL_LIFECYCLE_ADMISSION_REFUSED PCI=0 MAILBOX=0 MAP=0 RAM_ALLOC=0 FREE=0\r\nCXL_NATIVE_OWNER_PINNED\r\n");
        current=0;return 0;
    }
#endif
    return 1;
}
int NativePreparationDiscovery(NATIVE_PREPARATION *p,void *image,SYSTEM *system,U64 endpointBdf,U64 portBdf,U64 deviceRegisters)
{
    if(!original(p)||p->Flags||p->Image!=(U64)image||p->System!=(U64)system)return 0;
    p->EndpointBdf=endpointBdf;p->PortBdf=portBdf;p->DeviceRegisters=deviceRegisters;
#ifdef CXL_LIFECYCLE_ISOLATION
    if(!NativeLifecycleCheck(p,2))return 0;
#endif
    p->Flags|=PREP_DISCOVERED;return 1;
}
int NativePreparationAdmitMailbox(NATIVE_PREPARATION *p,U64 deviceRegisters)
{
    if(!original(p)||p->Flags!=PREP_DISCOVERED||!deviceRegisters||deviceRegisters!=p->DeviceRegisters)return 0;
#ifdef CXL_LIFECYCLE_ISOLATION
    if(!NativeLifecycleCheck(p,3))return 0;
#endif
    p->Flags|=PREP_MAILBOX_POSSIBLE;return 1;
}
int NativePreparationMailbox(NATIVE_PREPARATION *p,int completed)
{
    if(!original(p)||p->Flags!=(PREP_DISCOVERED|PREP_MAILBOX_POSSIBLE))return 0;
    if(!completed){NativePreparationQuarantine(p);return 0;}
#ifdef CXL_LIFECYCLE_ISOLATION
    if(!NativeLifecycleCheck(p,4))return 0;
#endif
    p->Flags|=PREP_MAILBOX;return 1;
}
int NativePreparationMapBegin(NATIVE_PREPARATION *p,U64 base,U64 *hdm)
{
    int i;
    if(!original(p)||!(p->Flags&PREP_DISCOVERED)||(p->Flags&PREP_MAP_POSSIBLE)||!base)return 0;
#ifdef NATIVE_MAILBOX_LAB
    if(!(p->Flags&PREP_MAILBOX))return 0;
#endif
    for(i=0;i<3;i++)if(!hdm[i])return 0;
#if defined(CXL_LIFECYCLE_ISOLATION) && !defined(CXL_LIFECYCLE_MAPPING)
    /* Isolation does not confer mapping authority. No HDM writes with generation0. */
    ((SYSTEM *)p->System)->ConOut->OutputString(((SYSTEM *)p->System)->ConOut,L"CXL_LIFECYCLE_MAPPING_PROVIDER_MISSING MAP_ATTEMPTS=0\r\n");
    return 0;
#else
    p->Base=base;for(i=0;i<3;i++)p->Hdm[i]=hdm[i];
    p->Flags|=PREP_MAP_POSSIBLE;p->MapAttempts++;
#ifdef CXL_LIFECYCLE_MAPPING
    if(!NativeLifecycleCheck(p,5))return 0;
#endif
    return 1;
#endif
}
int NativePreparationMapped(NATIVE_PREPARATION *p)
{
    if(!original(p)||!(p->Flags&PREP_MAP_POSSIBLE)||(p->Flags&PREP_LOCAL_RETIRED))return 0;
#ifdef CXL_LIFECYCLE_MAPPING
    if(!NativeLifecycleCheck(p,6))return 0;
#endif
    p->Flags|=PREP_MAP_ACTIVE;return 1;
}
#ifdef CXL_LIFECYCLE_MAPPING
int NativePreparationRouteVerified(NATIVE_PREPARATION *p)
{
    if(!original(p)||!(p->Flags&PREP_MAP_ACTIVE)||(p->Flags&PREP_CPU_ROUTE_VERIFIED))return 0;
    p->Flags|=PREP_CPU_ROUTE_VERIFIED;return NativeLifecycleCheck(p,7);
}
#endif
int NativePreparationAdmitSource(NATIVE_PREPARATION *p,U64 base)
{
    /* Local diagnostic admission, explicitly not current reset/DMA authority. */
    int valid=original(p)&&(p->Flags&PREP_MAP_ACTIVE)&&!(p->Flags&PREP_LOCAL_RETIRED)&&p->Base==base;
#ifdef CXL_LIFECYCLE_MAPPING
    return valid&&(p->Flags&PREP_CPU_ROUTE_VERIFIED)&&NativeLifecycleCheck(p,8);
#else
    return valid;
#endif
}
int NativePreparationCopySnapshot(NATIVE_PREPARATION *p,U64 base,U64 offset,U64 bytes)
{
    U64 i;
    if(!NativePreparationAdmitSource(p,base)||(p->Flags&PREP_SNAPSHOT)||!bytes||bytes>sizeof(p->Snapshot)||
       offset>0x10000000ULL||bytes>0x10000000ULL-offset||base>0xffffffffffffffffULL-offset-bytes)return 0;
    p->SnapshotBytes=bytes;
    for(i=0;i<bytes;i++){p->Snapshot[i]=((volatile U8 *)(base+offset))[i];p->SourceReadBytes++;}
    p->Flags|=PREP_SNAPSHOT;return 1;
}
int NativePreparationAuthenticated(NATIVE_PREPARATION *p,const U8 *snapshot)
{
    if(!original(p)||snapshot!=p->Snapshot||!(p->Flags&PREP_SNAPSHOT)||(p->Flags&PREP_AUTHENTICATED))return 0;
    p->Flags|=PREP_AUTHENTICATED;
#ifdef CXL_LIFECYCLE_MAPPING
    if(!NativeLifecycleCheck(p,9))return 0;
#endif
    return 1;
}
int NativePreparationAllocateRam(NATIVE_PREPARATION *p,const U8 *snapshot,U64 pages,U64 *address)
{
    SYSTEM *system;
    if(!original(p)||snapshot!=p->Snapshot||!(p->Flags&PREP_AUTHENTICATED)||p->RamAllocations||!pages||pages>17)return 0;
    system=(SYSTEM *)p->System;p->RamAllocations++;p->RamPages=pages;
#ifdef CXL_LIFECYCLE_MAPPING
    if(!NativeLifecycleCheck(p,10))return 0;
#endif
    if(((ALLOCATE_PAGES)system->BootServices->Functions[2])(0,2,pages,&p->Ram)){
        NativePreparationQuarantine(p);return 0;
    }
    /* Bind returned original before any geometry check or subsequent callback. */
    p->Flags|=PREP_RAM_OWNED;*address=p->Ram;return 1;
}
int NativePreparationRetired(NATIVE_PREPARATION *p,int localReceipt)
{
    if(!original(p)||!(p->Flags&PREP_MAP_POSSIBLE))return 0;
    if(!localReceipt){NativePreparationQuarantine(p);return 0;}
    p->Flags&=~PREP_MAP_ACTIVE;p->Flags|=PREP_LOCAL_RETIRED;
#ifdef CXL_LIFECYCLE_MAPPING
    if(!NativeLifecycleCheck(p,12))return 0;
#endif
    return 1;
}
int NativePreparationAdmitRam(NATIVE_PREPARATION *p,U64 address,U64 pages)
{
    int valid=original(p)&&(p->Flags&PREP_RAM_OWNED)&&address==p->Ram&&pages==p->RamPages&&
        pages&&pages<=17&&address&&!(address&4095)&&address<=0xffffffffffffffffULL-pages*4096;
#ifdef CXL_LIFECYCLE_MAPPING
    return valid&&NativeLifecycleCheck(p,11);
#else
    return valid;
#endif
}
#ifdef CXL_LIFECYCLE_MAPPING
int NativePreparationPublishRam(NATIVE_PREPARATION *p,int valid)
{
    if(!original(p)||!(p->Flags&PREP_RAM_OWNED)||!(p->Flags&PREP_LOCAL_RETIRED)||(p->Flags&PREP_RAM_PUBLISHED))return 0;
    p->Flags|=PREP_RAM_PUBLISHED;if(valid)p->Flags|=PREP_RAM_VALID;
    return NativeLifecycleCheck(p,13);
}
#endif
int NativePreparationFreeRam(NATIVE_PREPARATION *p,U64 address,U64 pages)
{
    U64 i;SYSTEM *system;STATUS result;
    if(!NativePreparationAdmitRam(p,address,pages)||!(p->Flags&PREP_LOCAL_RETIRED)||p->RamFreeAttempts)return 0;
    /* The caller must clear first; a failed check never grants FreePages. */
    for(i=0;i<pages*4096;i++)if(((volatile U8 *)address)[i]){NativePreparationQuarantine(p);return 0;}
    system=(SYSTEM *)p->System;p->RamFreeAttempts++;
#ifdef CXL_LIFECYCLE_MAPPING
    if(!NativeLifecycleCheck(p,14))return 0;
#endif
    result=((FREE_PAGES)system->BootServices->Functions[3])(address,pages);
    if(result){NativePreparationQuarantine(p);return 0;}
    p->Flags&=~PREP_RAM_OWNED;p->Flags|=PREP_RAM_RELEASED;
#ifdef CXL_LIFECYCLE_MAPPING
    if(!NativeLifecycleCheck(p,15))return 0;
#endif
    return 1;
}
void NativePreparationEnd(void *image,SYSTEM *system)
{
    NATIVE_PREPARATION *p=current;
    if(!p||p->Self!=(U64)p||p->Image!=(U64)image||p->System!=(U64)system)return;
#ifdef CXL_LIFECYCLE_MAPPING
    if(!NativeLifecycleCheck(p,16))return;
#endif
    /* No provider closure exists. Preserve the original owner+snapshot allocation
       even after successful local retirement and copied-System-RAM release. */
    p->Flags|=PREP_QUARANTINED|PREP_FINISHED;
    system->ConOut->OutputString(system->ConOut,L"CXL_NATIVE_OWNER_END RECORD=");hex(system->ConOut,p->Self);
    system->ConOut->OutputString(system->ConOut,L" FLAGS=");hex(system->ConOut,p->Flags);
    system->ConOut->OutputString(system->ConOut,L" RAM=");hex(system->ConOut,p->Ram);
    system->ConOut->OutputString(system->ConOut,L" PAGES=");hex(system->ConOut,p->RamPages);
    system->ConOut->OutputString(system->ConOut,L" ALLOCS=");hex(system->ConOut,p->RamAllocations);
    system->ConOut->OutputString(system->ConOut,L" FREES=");hex(system->ConOut,p->RamFreeAttempts);
    system->ConOut->OutputString(system->ConOut,L" REMOTE_CLOSURE=0\r\n");
    /* This bounded scratch invocation has no platform receipt authorizing
       record reuse. Keeping it allocated also prevents native record-address ABA. */
    system->ConOut->OutputString(system->ConOut,L"CXL_NATIVE_OWNER_PINNED\r\n");current=0;
}
