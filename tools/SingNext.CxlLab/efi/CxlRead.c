/* Explicit lab profiles: byte identity, guest crypto, optional verified RAM copy. */
#include "UefiLab.h"
#include "expected.h"
#ifdef CXL_NATIVE_CAPSULE
#include "NativePreparationOwner.h"
U64 NativeMappedBase(void);
#undef CXL_BASE
#define CXL_BASE NativeMappedBase()
#define EfiMain NativeCapsuleVerify
#endif
#ifndef CXL_NATIVE_CAPSULE
static U8 snapshot[73728];
#endif
#define SNAPSHOT_CAPACITY 73728ULL
#ifdef CXL_CRYPTO
int CryptoSelfTest(void);
int VerifyLabSignature(const U8 *,U64);
#ifdef CXL_RAM_COPY
STATUS RamStage(void *,SYSTEM *,const U8 *);
#endif
#endif
STATUS __cdecl EfiMain(void *image, SYSTEM *system)
{
    U8 difference=0;
#ifndef CXL_NATIVE_CAPSULE
    U64 i;
#endif
#ifdef CXL_NATIVE_CAPSULE
    NATIVE_PREPARATION *owner=NativePreparationCurrent();U8 *snapshot;
    if(!owner)return 0x800000000000001aULL;snapshot=owner->Snapshot;
#endif
    (void)image;
    if(!system || !system->ConOut) return 0x8000000000000002ULL;
    system->ConOut->OutputString(system->ConOut,L"CXL_EFI_READER_ENTER\r\n");
#ifdef CXL_CRYPTO
    if(!CryptoSelfTest()) {system->ConOut->OutputString(system->ConOut,L"CXL_EFI_CRYPTO_SELFTEST_FAILED\r\n");return 0x800000000000001aULL;}
    system->ConOut->OutputString(system->ConOut,L"CXL_EFI_SHA384_KAT_PASSED\r\n");
    system->ConOut->OutputString(system->ConOut,L"CXL_EFI_P384_KAT_AND_NEGATIVES_PASSED\r\n");
#endif
    if(EXPECTED_BYTES==0 || EXPECTED_BYTES>SNAPSHOT_CAPACITY ||
       CXL_OFFSET>0x10000000ULL || EXPECTED_BYTES>0x10000000ULL-CXL_OFFSET ||
       CXL_BASE>0xffffffffffffffffULL-CXL_OFFSET-EXPECTED_BYTES)
        return 0x8000000000000002ULL;
#ifdef CXL_NATIVE_CAPSULE
    if(!NativePreparationCopySnapshot(owner,CXL_BASE,CXL_OFFSET,EXPECTED_BYTES))return 0x800000000000001aULL;
#else
    for(i=0;i<EXPECTED_BYTES;i++) snapshot[i]=((volatile U8 *)(CXL_BASE+CXL_OFFSET))[i];
#endif
#ifdef CXL_CRYPTO
    difference=(U8)!VerifyLabSignature(snapshot,EXPECTED_BYTES);
#else
    for(i=0;i<EXPECTED_BYTES;i++) difference|=snapshot[i]^expected[i];
#endif
    system->ConOut->OutputString(system->ConOut,difference?
        L"CXL_EFI_IMAGE_MISMATCH\r\n":L"CXL_EFI_IMAGE_MATCH\r\n");
#ifdef CXL_CRYPTO
    system->ConOut->OutputString(system->ConOut,difference?L"CXL_EFI_SIGNATURE_OR_HASH_REJECTED\r\n":L"CXL_EFI_SIGNATURE_AND_HASH_VERIFIED\r\n");
#else
    system->ConOut->OutputString(system->ConOut,L"CXL_EFI_SIGNATURE_NOT_VERIFIED\r\n");
#endif
#ifdef CXL_RAM_COPY
    if(!difference){STATUS result;
#ifdef CXL_NATIVE_CAPSULE
        if(!NativePreparationAuthenticated(owner,snapshot))return 0x800000000000001aULL;
#endif
        result=RamStage(image,system,snapshot);
#ifndef CXL_NATIVE_CAPSULE
        for(i=0;i<SNAPSHOT_CAPACITY;i++)((volatile U8 *)snapshot)[i]=0;
#endif
        return result;
    }
#endif
#ifndef CXL_NATIVE_CAPSULE
    for(i=0;i<SNAPSHOT_CAPACITY;i++) ((volatile U8 *)snapshot)[i]=0;
#else
    if(difference)NativePreparationQuarantine(owner);
#endif
    return difference?0x800000000000001aULL:0;
}
