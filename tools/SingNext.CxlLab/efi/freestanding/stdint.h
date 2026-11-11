#ifndef CXL_STDINT_H
#define CXL_STDINT_H
typedef signed char int8_t; typedef unsigned char uint8_t;
typedef short int16_t; typedef unsigned short uint16_t;
typedef int int32_t; typedef unsigned int uint32_t;
typedef __int64 int64_t; typedef unsigned __int64 uint64_t;
typedef __int64 intptr_t; typedef unsigned __int64 uintptr_t;
#define UINT32_MAX 0xffffffffU
#define UINT64_MAX 0xffffffffffffffffULL
#define INT32_MAX 0x7fffffff
#define INT32_MIN (-2147483647-1)
#define UINT32_C(x) x##U
#define UINT64_C(x) x##ULL
#define INT64_C(x) x##LL
#endif
