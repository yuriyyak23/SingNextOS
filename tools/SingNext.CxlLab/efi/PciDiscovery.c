/* Read-only lab discovery via existing EFI PCI I/O. No DMA isolation claim. */
#include "UefiLab.h"
#include <string.h>
#ifdef CXL_NATIVE_CAPSULE
#include "NativePreparationOwner.h"
#endif
typedef struct {U32 A;U16 B,C;U8 D[8];} GUID;
typedef struct PCI PCI;
typedef STATUS (__cdecl *PCI_READ)(PCI *,U32,U32,U64,void *);
struct PCI {void *PollMem,*PollIo,*MemRead,*MemWrite,*IoRead,*IoWrite;PCI_READ Read;void *Write,*CopyMem,*Map,*Unmap,*AllocateBuffer,*FreeBuffer,*Flush;STATUS (__cdecl *Location)(PCI *,U64 *,U64 *,U64 *,U64 *);};
typedef STATUS (__cdecl *LOCATE)(U32,GUID *,void *,U64 *,void ***);
typedef STATUS (__cdecl *HANDLE)(void *,GUID *,void **);
typedef STATUS (__cdecl *FREE)(void *);
typedef struct {U64 Component;U32 Overlaps;U64 DeviceRegisters;U32 MemoryInfoValid;} DECODE;
static U8 endpoint[4096],port[4096],scratch[4096];
int NativeAcpiDiscovery(SYSTEM *system);
#ifdef NATIVE_MAILBOX_LAB
int NativeMailboxIdentify(SYSTEM *system,U64 deviceBase,U32 memoryInfoValid);
#endif
#ifdef NATIVE_HDM_LAB
int NativeHdmExercise(void *image,SYSTEM *system,U64 endpointBase,U64 portBase);
#endif
static U32 u16(const U8 *p){return (U32)p[0]|((U32)p[1]<<8);}
static U32 u32(const U8 *p){return u16(p)|(u16(p+2)<<16);}
static void put(U8 *p,U32 v){int i;for(i=0;i<4;i++){p[i]=(U8)v;v>>=8;}}
static int resolve(const U8 *c,U32 bir,U64 offset,U64 *result)
{
    U32 type=c[14]&127,count=type==0?6:type==1?2:0,i;
    for(i=0;i<count;i++){
        U32 low=u32(c+16+i*4),wide=(low&6)==4;U64 base;
        if(i==bir){
            if((low&1)||((low&6)!=0&&!wide)||(wide&&i+1>=count))return 0;
            base=low&0xfffffff0U;if(wide)base|=(U64)u32(c+20+i*4)<<32;
            if(!base||base==0xfffffff0ULL||base==0xfffffffffffffff0ULL||offset>0xffffffffffffffffULL-base)return 0;
            *result=base+offset;return 1;
        }
        if(wide){if(i+1==bir)return 0;i++;}
    }return 0;
}
static int decode(const U8 *c,U32 bytes,int memory,DECODE *output)
{
    U8 visited[1024],ids=0;U32 at=256,step,device=0,locator=0,component=0,deviceBlock=0,overlaps=0;
    U64 componentBase=0,deviceBase=0;U32 memoryInfoValid=0;
    if(bytes!=4096||c[11]!=(memory?5:6)||c[10]!=(memory?2:4))return 0;
    memset(visited,0,sizeof(visited));
    for(step=0;at&&step<64;step++){
        U32 cap,next,h,len,id,revision,end;
        if(at<256||at>4092||(at&3)||visited[at/4])return 0;
        visited[at/4]=1;cap=u32(c+at);if(!cap||cap==0xffffffffU){at=0;break;}
        next=cap>>20;if(next&&(next<256||next>4092||(next&3)))return 0;
        if((cap&65535)==0x23){
            if(at>4086||((cap>>16)&15)!=1)return 0;
            h=u32(c+at+4);len=h>>20;id=u16(c+at+8);revision=(h>>16)&15;
            if(len<10||len>4096-at)return 0;
            end=at+len;
            if(next>=at&&next<end){if(id==0||id==8||next<at+10)return 0;overlaps++;}
            if((h&65535)==0x1e98){
                if(id==0){if(device++||revision<1||revision>3||len<(revision==3?0x3cU:0x38U)||(memory&&(u16(c+at+10)&5)!=4))return 0;
                    memoryInfoValid=(((u16(c+at+10)>>4)&3)==1&&(u32(c+at+0x1c)&1));}
                if(id==8){
                    U32 pos,n=0,birs[16];U64 offsets[16];
                    if(locator++||revision||len<20||(len-12)%8||(len-12)/8>16||u16(c+at+10))return 0;
                    for(pos=at+12;pos<end;pos+=8){
                        U32 low=u32(c+pos),high=u32(c+pos+4),bir=low&7,block=(low>>8)&255,j;U64 offset,address;
                        if(!low&&!high)continue;
                        if(bir>5||(low&0xf8)||block<1||block>3||(ids&(1<<block)))return 0;
                        ids|=(U8)(1<<block);offset=((U64)high<<32)|(low&0xffff0000U);
                        for(j=0;j<n;j++)if(birs[j]==bir&&offsets[j]==offset)return 0;
                        birs[n]=bir;offsets[n++]=offset;
                        if(!resolve(c,bir,offset,&address))return 0;
                        if(block==1){component++;componentBase=address;}if(block==3){deviceBlock++;deviceBase=address;}
                    }
                }
            }
        }at=next;
    }
    if(at||!locator||component!=1||(memory&&(!device||!deviceBlock)))return 0;
    output->Component=componentBase;output->Overlaps=overlaps;output->DeviceRegisters=deviceBase;output->MemoryInfoValid=memoryInfoValid;return 1;
}
static int selftest(void)
{
    DECODE d;int i;memset(endpoint,0,4096);endpoint[11]=5;endpoint[10]=2;
    put(endpoint+16,0x81200004);put(endpoint+24,0x81210004);
    put(endpoint+0x100,0x14010023);put(endpoint+0x104,0x03c31e98);endpoint[0x10a]=0x1e;
    put(endpoint+0x140,0x00010023);put(endpoint+0x144,0x02401e98);endpoint[0x148]=8;put(endpoint+0x14c,0x100);put(endpoint+0x154,0x302);
    if(!decode(endpoint,4096,1,&d)||d.Component!=0x81200000)return 0;
    for(i=0;i<13;i++){
        memcpy(scratch,endpoint,4096);
        switch(i){
        case 0:put(scratch+0x100,0x10010023);break;
        case 1:put(scratch+0x104,0x00031e98);break;
        case 2:put(scratch+0x104,0x03c41e98);break;
        case 3:put(scratch+0x14c,0x107);break;
        case 4:put(scratch+16,0x81200001);break;
        case 5:put(scratch+0x14c,0x101);break;
        case 6:put(scratch+0x154,0x100);break;
        case 7:put(scratch+0x144,0x02411e98);break;
        case 8:scratch[0x10a]|=1;break;
        case 9:put(scratch+0x14c,0x108);break;
        case 10:put(scratch+0x100,0x10210023);break;
        case 11:put(scratch+0x104,0xfff31e98);break;
        default:break;
        }if(decode(scratch,i==12?4092:4096,1,&d))return 0;
    }return 1;
}
static void hex(TEXT *out,U64 value,int digits){U16 s[17];int i;s[digits]=0;for(i=digits-1;i>=0;i--){s[i]=L"0123456789abcdef"[value&15];value>>=4;}out->OutputString(out,s);}
static void raw(TEXT *out,const U16 *name,const U8 *c)
{
    U32 at,j;U16 data[33];data[32]=0;
    for(at=0;at<4096;at+=16){out->OutputString(out,L"PCIRAW ");out->OutputString(out,name);out->OutputString(out,L" ");hex(out,at,4);out->OutputString(out,L" ");
        for(j=0;j<16;j++){data[j*2]=L"0123456789abcdef"[c[at+j]>>4];data[j*2+1]=L"0123456789abcdef"[c[at+j]&15];}out->OutputString(out,data);out->OutputString(out,L"\r\n");}
}
STATUS __cdecl EfiMain(void *image,SYSTEM *system)
{
    GUID id={0x4cf5b200,0x68b8,0x4ca5,{0x9e,0xec,0xb2,0x3e,0x3f,0x50,0x02,0x9a}};
    void **handles=0;U64 count=0,i,epBdf=0,rpBdf=0,epBus=0,epSegment=0;U32 found=0,parents=0;DECODE ep={0},rp={0};TEXT *out;STATUS status=0x8000000000000003ULL;
    (void)image;if(!system||!system->ConOut||!system->BootServices)return status;out=system->ConOut;
    out->OutputString(out,L"CXL_NATIVE_PCI_ENTER\r\n");
    if(!selftest()){out->OutputString(out,L"CXL_NATIVE_PCI_SELFTEST_FAILED\r\n");return status;}
    out->OutputString(out,L"CXL_NATIVE_PCI_SELFTEST_PASSED NEGATIVES=13\r\n");
#ifdef CXL_NATIVE_CAPSULE
    if(!NativePreparationBegin(image,system))return status;
#endif
    if(!NativeAcpiDiscovery(system))goto cleanup;
    if(((LOCATE)system->BootServices->Functions[36])(2,&id,0,&count,&handles)||!handles||!count||count>256)goto cleanup;
    for(i=0;i<count;i++){
        PCI *io=0;U64 segment,bus,dev,fn;
        if(((HANDLE)system->BootServices->Functions[16])(handles[i],&id,(void **)&io)||!io||io->Location(io,&segment,&bus,&dev,&fn)||segment>65535||bus>255||dev>31||fn>7||io->Read(io,2,0,16,scratch))goto cleanup;
        if(scratch[11]==5&&scratch[10]==2){
            if(io->Read(io,2,0,1024,endpoint)||!decode(endpoint,4096,1,&ep))goto cleanup;
            found++;epBus=bus;epSegment=segment;epBdf=(segment<<24)|(bus<<16)|(dev<<8)|fn;
        }
    }
    if(found!=1)goto cleanup;
    for(i=0;i<count;i++){
        PCI *io=0;U64 segment,bus,dev,fn;
        if(((HANDLE)system->BootServices->Functions[16])(handles[i],&id,(void **)&io)||!io||io->Location(io,&segment,&bus,&dev,&fn)||io->Read(io,2,0,16,scratch))goto cleanup;
        if(segment==epSegment&&scratch[11]==6&&scratch[10]==4&&scratch[0x19]==epBus){
            if(bus>255||dev>31||fn>7||io->Read(io,2,0,1024,port)||!decode(port,4096,0,&rp))goto cleanup;
            parents++;rpBdf=(segment<<24)|(bus<<16)|(dev<<8)|fn;
        }
    }
    if(parents!=1)goto cleanup;
#ifdef CXL_NATIVE_CAPSULE
    if(!NativePreparationDiscovery(NativePreparationCurrent(),image,system,epBdf,rpBdf,ep.DeviceRegisters))goto cleanup;
#endif
    out->OutputString(out,L"CXL_NATIVE_EP BDF=");hex(out,epBdf,16);out->OutputString(out,L" BASE=");hex(out,ep.Component,16);out->OutputString(out,L"\r\n");
    out->OutputString(out,L"CXL_NATIVE_RP BDF=");hex(out,rpBdf,16);out->OutputString(out,L" BASE=");hex(out,rp.Component,16);out->OutputString(out,L"\r\n");
    raw(out,L"EP",endpoint);raw(out,L"RP",port);
    out->OutputString(out,L"CXL_NATIVE_PCI_PASS OVERLAPS=");hex(out,ep.Overlaps+rp.Overlaps,16);out->OutputString(out,L"\r\n");status=0;
#ifdef NATIVE_MAILBOX_LAB
#ifdef CXL_NATIVE_CAPSULE
    /* Wrong register identity must deny before the real mailbox effect and must
       not consume the original owner admission. */
    if(NativePreparationAdmitMailbox(NativePreparationCurrent(),0)||
       !NativePreparationAdmitMailbox(NativePreparationCurrent(),ep.DeviceRegisters)){
        status=0x8000000000000003ULL;goto cleanup;
    }
    out->OutputString(out,L"CXL_NATIVE_OWNER_FOREIGN_MAILBOX_DENIED\r\n");
#endif
    if(u32(endpoint)!=0x0d938086||!NativeMailboxIdentify(system,ep.DeviceRegisters,ep.MemoryInfoValid))status=0x8000000000000003ULL;
#ifdef CXL_NATIVE_CAPSULE
    if(!NativePreparationMailbox(NativePreparationCurrent(),!status))status=0x8000000000000003ULL;
#endif
#endif
#ifdef NATIVE_HDM_LAB
    if(!status&&(u32(endpoint)!=0x0d938086||u32(port)!=0x70758086||!NativeHdmExercise(image,system,ep.Component,rp.Component)))status=0x8000000000000003ULL;
#endif
cleanup:
    if(handles)((FREE)system->BootServices->Functions[6])(handles);
#ifdef CXL_NATIVE_CAPSULE
    {
        NATIVE_PREPARATION *old=NativePreparationCurrent();
        U64 oldBase=old?old->Base:0,oldRam=old?old->Ram:0,oldPages=old?old->RamPages:0;
        NativePreparationEnd(image,system);
        if(old){
            if(NativePreparationAdmitSource(old,oldBase)||NativePreparationAdmitRam(old,oldRam,oldPages)||NativePreparationFreeRam(old,oldRam,oldPages))
                status=0x800000000000001aULL;
            else out->OutputString(out,L"CXL_NATIVE_OWNER_STALE_SESSION_DENIED\r\n");
        }
    }
#endif
    if(status){out->OutputString(out,L"CXL_NATIVE_PCI_UNSUPPORTED_OR_INVALID HANDLES=");hex(out,count,16);out->OutputString(out,L"\r\n");}
    out->OutputString(out,L"CXL_NATIVE_DMA_ISOLATION_NOT_QUALIFIED\r\n");return status;
}
