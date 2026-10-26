using System.Buffers.Binary;
using System.Text;

namespace SingNext.CxlLab;

internal static class FatImage
{
    // 16 MiB FAT16 superfloppy, no MBR/boot code. Explicit opt-in may add a test EFI file.
    public static void Create(string path, IReadOnlyDictionary<string,byte[]> files)
    {
        byte[] image=new byte[16*1024*1024], fat=new byte[128*512];
        image[0]=0xeb;image[1]=0x3c;image[2]=0x90;Encoding.ASCII.GetBytes("CXLLAB  ").CopyTo(image,3);
        U16(image,11,512);image[13]=1;U16(image,14,1);image[16]=2;U16(image,17,512);U16(image,19,32768);image[21]=0xf8;U16(image,22,128);U16(image,24,32);U16(image,26,64);
        image[36]=0x80;image[38]=0x29;Encoding.ASCII.GetBytes("CXL LAB    ").CopyTo(image,43);Encoding.ASCII.GetBytes("FAT16   ").CopyTo(image,54);image[510]=0x55;image[511]=0xaa;
        U16(fat,0,0xfff8);U16(fat,2,0xffff);int cluster=2,entry=257*512;
        foreach(var (name,bytes) in files)
        {
            string[] parts=name.ToUpperInvariant().Split('.');if(parts.Length!=2||parts[0].Length>8||parts[1].Length>3||!parts.All(x=>x.All(char.IsAsciiLetterOrDigit)))throw new ArgumentException("Use 8.3 filenames.");
            Encoding.ASCII.GetBytes(parts[0].PadRight(8)+parts[1].PadRight(3)).CopyTo(image,entry);image[entry+11]=0x20;U16(image,entry+26,(ushort)cluster);BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(entry+28),(uint)bytes.Length);
            int count=Math.Max(1,(bytes.Length+511)/512);if(cluster+count>32000)throw new ArgumentException("FAT data exceeds lab limit.");
            for(int i=0;i<count;i++)U16(fat,(cluster+i)*2,(ushort)(i+1<count?cluster+i+1:0xffff));
            bytes.CopyTo(image,(289+cluster-2)*512);cluster+=count;entry+=32;
        }
        fat.CopyTo(image,512);fat.CopyTo(image,129*512);
        using var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);output.Write(image);output.Flush(true);
    }
    static void U16(byte[] b,int o,ushort v)=>BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o),v);
}
