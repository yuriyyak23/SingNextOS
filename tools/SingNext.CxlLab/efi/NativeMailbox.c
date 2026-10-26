/* QEMU lab only. Whitelisted Identify Memory Device (0x4000), no media effects.
   CXL 3.1 sections 8.2.8 and 8.2.9.9.1.1; not a production DMA-isolated driver. */
#include "UefiLab.h"
#include <string.h>
typedef STATUS (__cdecl *STALL)(U64);
typedef struct {U32 Mailbox,MailboxBytes,MemoryStatus;} LAYOUT;
static U8 headers[528],response[128];
static U32 u32(const U8 *p){return (U32)p[0]|((U32)p[1]<<8)|((U32)p[2]<<16)|((U32)p[3]<<24);}
static U64 u64(const U8 *p){return u32(p)|((U64)u32(p+4)<<32);}
static void put(U8 *p,U32 v){int i;for(i=0;i<4;i++){p[i]=(U8)v;v>>=8;}}
static U32 read32(U64 p){return *(volatile U32 *)p;}
static U64 read64(U64 p){return *(volatile U64 *)p;}
static void copyMmio(U8 *destination,U64 address,U32 length)
{
    U32 i,j;for(i=0;i<length;i+=8){U64 value=read64(address+i);for(j=0;j<8&&i+j<length;j++){destination[i+j]=(U8)value;value>>=8;}}
}
static void hex(TEXT *out,U64 v,int digits){U16 s[17];int i;s[digits]=0;for(i=digits-1;i>=0;i--){s[i]=L"0123456789abcdef"[v&15];v>>=4;}out->OutputString(out,s);}
static int decode(const U8 *h,U32 n,LAYOUT *layout)
{
    U32 count,i,j,mb=0,mem=0;U64 header;if(n<16)return 0;header=u64(h);count=(U32)(header>>32)&65535;
    if((header&0xffffff)!=0x10000||(header&0xffff0000f0000000ULL)||((header>>24)&15)>1||u64(h+8)||!count||count>32||n!=16*(count+1))return 0;
    memset(layout,0,sizeof(*layout));
    for(i=1;i<=count;i++){
        U32 id=u32(h+i*16),offset=u32(h+i*16+4),length=u32(h+i*16+8);
        if((id&0xff000000)||!(id&65535)||((id>>16)&255)<1||u32(h+i*16+12)||(offset&7)||offset<n||!length||offset>4096||length>4096-offset)return 0;
        for(j=1;j<i;j++){U32 old=u32(h+j*16+4),len=u32(h+j*16+8);
            if((u32(h+j*16)&65535)==(id&65535)||(offset<old+len&&old<offset+length))return 0;}
        if((id&65535)==2){mb++;if(length<0x120)return 0;layout->Mailbox=offset;layout->MailboxBytes=length;}
        if((id&65535)==0x4000){mem++;if(length<8)return 0;layout->MemoryStatus=offset;}
    }return mb==1&&mem==1;
}
static int selftest(void)
{
    U8 sample[64],copy[64];LAYOUT out;int i;
    memset(sample,0,sizeof(sample));put(sample,0x10000);put(sample+4,3);
    put(sample+16,0x10001);put(sample+20,0x80);put(sample+24,8);
    put(sample+32,0x10002);put(sample+36,0x100);put(sample+40,0x820);
    put(sample+48,0x14000);put(sample+52,0xa00);put(sample+56,8);
    if(!decode(sample,64,&out)||out.Mailbox!=0x100||out.MemoryStatus!=0xa00)return 0;
    for(i=0;i<8;i++){memcpy(copy,sample,64);switch(i){
        case 0:put(copy,0x20000);break;case 1:put(copy+4,33);break;case 2:put(copy+36,32);break;
        case 3:put(copy+40,65536);break;case 4:put(copy+52,0x108);break;
        case 5:put(copy+48,0x10002);break;case 6:put(copy+44,1);break;case 7:put(copy+52,0x1001);break;
    }if(decode(copy,64,&out))return 0;}return 1;
}
int NativeMailboxIdentify(SYSTEM *system,U64 base,U32 memoryInfoValid)
{
    TEXT *out=system->ConOut;U32 count,n,i,exponent,length,stage=1;U64 mailbox,status,command,total,vol,persistent;LAYOUT layout;
    out->OutputString(out,L"CXL_NATIVE_MAILBOX_ENTER OPCODE=4000\r\n");
    if(!selftest())goto failure;out->OutputString(out,L"CXL_NATIVE_MAILBOX_SELFTEST_PASSED NEGATIVES=8\r\n");
    stage=2;if(!memoryInfoValid||!base||(base&4095)||base>0xffffffffffffffffULL-65536)goto failure;
    stage=21;
    copyMmio(headers,base,16);count=u32(headers+4)&65535;
    if(!count||count>32)goto failure;n=16*(count+1);copyMmio(headers+16,base+16,n-16);
    stage=22;
    if(!decode(headers,n,&layout))goto failure;
    mailbox=base+layout.Mailbox;
    out->OutputString(out,L"CXL_NATIVE_MAILBOX_REGS DEVICE=");hex(out,base,16);out->OutputString(out,L" MB=");hex(out,mailbox,16);out->OutputString(out,L" STATUS=");hex(out,base+layout.MemoryStatus,16);out->OutputString(out,L"\r\n");
    stage=3;status=read64(base+layout.MemoryStatus);
    if((status&0xff)!=0x14)goto failure;
    exponent=read32(mailbox)&31;if(exponent<8||exponent>20||(1U<<exponent)>layout.MailboxBytes-0x20||read32(mailbox+4)!=0)goto failure;
    stage=4;*(volatile U64 *)(mailbox+8)=0x4000;*(volatile U32 *)(mailbox+4)=1;
    for(i=0;i<2000;i++){if(!(read32(mailbox+4)&1))break;((STALL)system->BootServices->Functions[28])(1000);}
    if(i==2000)goto failure;
    stage=5;status=read64(mailbox+0x10);command=read64(mailbox+8);length=(U32)((command>>16)&0x1fffff);
    if(((status>>32)&65535)||(status&0xfffffffeULL)||(command&65535)!=0x4000||length<67||length>sizeof(response)||length>(1U<<exponent))goto failure;
    out->OutputString(out,L"CXL_NATIVE_MAILBOX_COMPLETE STATUS=");hex(out,status,16);out->OutputString(out,L" RC=0000\r\n");
    copyMmio(response,mailbox+0x20,length);
    total=u64(response+16);vol=u64(response+24);persistent=u64(response+32);
    stage=6;if(!total||total>0xfffffffffULL||vol>total||persistent>total-vol)goto failure;
    out->OutputString(out,L"CXL_NATIVE_IDENTIFY TOTAL=");hex(out,total<<28,16);out->OutputString(out,L" VOLATILE=");hex(out,vol<<28,16);out->OutputString(out,L" PERSISTENT=");hex(out,persistent<<28,16);out->OutputString(out,L" LSA=");hex(out,u32(response+56),16);out->OutputString(out,L" LEN=");hex(out,length,8);out->OutputString(out,L"\r\n");
    out->OutputString(out,L"CXL_NATIVE_IDENTIFY_RAW ");for(i=0;i<length;i++)hex(out,response[i],2);out->OutputString(out,L"\r\n");
#ifdef CXL_MAILBOX_CAPACITY_GATE
    out->OutputString(out,L"CXL_NATIVE_CAPACITY_GATE EXPECTED=");hex(out,CXL_EXPECTED_PMEM_BYTES,16);out->OutputString(out,L" ACTUAL=");hex(out,persistent<<28,16);out->OutputString(out,L"\r\n");
    stage=7;if((total<<28)!=CXL_EXPECTED_PMEM_BYTES||vol||(persistent<<28)!=CXL_EXPECTED_PMEM_BYTES||u32(response+56)!=1048576)goto failure;
    out->OutputString(out,L"CXL_NATIVE_CAPACITY_GATE_PASSED\r\n");
#endif
    out->OutputString(out,L"CXL_NATIVE_MAILBOX_PASS\r\n");return 1;
failure:out->OutputString(out,L"CXL_NATIVE_MAILBOX_FAILED STAGE=");hex(out,stage,8);out->OutputString(out,L"\r\n");return 0;
}
