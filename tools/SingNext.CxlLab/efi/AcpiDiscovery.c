/* Bounded, read-only ACPI2/XSDT/CEDT diagnostic. Single-target CXL2 lab profile. */
#include "UefiLab.h"
#include <string.h>
typedef struct {U32 A;U16 B,C;U8 D[8];} GUID;
typedef struct {GUID Id;void *Table;} CONFIG;
typedef STATUS (__cdecl *GET_MAP)(U64 *,void *,U64 *,U64 *,U32 *);
typedef struct {U64 Base,Bytes,HostBase,HostBytes;U32 Uid,Restrictions;} CEDT;
static CEDT discoveredGeometry;
static int geometryValid;
int NativeAcpiGeometry(U64 *base,U64 *bytes,U64 *host,U64 *hostBytes)
{
    if(!geometryValid)return 0;
    *base=discoveredGeometry.Base;*bytes=discoveredGeometry.Bytes;
    *host=discoveredGeometry.HostBase;*hostBytes=discoveredGeometry.HostBytes;return 1;
}
__declspec(align(8)) static U8 memoryMap[65536];
static U64 mapBytes,mapStride;
static U8 testTable[256],testCopy[256];
static U32 r16(const U8 *p){return (U32)p[0]|((U32)p[1]<<8);}
static U32 r32(const U8 *p){return r16(p)|(r16(p+2)<<16);}
static U64 r64(const U8 *p){return r32(p)|((U64)r32(p+4)<<32);}
static void w32(U8 *p,U32 v){int i;for(i=0;i<4;i++){p[i]=(U8)v;v>>=8;}}
static void w64(U8 *p,U64 v){int i;for(i=0;i<8;i++){p[i]=(U8)v;v>>=8;}}
static U8 sum(const U8 *p,U64 n){U64 i;U8 s=0;for(i=0;i<n;i++)s=(U8)(s+p[i]);return s;}
static int acpiRange(U64 address,U64 n)
{
    U64 i;if(!n||address>0xffffffffffffffffULL-n)return 0;
    for(i=0;i<mapBytes;i+=mapStride){const U8 *d=memoryMap+i;U64 start=r64(d+8),pages=r64(d+24),size;
        if(pages>0xffffffffffffffffULL/4096)continue;size=pages*4096;
        if((r32(d)==9||r32(d)==10)&&address>=start&&address-start<=size&&n<=size-(address-start))return 1;
    }return 0;
}
static const U8 *table(U64 address,U32 *length)
{
    const U8 *p=(const U8 *)address;
    if(!acpiRange(address,36))return 0;*length=r32(p+4);
    if(*length<36||*length>65536||!acpiRange(address,*length)||sum(p,*length))return 0;
    return p;
}
#ifdef CXL_LIFECYCLE_ISOLATION
/* Read-only input discovery before PCI. Advertisement alone never grants isolation. */
int NativePlatformDmar(SYSTEM *system,U64 *registers,U64 owned,U64 pages)
{
    GUID acpi={0x8868e871,0xe4f1,0x11d3,{0xbc,0x22,0x00,0x80,0xc7,0x3c,0x88,0x81}};
    const U8 *rsdp=0,*xsdt,*dmar=0;U64 i,key=0;U32 version=0,n,xn,units=0,at;
    mapBytes=sizeof(memoryMap);mapStride=0;
    if(((GET_MAP)system->BootServices->Functions[4])(&mapBytes,memoryMap,&key,&mapStride,&version)||version!=1||mapStride<40||mapStride>256||mapBytes>sizeof(memoryMap)||mapBytes%mapStride)return 0;
    if(!owned||(owned&4095)||pages!=12||owned>0x10000000ULL-pages*4096)return 0;
    {int found=0;for(i=0;i<mapBytes;i+=mapStride){const U8 *d=memoryMap+i;U64 start=r64(d+8),count=r64(d+24);
        if(r32(d)==2&&start<=owned&&(owned-start)%4096==0&&(owned-start)/4096<=count&&pages<=count-(owned-start)/4096)found++;}
        if(found!=1)return 0;}
    if(!system->ConfigurationTable||system->NumberOfTableEntries>256)return 0;
    for(i=0;i<system->NumberOfTableEntries;i++){const CONFIG *c=(const CONFIG *)system->ConfigurationTable+i;
        if(!memcmp(&c->Id,&acpi,sizeof(acpi))){if(rsdp)return 0;rsdp=(const U8 *)c->Table;}}
    if(!rsdp||!acpiRange((U64)rsdp,36)||memcmp(rsdp,"RSD PTR ",8)||rsdp[15]<2||sum(rsdp,20))return 0;
    n=r32(rsdp+20);if(n<36||n>4096||!acpiRange((U64)rsdp,n)||sum(rsdp,n))return 0;
    xsdt=table(r64(rsdp+24),&xn);if(!xsdt||memcmp(xsdt,"XSDT",4)||(xn-36)%8||(xn-36)/8>128)return 0;
    for(i=36;i<xn;i+=8){U64 a=r64(xsdt+i);const U8 *candidate;
        if(!acpiRange(a,36))return 0;
        if(!memcmp((const void *)a,"DMAR",4)){if(dmar)return 0;candidate=table(a,&n);if(!candidate)return 0;dmar=candidate;}}
    if(!dmar||r32(dmar+4)<48||r32(dmar+4)>65536||dmar[36]!=47)return 0;
    for(at=48;at<r32(dmar+4);){U32 len,s;
        if(r32(dmar+4)-at<4)return 0;len=r16(dmar+at+2);if(len<4||len>r32(dmar+4)-at)return 0;
        if(r16(dmar+at)==0){U64 base;
            if(units++||len<16||r16(dmar+at+6)!=0)return 0;base=r64(dmar+at+8);
            if(!base||(base&4095)||base>0xfffff000ULL)return 0;
            for(s=16;s<len;){U32 size;if(len-s<6)return 0;size=dmar[at+s+1];
                if(size<8||size>len-s||(size-6)%2)return 0;s+=size;}
            *registers=base;
        }at+=len;
    }return units==1;
}
#endif
static int decode(const U8 *p,U32 n,CEDT *out)
{
    U32 at,count=0,windows=0,hosts=0;CEDT found={0};
    if(n<36||n>65536||memcmp(p,"CEDT",4)||r32(p+4)!=n||p[8]!=1||sum(p,n))return 0;
    for(at=36;at<n;){U32 len;if(n-at<4||count++>=256)return 0;len=r16(p+at+2);if(len<4||len>n-at)return 0;
        if(p[at]==1){const U8 *w=p+at;U32 restrictions;
            if(windows++||len!=40||w[1]||r32(w+4)||w[24]||w[25]||r16(w+26)||r32(w+28)>6)return 0;
            restrictions=r16(w+32);if((restrictions&0xa)!=0xa||(restrictions&~0x3fU))return 0;
            found.Restrictions=restrictions; /* BI is recorded, never enabled/qualified here. */
            found.Base=r64(w+8);found.Bytes=r64(w+16);found.Uid=r32(w+36);
            if(!found.Base||(found.Base&0xfffffff)||found.Bytes<0x10000000ULL||(found.Bytes&0xfffffff)||found.Base>0xffffffffffffffffULL-found.Bytes)return 0;
        }at+=len;
    }
    if(windows!=1)return 0;
    for(at=36;at<n;at+=r16(p+at+2))if(p[at]==0){const U8 *h=p+at;
        if(r16(h+2)!=32||h[1]||r32(h+12)||r32(h+8)!=1)return 0;
        if(r32(h+4)==found.Uid){if(hosts++)return 0;found.HostBase=r64(h+16);found.HostBytes=r64(h+24);
            if(!found.HostBase||(found.HostBase&65535)||found.HostBytes<65536||found.HostBytes>0x1000000ULL||found.HostBase>0xffffffffffffffffULL-found.HostBytes)return 0;}
    }
    if(hosts!=1)return 0;*out=found;return 1;
}
static void checksum(U8 *p,U32 n){p[9]=0;p[9]=(U8)(0-sum(p,n));}
static int selftest(void)
{
    CEDT out;int i;memset(testTable,0,sizeof(testTable));memcpy(testTable,"CEDT",4);w32(testTable+4,108);testTable[8]=1;
    testTable[38]=32;w32(testTable+40,7);w32(testTable+44,1);w64(testTable+52,0x180000000ULL);w64(testTable+60,65536);
    testTable[68]=1;testTable[70]=40;w64(testTable+76,0x190000000ULL);w64(testTable+84,0x100000000ULL);testTable[100]=0xa;w32(testTable+104,7);checksum(testTable,108);
    if(!decode(testTable,108,&out)||out.Base!=0x190000000ULL||out.HostBase!=0x180000000ULL)return 0;
    for(i=0;i<10;i++){U32 n=108;memcpy(testCopy,testTable,sizeof(testTable));
        switch(i){
        case 0:testCopy[9]^=1;break;
        case 1:w32(testCopy+4,109);break;
        case 2:testCopy[38]=0;break;
        case 3:testCopy[92]=1;break;
        case 4:w64(testCopy+84,0);break;
        case 5:testCopy[76]=1;break;
        case 6:w32(testCopy+104,8);break;
        case 7:w32(testCopy+44,2);break;
        case 8:testCopy[100]=2;break;
        default:memcpy(testCopy+108,testCopy+68,40);n=148;w32(testCopy+4,n);break;
        }if(i!=0)checksum(testCopy,n);if(decode(testCopy,n,&out))return 0;
    }testTable[100]|=0x20;checksum(testTable,108);if(!decode(testTable,108,&out)||(out.Restrictions&0x20)==0)return 0;
    return 1;
}
static void hex(TEXT *out,U64 v,int digits){U16 s[17];int i;s[digits]=0;for(i=digits-1;i>=0;i--){s[i]=L"0123456789abcdef"[v&15];v>>=4;}out->OutputString(out,s);}
static void dump(TEXT *out,const U8 *cedt,U32 bytes)
{
    U64 i;for(i=0;i<bytes;i+=16){U64 j;U16 text[33];out->OutputString(out,L"CEDTRAW ");hex(out,i,4);out->OutputString(out,L" ");for(j=0;j<16&&j<bytes-i;j++){text[2*j]=L"0123456789abcdef"[cedt[i+j]>>4];text[2*j+1]=L"0123456789abcdef"[cedt[i+j]&15];}text[2*j]=0;out->OutputString(out,text);out->OutputString(out,L"\r\n");}
}
int NativeAcpiDiscovery(SYSTEM *system)
{
    GUID acpi={0x8868e871,0xe4f1,0x11d3,{0xbc,0x22,0x00,0x80,0xc7,0x3c,0x88,0x81}};
    const U8 *rsdp=0,*xsdt,*cedt=0;U32 length,xsdtLength=0,cedtLength=0,stage=1;U64 i,count=0,key=0,address=0;U32 version=0;CEDT result;TEXT *out=system->ConOut;
    geometryValid=0;
    if(!selftest())goto failure;out->OutputString(out,L"CXL_NATIVE_ACPI_SELFTEST_PASSED NEGATIVES=10\r\n");
    stage=2;mapBytes=sizeof(memoryMap);mapStride=0;
    if(((GET_MAP)system->BootServices->Functions[4])(&mapBytes,memoryMap,&key,&mapStride,&version)||version!=1||mapStride<40||mapStride>256||mapBytes>sizeof(memoryMap)||mapBytes%mapStride)goto failure;
    stage=3;if(!system->ConfigurationTable||system->NumberOfTableEntries>256)goto failure;
    for(i=0;i<system->NumberOfTableEntries;i++){const CONFIG *c=(const CONFIG *)system->ConfigurationTable+i;if(!memcmp(&c->Id,&acpi,sizeof(acpi))){if(rsdp)goto failure;rsdp=(const U8 *)c->Table;}}
    stage=4;if(!rsdp||!acpiRange((U64)rsdp,36)||memcmp(rsdp,"RSD PTR ",8)||rsdp[15]<2||sum(rsdp,20))goto failure;
    length=r32(rsdp+20);if(length<36||length>4096||!acpiRange((U64)rsdp,length)||sum(rsdp,length))goto failure;
    stage=5;xsdt=table(r64(rsdp+24),&xsdtLength);if(!xsdt||memcmp(xsdt,"XSDT",4)||(xsdtLength-36)%8||(xsdtLength-36)/8>128)goto failure;
    for(i=36;i<xsdtLength;i+=8){const U8 *candidate;U32 n;U64 a=r64(xsdt+i);if(!acpiRange(a,36))goto failure;
        if(!memcmp((const void *)a,"CEDT",4)){candidate=table(a,&n);if(!candidate||count++)goto failure;cedt=candidate;cedtLength=n;address=a;}}
    stage=6;if(!cedt)goto failure;
    out->OutputString(out,L"CXL_NATIVE_CEDT ADDR=");hex(out,address,16);out->OutputString(out,L" LEN=");hex(out,cedtLength,16);out->OutputString(out,L"\r\n");
    dump(out,cedt,cedtLength);if(!decode(cedt,cedtLength,&result))goto failure;
    out->OutputString(out,L"CXL_NATIVE_CFMW BASE=");hex(out,result.Base,16);out->OutputString(out,L" SIZE=");hex(out,result.Bytes,16);out->OutputString(out,L"\r\n");
    out->OutputString(out,L"CXL_NATIVE_CHBS BASE=");hex(out,result.HostBase,16);out->OutputString(out,L" SIZE=");hex(out,result.HostBytes,16);out->OutputString(out,L" UID=");hex(out,result.Uid,8);out->OutputString(out,L"\r\n");
    out->OutputString(out,L"CXL_NATIVE_CFMW_RESTRICTIONS RAW=");hex(out,result.Restrictions,4);out->OutputString(out,L" POLICY_QUALIFIED=0\r\n");
    if(result.Restrictions&0x20)out->OutputString(out,L"CXL_NATIVE_BI_ADVERTISED_NOT_QUALIFIED\r\n");
    discoveredGeometry=result;geometryValid=1;
    out->OutputString(out,L"CXL_NATIVE_ACPI_PASS\r\n");return 1;
failure:out->OutputString(out,L"CXL_NATIVE_ACPI_FAILURE STAGE=");hex(out,stage,8);out->OutputString(out,L"\r\n");return 0;
}
