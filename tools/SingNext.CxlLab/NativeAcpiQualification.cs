using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SingNext.CxlLab;

internal static class NativeAcpiQualification
{
    static uint U32(byte[] b,int p)=>BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p));
    static ushort U16(byte[] b,int p)=>BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p));
    static ulong U64(byte[] b,int p)=>BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(p));
    public static bool Verify(LabVm vm,string output,string text,Discovery discovery)
    {
        if(!text.Contains("CXL_NATIVE_ACPI_SELFTEST_PASSED NEGATIVES=10")||!text.Contains("CXL_NATIVE_ACPI_PASS")||text.Contains("CXL_NATIVE_ACPI_FAILURE"))throw new InvalidDataException("Native ACPI discovery did not pass.");
        Match One(string pattern){var m=Regex.Matches(text,pattern,RegexOptions.Multiline);return m.Count==1?m[0]:throw new InvalidDataException("Missing/duplicate native ACPI marker.");}
        ulong Hex(Group g)=>ulong.Parse(g.Value,NumberStyles.HexNumber,CultureInfo.InvariantCulture);
        var table=One(@"^CXL_NATIVE_CEDT ADDR=([0-9a-f]{16}) LEN=([0-9a-f]{16})\s*$");
        ulong address=Hex(table.Groups[1]),length=Hex(table.Groups[2]);
        if(length<36||length>65536||address>ulong.MaxValue-length)throw new InvalidDataException("CEDT range invalid.");
        byte[] raw=new byte[(int)length];var rows=Regex.Matches(text,@"^CEDTRAW ([0-9a-f]{4}) ([0-9a-f]{2,32})\s*$",RegexOptions.Multiline);
        if(rows.Count!=(raw.Length+15)/16)throw new InvalidDataException("CEDT dump incomplete.");
        for(int i=0;i<rows.Count;i++){
            int offset=int.Parse(rows[i].Groups[1].Value,NumberStyles.HexNumber);byte[] part=Convert.FromHexString(rows[i].Groups[2].Value);
            if(offset!=i*16||part.Length!=Math.Min(16,raw.Length-offset))throw new InvalidDataException("CEDT rows outside bounds or out of order.");part.CopyTo(raw,offset);
        }
        File.WriteAllBytes(Path.Combine(output,"native-cedt.bin"),raw);
        if(!vm.ReadBytes(address,raw.Length).AsSpan().SequenceEqual(raw))throw new InvalidDataException("Guest CEDT differs from QMP memory snapshot.");
        if(U32(raw,0)!=0x54444543||U32(raw,4)!=raw.Length||raw[8]!=1||(raw.Sum(x=>(int)x)&255)!=0)throw new InvalidDataException("CEDT header/checksum invalid.");
        var subtables=new List<(int Offset,int Length)>();
        for(int at=36;at<raw.Length;){if(raw.Length-at<4||subtables.Count>=256)throw new InvalidDataException("CEDT subtable header bound.");int n=U16(raw,at+2);if(n<4||n>raw.Length-at)throw new InvalidDataException("CEDT subtable length bound.");subtables.Add((at,n));at+=n;}
        var windows=subtables.Where(x=>raw[x.Offset]==1).ToArray();
        if(windows.Length!=1)throw new InvalidDataException("Single CFMWS profile required.");
        int w=windows[0].Offset;
        if(windows[0].Length!=40||raw[w+1]!=0||U32(raw,w+4)!=0||raw[w+24]!=0||raw[w+25]!=0||U16(raw,w+26)!=0||U32(raw,w+28)>6||(U16(raw,w+32)&0xa)!=0xa||(U16(raw,w+32)&~0x3f)!=0)throw new InvalidDataException("Unsupported CFMWS profile.");
        ulong hpa=U64(raw,w+8),bytes=U64(raw,w+16);uint uid=U32(raw,w+36);
        if(hpa==0||(hpa&0xfffffff)!=0||bytes<0x10000000||(bytes&0xfffffff)!=0||hpa>ulong.MaxValue-bytes)throw new InvalidDataException("CFMWS geometry invalid.");
        var hosts=subtables.Where(x=>raw[x.Offset]==0).ToArray();
        foreach(var h in hosts)if(h.Length!=32||raw[h.Offset+1]!=0||U32(raw,h.Offset+8)!=1||U32(raw,h.Offset+12)!=0)throw new InvalidDataException("Unsupported CHBS profile.");
        var targets=hosts.Where(x=>U32(raw,x.Offset+4)==uid).ToArray();if(targets.Length!=1)throw new InvalidDataException("CHBS target unresolved/ambiguous.");
        int atHost=targets[0].Offset;ulong hostBase=U64(raw,atHost+16),hostBytes=U64(raw,atHost+24);
        if(hostBase==0||(hostBase&65535)!=0||hostBytes<65536||hostBytes>0x1000000||hostBase>ulong.MaxValue-hostBytes)throw new InvalidDataException("CHBS geometry invalid.");
        var nativeWindow=One(@"^CXL_NATIVE_CFMW BASE=([0-9a-f]{16}) SIZE=([0-9a-f]{16})\s*$");
        var nativeHost=One(@"^CXL_NATIVE_CHBS BASE=([0-9a-f]{16}) SIZE=([0-9a-f]{16}) UID=([0-9a-f]{8})\s*$");
        if(Hex(nativeWindow.Groups[1])!=hpa||Hex(nativeWindow.Groups[2])!=bytes||Hex(nativeHost.Groups[1])!=hostBase||Hex(nativeHost.Groups[2])!=hostBytes||Hex(nativeHost.Groups[3])!=uid||hpa!=discovery.Hpa||bytes!=discovery.WindowBytes||hostBase!=discovery.Host.Address)throw new InvalidDataException("Guest ACPI, managed ACPI and QEMU map disagree.");
        bool bi=(U16(raw,w+32)&0x20)!=0;
        var restrictions=One(@"^CXL_NATIVE_CFMW_RESTRICTIONS RAW=([0-9a-f]{4}) POLICY_QUALIFIED=0\s*$");
        if(Hex(restrictions.Groups[1])!=U16(raw,w+32)||bi!=text.Contains("CXL_NATIVE_BI_ADVERTISED_NOT_QUALIFIED"))throw new InvalidDataException("Native CFMWS policy flags disagree with raw table.");
        File.WriteAllText(Path.Combine(output,"native-acpi-verification.json"),JsonSerializer.Serialize(new{CedtAddress=$"0x{address:x}",CedtBytes=length,CfmwBase=$"0x{hpa:x}",WindowBytes=bytes,HostBase=$"0x{hostBase:x}",HostBytes=hostBytes,TargetUid=uid,Restrictions=U16(raw,w+32),BiAdvertised=bi,CfmwAccessPolicyQualified=false,HostOnlyAndBiBothAdvertised=bi&&(U16(raw,w+32)&2)!=0,NativeNegativeCases=10,GuestAcpiMatchesQmp=true,RootChain="EFI ACPI2 -> checksummed RSDP -> XSDT -> CEDT; memory map ACPI bounds",GuestHdmProgrammingByEfiVerified=false},LabVm.JsonOptions));
        return bi;
    }
}
