#ifndef CXL_UEFI_LAB_H
#define CXL_UEFI_LAB_H
typedef unsigned char U8;
typedef unsigned short U16;
typedef unsigned int U32;
typedef unsigned __int64 U64;
typedef U64 STATUS;
typedef struct { U64 Signature; U32 Revision, HeaderSize, Crc32, Reserved; } HEADER;
typedef struct TEXT TEXT;
struct TEXT { void *Reset; STATUS (__cdecl *OutputString)(TEXT *, const U16 *); };
typedef struct {U16 Scan,Character;} INPUT_KEY;
typedef struct INPUT INPUT;
struct INPUT {void *Reset;STATUS (__cdecl *ReadKeyStroke)(INPUT *,INPUT_KEY *);void *WaitForKey;};
typedef struct {HEADER Hdr;void *Functions[44];} BOOT;
typedef struct {
    HEADER Hdr; U16 *Vendor; U32 FirmwareRevision; void *ConsoleInHandle;
    INPUT *ConIn; void *ConsoleOutHandle; TEXT *ConOut;
    void *StandardErrorHandle; TEXT *StdErr; void *RuntimeServices; BOOT *BootServices;
    U64 NumberOfTableEntries; void *ConfigurationTable;
} SYSTEM;
#endif
