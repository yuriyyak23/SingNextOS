/* Public verification vectors: RFC 6979 A.2.6, SHA-384, sample and test.
 * https://www.rfc-editor.org/rfc/rfc6979.html#appendix-A.2.6
 * No private key or signing nonce is included. */
#include "bearssl.h"
#include <string.h>
static unsigned char nibble(char c){return (unsigned char)(c<='9'?c-'0':c-'A'+10);}
static void unhex(unsigned char *out,const char *hex,size_t n){size_t i;for(i=0;i<n;i++)out[i]=(unsigned char)((nibble(hex[2*i])<<4)|nibble(hex[2*i+1]));}
int P384KnownAnswer(void)
{
    static const char *point="04EC3A4E415B4E19A4568618029F427FA5DA9A8BC4AE92E02E06AAE5286B300C64DEF8F0EA9055866064A254515480BC138015D9B72D7D57244EA8EF9AC0C621896708A59367F9DFB9F54CA84B3F1C9DB1288B231C3AE0D4FE7344FD2533264720";
    static const char *signatures[2]={
        "94EDBB92A5ECB8AAD4736E56C691916B3F88140666CE9FA73D64C4EA95AD133C81A648152E44ACF96E36DD1E80FABE4699EF4AEB15F178CEA1FE40DB2603138F130E740A19624526203B6351D0A3A94FA329C145786E679E7B82C71A38628AC8",
        "8203B63D3C853E8D77227FB377BCF7B7B772E97892A80F36AB775D509D7A5FEB0542A7F0812998DA8F1DD3CA3CF023DBDDD0760448D42D8A43AF45AF836FCE4DE8BE06B485E9B61B827C2F13173923E06A739F040649A667BF3B828246BAA5A5"};
    static const char *messages[2]={"sample","test"};
    static const size_t lengths[2]={6,4};
    static const char *order="FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFC7634D81F4372DDF581A0DB248B0A77AECEC196ACCC52973";
    unsigned char q[97],sig[96],bad[96],digest[48],invalid[97];
    br_ec_public_key key; br_sha384_context hash;size_t i;
    unhex(q,point,97);key.curve=BR_EC_secp384r1;key.q=q;key.qlen=97;
    for(i=0;i<2;i++){
        unhex(sig,signatures[i],96);br_sha384_init(&hash);br_sha384_update(&hash,messages[i],lengths[i]);br_sha384_out(&hash,digest);
        if(!br_ecdsa_i31_vrfy_raw(&br_ec_prime_i31,digest,48,&key,sig,96))return 0;
    }
    digest[0]^=1;if(br_ecdsa_i31_vrfy_raw(&br_ec_prime_i31,digest,48,&key,sig,96))return 0;digest[0]^=1;
    if(br_ecdsa_i31_vrfy_raw(&br_ec_prime_i31,digest,48,&key,sig,95))return 0;
    memcpy(bad,sig,96);memset(bad,0,48);if(br_ecdsa_i31_vrfy_raw(&br_ec_prime_i31,digest,48,&key,bad,96))return 0;
    memcpy(bad,sig,96);memset(bad+48,0,48);if(br_ecdsa_i31_vrfy_raw(&br_ec_prime_i31,digest,48,&key,bad,96))return 0;
    memcpy(bad,sig,96);unhex(bad,order,48);if(br_ecdsa_i31_vrfy_raw(&br_ec_prime_i31,digest,48,&key,bad,96))return 0;
    memcpy(bad,sig,96);unhex(bad+48,order,48);if(br_ecdsa_i31_vrfy_raw(&br_ec_prime_i31,digest,48,&key,bad,96))return 0;
    memset(invalid,0,97);invalid[0]=4;key.q=invalid;
    if(br_ecdsa_i31_vrfy_raw(&br_ec_prime_i31,digest,48,&key,sig,96))return 0;
    key.q=q;key.qlen=96;if(br_ecdsa_i31_vrfy_raw(&br_ec_prime_i31,digest,48,&key,sig,96))return 0;
    key.qlen=97;q[0]=0;if(br_ecdsa_i31_vrfy_raw(&br_ec_prime_i31,digest,48,&key,sig,96))return 0;
    return 1;
}
