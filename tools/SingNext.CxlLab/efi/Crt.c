#include <string.h>
#pragma function(memcpy,memset,memcmp,memmove)
void *memcpy(void *d,const void *s,size_t n){size_t i;for(i=0;i<n;i++)((unsigned char *)d)[i]=((const unsigned char *)s)[i];return d;}
void *memmove(void *d,const void *s,size_t n){size_t i;if((size_t)d<(size_t)s)return memcpy(d,s,n);for(i=n;i>0;i--)((unsigned char *)d)[i-1]=((const unsigned char *)s)[i-1];return d;}
void *memset(void *d,int v,size_t n){size_t i;for(i=0;i<n;i++)((unsigned char *)d)[i]=(unsigned char)v;return d;}
int memcmp(const void *a,const void *b,size_t n){size_t i;for(i=0;i<n;i++){int d=((const unsigned char *)a)[i]-((const unsigned char *)b)[i];if(d)return d;}return 0;}
