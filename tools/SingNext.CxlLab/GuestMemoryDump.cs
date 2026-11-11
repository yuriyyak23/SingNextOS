using System.Globalization;
using System.Text.RegularExpressions;

namespace SingNext.CxlLab;

internal static class GuestMemoryDump
{
    private static readonly Regex Header=new(@"^\s*Memory Address ([0-9A-Fa-f]{16}) ([0-9A-Fa-f]+) Bytes\s*$");
    private static readonly Regex Row=new(@"^\s*([0-9A-Fa-f]{8}|[0-9A-Fa-f]{16}):\s+(.*?)\s+\*.*\*\s*$");
    public static byte[] Parse(string text,ulong address,int length)
    {
        if(length is <1 or >1048576 || address>ulong.MaxValue-(uint)length) throw new InvalidDataException("Dump range exceeds bounds.");
        string[] lines=text.Replace("\r","").Split('\n');
        int[] begins=Indexes(lines,"CXL_IMAGE_BEGIN"),ends=Indexes(lines,"CXL_IMAGE_END");
        if(begins.Length!=0||ends.Length!=0) {
            if(begins.Length!=1||ends.Length!=1||begins[0]>=ends[0]) throw new InvalidDataException("Invalid guest dump framing.");
            lines=lines[(begins[0]+1)..ends[0]];
        }
        var headers=lines.Select((line,index)=>(Match:Header.Match(line),Index:index)).Where(x=>x.Match.Success).ToArray();
        if(headers.Length!=1) throw new InvalidDataException("Expected one full-HPA dmem header.");
        var header=headers[0];
        if(ulong.Parse(header.Match.Groups[1].Value,NumberStyles.HexNumber)!=address||ulong.Parse(header.Match.Groups[2].Value,NumberStyles.HexNumber)!=(ulong)length)
            throw new InvalidDataException("Dump header does not match requested HPA/size.");
        if(lines.Take(header.Index).Any(x=>Row.IsMatch(x)))throw new InvalidDataException("Memory rows precede the header.");
        byte[] output=new byte[length];int cursor=0;
        foreach(string line in lines[(header.Index+1)..]) {
            if(string.IsNullOrWhiteSpace(line)||line.TrimStart().StartsWith("Shell>",StringComparison.Ordinal)) {
                if(cursor<length&&!string.IsNullOrWhiteSpace(line))throw new InvalidDataException("Prompt before dump completion.");
                continue;
            }
            var row=Row.Match(line);if(!row.Success)throw new InvalidDataException("Unexpected text in guest memory dump.");
            ulong rowAddress=ulong.Parse(row.Groups[1].Value,NumberStyles.HexNumber);
            if(row.Groups[1].Value.Length==8 && address>uint.MaxValue) {
                if((address&uint.MaxValue)+(uint)length>1UL<<32)throw new InvalidDataException("Ambiguous low-32-bit dmem labels.");
                rowAddress|=address&0xffffffff00000000UL;
            }
            if(cursor>=length||rowAddress!=address+(uint)cursor)throw new InvalidDataException("Out-of-order, duplicate or out-of-range dump row.");
            string[] tokens=row.Groups[2].Value.Replace('-',' ').Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
            int expected=Math.Min(16,length-cursor);
            if(tokens.Length!=expected||tokens.Any(x=>!Regex.IsMatch(x,@"^[0-9A-Fa-f]{2}$")))throw new InvalidDataException("Dump row has invalid byte count or content.");
            foreach(string token in tokens)output[cursor++]=byte.Parse(token,NumberStyles.HexNumber);
        }
        if(cursor!=length)throw new InvalidDataException($"Incomplete guest dump ({cursor}/{length}).");
        return output;
    }
    private static int[] Indexes(string[] lines,string marker)=>lines.Select((line,index)=>(line,index)).Where(x=>x.line.Trim()==marker).Select(x=>x.index).ToArray();
    internal static void SelfTest()
    {
        const ulong hpa=0x190000000;
        const string row="  90000000: 00 01 02 03 04 05 06 07-08 09 0A 0B 0C 0D 0E 0F  *................*";
        string header=$"Memory Address {hpa:X16} 10 Bytes\n";
        byte[] expected=Enumerable.Range(0,16).Select(x=>(byte)x).ToArray();
        foreach(string good in new[]{header+row,header+row.Replace("90000000","0000000190000000"),"CXL_IMAGE_BEGIN\n"+header+row+"\nCXL_IMAGE_END"})
            if(!Parse(good,hpa,16).SequenceEqual(expected))throw new Exception("Valid dump parser fixture failed.");
        string[] bad=[row,header+row+"\n"+row,header+row.Replace(" 0F "," xx "),header+row.Replace("90000000","90001000"),
            header.Replace(" 10 Bytes"," 11 Bytes")+row,header.Replace("0000000190000000","0000000290000000")+row,
            header+row.Replace(" 0F "," "),header+row.Replace(" 0F "," 0F AA "),header+row+"\n"+header,
            header+row+"\n"+row.Replace("90000000","90000010"),header+row.Replace("*................*","not-a-row"),
            "CXL_IMAGE_BEGIN\n"+header+row,"CXL_IMAGE_BEGIN\nCXL_IMAGE_BEGIN\n"+header+row+"\nCXL_IMAGE_END",
            header+"random log text\n"+row,header+row.Replace("90000000","0000000290000000"),
            $"Memory Address {hpa:X16} 20 Bytes\n"+row.Replace("90000000","90000010")+"\n"+row];
        int rejected=0;foreach(string input in bad)try{Parse(input,hpa,16);}catch(InvalidDataException){rejected++;}
        if(rejected!=bad.Length)throw new Exception($"Strict dump rejection tests failed ({rejected}/{bad.Length}).");
        Console.WriteLine($"Strict dmem parser: 3 valid profiles and {rejected} rejection cases passed.");
    }
}
