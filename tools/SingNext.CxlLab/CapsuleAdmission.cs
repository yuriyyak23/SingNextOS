using System.Security.Cryptography;

namespace SingNext.CxlLab;

// Host-qualified composition seam, not a guest executable or production boot ABI.
internal interface ICapsuleSource
{
    ulong Capacity { get; }
    (ulong Reset, ulong Mapping) Observe();
    int Read(ulong offset, Span<byte> destination);
}

internal static class CapsuleAdmission
{
    public static CapsuleEvidence Verify(ICapsuleSource source, ulong offset, int bytes,
        string provisionedPublicKey, ReadOnlySpan<byte> provisionedSpkiSha256,ulong minimumImageGeneration=1)
    {
        ArgumentNullException.ThrowIfNull(source);
        if(bytes < LabCapsule.PayloadOffset+1 || bytes > LabCapsule.PayloadOffset+LabCapsule.MaxPayloadBytes+7)
            throw new InvalidDataException("Untrusted image length outside lab bounds.");
        Qualification.ValidateCapsuleRange(offset,(ulong)bytes,source.Capacity);
        using var key=ECDsa.Create(); key.ImportFromPem(provisionedPublicKey);
        if(key.KeySize!=384 || provisionedSpkiSha256.Length!=32 ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(key.ExportSubjectPublicKeyInfo()),provisionedSpkiSha256))
            throw new InvalidDataException("Provisioned trust anchor mismatch.");
        var generation=source.Observe();
        if(generation.Reset==0 || generation.Mapping==0)throw new InvalidDataException("Missing reset/mapping generation.");
        byte[] snapshot=new byte[bytes];
        try
        {
            // Never authenticate directly from mutable CXL memory. Only the captured bytes
            // are verified/consumed; this does not claim the source remained immutable.
            for(int position=0;position<bytes;)
            {
                if(source.Observe()!=generation)throw new InvalidDataException("Source generation changed before read.");
                int count=Math.Min(4096,bytes-position);
                int read=source.Read(checked(offset+(ulong)position),snapshot.AsSpan(position,count));
                if(read!=count)throw new InvalidDataException("Incomplete capsule read.");
                if(source.Observe()!=generation)throw new InvalidDataException("Source generation changed during read.");
                position+=count;
            }
            var evidence=LabCapsule.Verify(snapshot,provisionedPublicKey,minimumImageGeneration);
            if(source.Observe()!=generation)throw new InvalidDataException("Source generation changed during verification.");
            return evidence;
        }
        finally { CryptographicOperations.ZeroMemory(snapshot); }
    }

    internal static void SelfTest()
    {
        var capsule=LabCapsule.Create(LabCapsule.Fixture());
        using var key=ECDsa.Create();key.ImportFromPem(capsule.PublicKey);
        byte[] pin=SHA256.HashData(key.ExportSubjectPublicKeyInfo());
        Verify(new TestSource(capsule.Bytes),0,capsule.Bytes.Length,capsule.PublicKey,pin);
        int rejected=0;
        foreach(string mode in new[]{"short","reset","mapping","corrupt","read-throw"})
        {
            try{Verify(new TestSource(capsule.Bytes,mode),0,capsule.Bytes.Length,capsule.PublicKey,pin);}
            catch(Exception e) when(e is IOException or InvalidDataException){rejected++;}
        }
        byte[] wrongPin=(byte[])pin.Clone();wrongPin[0]^=1;
        try{Verify(new TestSource(capsule.Bytes),0,capsule.Bytes.Length,capsule.PublicKey,wrongPin);}catch(InvalidDataException){rejected++;}
        try{Verify(new TestSource(capsule.Bytes),ulong.MaxValue,capsule.Bytes.Length,capsule.PublicKey,pin);}catch(InvalidDataException){rejected++;}
        try{Verify(new TestSource(capsule.Bytes),0,capsule.Bytes.Length,capsule.PublicKey,pin,2);}catch(InvalidDataException){rejected++;}
        if(rejected!=8)throw new Exception($"Capsule admission failed ({rejected}/8 rejections).");
        Console.WriteLine("Host capsule admission: pinned anchor + bounded snapshot; 8 failure cases rejected. Guest verification not proven.");
    }

    private sealed class TestSource(byte[] image,string mode="valid"):ICapsuleSource
    {
        int reads;
        public ulong Capacity=>(ulong)image.Length;
        public (ulong Reset,ulong Mapping) Observe()=>
            (mode=="reset"&&reads>0?2UL:1UL,mode=="mapping"&&reads>0?2UL:1UL);
        public int Read(ulong offset,Span<byte> destination)
        {
            reads++;
            if(mode=="read-throw")throw new IOException("Injected read failure.");
            image.AsSpan(checked((int)offset),destination.Length).CopyTo(destination);
            if(mode=="corrupt")destination[0]^=1;
            return mode=="short"?destination.Length-1:destination.Length;
        }
    }
}
