/* Lab provisioning into fresh VM RAM-backed CXL, via read-only FAT file input.
   EFI file protocol ABI: UEFI 2.10 sections 13.4/13.5. No host file writes. */
#include "UefiLab.h"
#include "expected.h"
#include "NativePreparationOwner.h"
typedef struct {U32 A;U16 B,C;U8 D[8];} GUID;
typedef struct {U32 Revision;void *Parent;SYSTEM *System;void *Device,*Path,*Reserved;U32 OptionsSize;void *Options;} LOADED;
typedef STATUS (__cdecl *HANDLE)(void *,GUID *,void **);
typedef struct FILE FILE;
struct FILE {U64 Revision;STATUS (__cdecl *Open)(FILE *,FILE **,const U16 *,U64,U64);STATUS (__cdecl *Close)(FILE *);void *Delete;STATUS (__cdecl *Read)(FILE *,U64 *,void *);};
typedef struct FS FS;
struct FS {U64 Revision;STATUS (__cdecl *OpenVolume)(FS *,FILE **);};
static U8 chunk[4096];
static void hex(TEXT *out,U64 v,int digits){U16 s[17];int i;s[digits]=0;for(i=digits-1;i>=0;i--){s[i]=L"0123456789abcdef"[v&15];v>>=4;}out->OutputString(out,s);}
int NativeProvisionCapsule(void *image,SYSTEM *system,U64 base)
{
    GUID loadedId={0x5b1b31a1,0x9562,0x11d2,{0x8e,0x3f,0x00,0xa0,0xc9,0x69,0x72,0x3b}};
    GUID fsId={0x964e5b22,0x6459,0x11d2,{0x8e,0x39,0x00,0xa0,0xc9,0x69,0x72,0x3b}};
    LOADED *loaded=0;FS *fs=0;FILE *root=0,*file=0;U64 at=0,i,n;int ok=0;U32 reason=1;
    if(!NativePreparationAdmitSource(NativePreparationCurrent(),base))goto done;
    if(!EXPECTED_BYTES||EXPECTED_BYTES>73728||CXL_OFFSET>0x10000000ULL||EXPECTED_BYTES>0x10000000ULL-CXL_OFFSET||base>0xffffffffffffffffULL-0x10000000ULL)goto done;
    reason=2;for(i=0;i<EXPECTED_BYTES;i++)if(((volatile U8 *)(base+CXL_OFFSET))[i])goto done;
    reason=3;if(((HANDLE)system->BootServices->Functions[16])(image,&loadedId,(void **)&loaded)||!loaded||!loaded->Device)goto done;
    reason=4;if(((HANDLE)system->BootServices->Functions[16])(loaded->Device,&fsId,(void **)&fs)||!fs||fs->OpenVolume(fs,&root)||!root)goto done;
    reason=5;if(root->Open(root,&file,L"\\capsule.bin",1,0)||!file)goto done;
    reason=6;
    while(at<EXPECTED_BYTES){
        n=EXPECTED_BYTES-at;if(n>sizeof(chunk))n=sizeof(chunk);
        if(file->Read(file,&n,chunk)||!n||n>sizeof(chunk)||n>EXPECTED_BYTES-at)goto done;
        if(!NativePreparationAdmitSource(NativePreparationCurrent(),base))goto done;
        for(i=0;i<n;i++)((volatile U8 *)(base+CXL_OFFSET+at))[i]=chunk[i];
        at+=n;
    }
    reason=7;n=1;if(file->Read(file,&n,chunk)||n)goto done;ok=1;
done:
    if(file&&file->Close(file)){ok=0;reason=8;}
    if(root&&root->Close(root)){ok=0;reason=8;}
    for(i=0;i<sizeof(chunk);i++)((volatile U8 *)chunk)[i]=0;
    if(ok)system->ConOut->OutputString(system->ConOut,L"CXL_NATIVE_CAPSULE_FILE_STAGED\r\n");
    else{system->ConOut->OutputString(system->ConOut,L"CXL_NATIVE_CAPSULE_FILE_FAILED REASON=");hex(system->ConOut,reason,8);system->ConOut->OutputString(system->ConOut,L" READ=");hex(system->ConOut,at,16);system->ConOut->OutputString(system->ConOut,L"\r\n");}return ok;
}
