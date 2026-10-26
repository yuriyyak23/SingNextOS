#ifndef CXL_NATIVE_PREPARATION_OWNER_H
#define CXL_NATIVE_PREPARATION_OWNER_H
#include "UefiLab.h"
/* Diagnostic invocation identity only. No fabricated reset/isolation generation.
   The record and snapshot live in LoaderData, independently of EFI image unload. */
#define PREP_DISCOVERED 1ULL
#define PREP_MAILBOX 2ULL
#define PREP_MAP_POSSIBLE 4ULL
#define PREP_MAP_ACTIVE 8ULL
#define PREP_SNAPSHOT 16ULL
#define PREP_AUTHENTICATED 32ULL
#define PREP_RAM_OWNED 64ULL
#define PREP_LOCAL_RETIRED 128ULL
#define PREP_RAM_RELEASED 256ULL
#define PREP_QUARANTINED 512ULL
#define PREP_FINISHED 1024ULL
#define PREP_MAILBOX_POSSIBLE 2048ULL
#define PREP_CPU_ROUTE_VERIFIED 4096ULL
#define PREP_RAM_PUBLISHED 8192ULL
#define PREP_RAM_VALID 16384ULL
typedef struct {
    U64 Magic,Self,Image,System;
    U64 Base,Hdm[3];
    U64 Ram,RamPages,SnapshotBytes,Flags;
    U64 MapAttempts,RamAllocations,RamFreeAttempts,SourceReadBytes;
    U8 Snapshot[73728];
    U64 EndpointBdf,PortBdf,DeviceRegisters;
#ifdef CXL_LIFECYCLE_ISOLATION
    U64 PlatformBuffers,PlatformPages,PlatformAllocations;
#endif
} NATIVE_PREPARATION;
NATIVE_PREPARATION *NativePreparationCurrent(void);
int NativePreparationBegin(void *,SYSTEM *);
int NativePreparationDiscovery(NATIVE_PREPARATION *,void *,SYSTEM *,U64,U64,U64);
int NativePreparationAdmitMailbox(NATIVE_PREPARATION *,U64);
int NativePreparationMailbox(NATIVE_PREPARATION *,int);
int NativePreparationMapBegin(NATIVE_PREPARATION *,U64,U64 *);
int NativePreparationMapped(NATIVE_PREPARATION *);
int NativePreparationAdmitSource(NATIVE_PREPARATION *,U64);
int NativePreparationCopySnapshot(NATIVE_PREPARATION *,U64,U64,U64);
int NativePreparationAuthenticated(NATIVE_PREPARATION *,const U8 *);
int NativePreparationAllocateRam(NATIVE_PREPARATION *,const U8 *,U64,U64 *);
int NativePreparationAdmitRam(NATIVE_PREPARATION *,U64,U64);
int NativePreparationRetired(NATIVE_PREPARATION *,int);
int NativePreparationFreeRam(NATIVE_PREPARATION *,U64,U64);
void NativePreparationQuarantine(NATIVE_PREPARATION *);
void NativePreparationEnd(void *,SYSTEM *);
#ifdef CXL_LIFECYCLE_ISOLATION
int NativeLifecycleCheck(NATIVE_PREPARATION *,U64);
#endif
#ifdef CXL_LIFECYCLE_MAPPING
int NativePreparationRouteVerified(NATIVE_PREPARATION *);
int NativePreparationPublishRam(NATIVE_PREPARATION *,int);
void NativeLifecycleAbort(NATIVE_PREPARATION *);
#endif
#endif
