#ifndef CXL_STDDEF_H
#define CXL_STDDEF_H
typedef unsigned __int64 size_t;
typedef __int64 ptrdiff_t;
#define NULL ((void *)0)
#define offsetof(T,M) ((size_t)&(((T *)0)->M))
#endif
