#include "bearssl.h"
#include <string.h>
#include "trust.h"
static unsigned long r32(const unsigned char *p){return (unsigned long)p[0]|((unsigned long)p[1]<<8)|((unsigned long)p[2]<<16)|((unsigned long)p[3]<<24);}
static unsigned __int64 r64(const unsigned char *p){return r32(p)|((unsigned __int64)r32(p+4)<<32);}
static int zero(const unsigned char *p,size_t n){size_t i;unsigned char d=0;for(i=0;i<n;i++)d|=p[i];return d==0;}
static unsigned long crc32c(const unsigned char *p,size_t n){unsigned long c=0xffffffffUL;size_t i;int bit;for(i=0;i<n;i++){c^=p[i];for(bit=0;bit<8;bit++)c=(c>>1)^((0UL-(c&1))&0x82f63b78UL);}return ~c;}
static void hash384(const void *p,size_t n,unsigned char out[48]){br_sha384_context c;br_sha384_init(&c);br_sha384_update(&c,p,n);br_sha384_out(&c,out);}
int P384KnownAnswer(void);
int CryptoSelfTest(void)
{
    static const unsigned char abc[48]={0xcb,0x00,0x75,0x3f,0x45,0xa3,0x5e,0x8b,0xb5,0xa0,0x3d,0x69,0x9a,0xc6,0x50,0x07,0x27,0x2c,0x32,0xab,0x0e,0xde,0xd1,0x63,0x1a,0x8b,0x60,0x5a,0x43,0xff,0x5b,0xed,0x80,0x86,0x07,0x2b,0xa1,0xe7,0xcc,0x23,0x58,0xba,0xec,0xa1,0x34,0xc8,0x25,0xa7};
    unsigned char digest[48];hash384("abc",3,digest);return memcmp(digest,abc,48)==0 && P384KnownAnswer();
}
/* Strict lab profile only. This is not the full production BootManifest policy. */
int VerifyLabSignature(const unsigned char *image,size_t bytes)
{
    static const unsigned char platform[16]={0xfb,0xa1,0x2e,0x33,0x22,0x94,0x44,0x31,0x96,0xbc,0x6d,0x63,0x5b,0x44,0x00,0x01};
    const unsigned char *m,*d;unsigned long length;unsigned __int64 payload;
    unsigned char digest[48];br_ec_public_key key;
    if(bytes<8193 || bytes>73728)return 0;
    m=image+4096;
    if(r32(image)!=0x31564248UL || r32(image+4)!=0x10000001UL || r64(image+8)==0 ||
       zero(image+16,16)||zero(image+32,16)||memcmp(image+16,m+24,16)||memcmp(image+32,m+40,16)||
       r64(image+48)!=0x1000000ULL||r64(image+56)!=4096||r32(image+64)!=4096||
       !zero(image+68,4092-68)||crc32c(image,4092)!=r32(image+4092))return 0;
    length=r32(m+12);
    if(length!=272 || r32(m+8)!=200 || r32(m+16)!=length ||
        m[0]!='S'||m[1]!='N'||m[2]!='B'||m[3]!='1'||r32(m+4)!=1||
        m[160]!=2||m[161]!=0||m[162]!=2||m[163]!=0||
        memcmp(m+168,trusted_key_id,32)!=0)return 0;
    if(r32(m+20)!=0||zero(m+56,16)||zero(m+72,16)||r64(m+88)<TRUSTED_IMAGE_GENERATION_FLOOR||memcmp(m+96,platform,16)||
       r32(m+112)!=1||r32(m+116)!=1||r32(m+120)!=1||r32(m+124)!=1||r64(m+128)!=0||
       r32(m+136)!=0x10001UL||r32(m+140)!=200||!zero(m+144,16)||r32(m+164)!=0)return 0;
    /* Only the one optional laboratory component descriptor is admitted. */
    if(m[200]!=1||m[201]!=0x7f||m[202]!=2||m[203]!=0||r32(m+204)!=64)return 0;
    d=m+208;payload=r64(d+8);
    if(r64(d)!=8192 || payload==0 || payload>65536 || payload>bytes-8192 ||
        bytes!=((8192+(size_t)payload+7)&~(size_t)7))return 0;
    key.curve=BR_EC_secp384r1;key.q=(unsigned char *)trusted_point;key.qlen=97;
    hash384(m,length,digest);
    if(!br_ecdsa_i31_vrfy_raw(&br_ec_prime_i31,digest,48,&key,image+6144,96))return 0;
    hash384(image+8192,(size_t)payload,digest);
    return memcmp(digest,d+16,48)==0 && zero(image+4096+length,6144-4096-length) &&
        zero(image+6144+96,8192-6144-96) && zero(image+8192+(size_t)payload,bytes-8192-(size_t)payload);
}
