using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SingNext.Boot.Core;
using SingNext.Boot.Capsule;
using YAKSys_Hybrid_CPU.Boot.Contracts;

namespace SingNext.CxlLab;

// Lab-only transport layout. Existing HBV1/SNB1 codecs and verifier remain authoritative.
// This does not introduce a production capsule ABI or an executable kernel format.
internal static class LabCapsule
{
    public const int ManifestOffset = 4096, SignatureOffset = 6144, PayloadOffset = 8192;
    public const int MaxPayloadBytes = 64 * 1024; // keep serial guest qualification bounded
    public const ulong VolumeBytes = 16 * 1024 * 1024;
    public static readonly Guid Platform = Guid.Parse("fba12e33-2294-4431-96bc-6d635b440001");
    public static readonly Guid KeySet = Guid.Parse("fba12e33-2294-4431-96bc-6d635b440002");
    private const ushort ComponentTlv = 0x7f01; // optional laboratory descriptor, NOT a production component table

    public static (byte[] Bytes, string PublicKey) Create(byte[] payload,ECDsa? signingKey=null,ulong generation=1)
    {
        if (payload.Length == 0 || payload.Length > MaxPayloadBytes) throw new InvalidDataException("Payload must be 1..65536 bytes for this laboratory transport.");
        if(generation==0)throw new InvalidDataException("Image generation must be nonzero.");
        using ECDsa? generated=signingKey is null?ECDsa.Create(ECCurve.NamedCurves.nistP384):null;
        ECDsa key=signingKey??generated!;
        if(key.ExportParameters(false).Curve.Oid.Value!="1.3.132.0.34")throw new InvalidDataException("Signing key must be NIST P384.");
        byte[] descriptor = new byte[64];
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor, PayloadOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor.AsSpan(8), (ulong)payload.Length);
        SHA384.HashData(payload).CopyTo(descriptor, 16);
        Guid volume = Guid.NewGuid(), replica = Guid.NewGuid();
        SingNextBootManifestV1 manifest = new(volume, replica, Guid.NewGuid(), Guid.NewGuid(), generation, Platform,
            1, 1, 1, 1, 0, 1, 1, BootManifestCodec.HeaderSize, 0, 0, 0,
            BootHashAlgorithm.Sha384, BootSignatureAlgorithm.EcdsaP384, 0,
            SHA256.HashData(key.ExportSubjectPublicKeyInfo()), 0,
            [new(ComponentTlv, BootRecordFlags.EvidenceOnly, descriptor)]);
        byte[] signed = BootManifestCodec.Encode(manifest);
        byte[] signature = key.SignData(signed, HashAlgorithmName.SHA384, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        byte[] image = new byte[(PayloadOffset + payload.Length + 7) & ~7];
        BootVolumeHeaderCodec.Encode(new(1, volume, replica, VolumeBytes, ManifestOffset, 4096, 0, 0, 0, 0, 0, 0, 0)).CopyTo(image, 0);
        signed.CopyTo(image, ManifestOffset); signature.CopyTo(image, SignatureOffset); payload.CopyTo(image, PayloadOffset);
        Verify(image, key.ExportSubjectPublicKeyInfoPem());
        return (image, key.ExportSubjectPublicKeyInfoPem());
    }

    public static CapsuleEvidence Verify(byte[] image, string publicKey,ulong rollbackFloor=1)
    {
        if (image.Length < PayloadOffset + 1 || image.Length > PayloadOffset + MaxPayloadBytes + 7) throw new InvalidDataException("Lab image length outside bounds.");
        var volume = BootVolumeHeaderCodec.Parse(image, VolumeBytes);
        if (!volume.IsSuccess) throw new InvalidDataException("HBV1: " + volume.Detail);
        var header = volume.Value!;
        if (header.ManifestAOffset != ManifestOffset || header.ManifestAMaxBytes != 4096 || header.ManifestBOffset != 0 || header.RecoveryManifestOffset != 0) throw new InvalidDataException("Unexpected lab volume layout.");
        int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(ManifestOffset + 12)));
        if (length < BootManifestCodec.HeaderSize || length > SignatureOffset - ManifestOffset) throw new InvalidDataException("Manifest bounds.");
        using ECDsa key = ECDsa.Create(); key.ImportFromPem(publicKey);
        var policy = new HybridBootPolicyV1(1, Platform, KeySet, Guid.Empty, BootPolicyFlags.AllowDevelopmentImages, []);
        var trust = new BootTrustContext(KeySet, 1, BootTrustRole.Development, false, true);
        if(rollbackFloor==0)throw new InvalidDataException("Rollback floor must be nonzero.");
        var verified = BootManifestVerifier.ParseAndVerify(image.AsSpan(ManifestOffset, length), image.AsSpan(SignatureOffset, 96), 0, 1, 1, Platform, rollbackFloor, policy, trust, new DevelopmentTrust(key));
        if (!verified.IsSuccess) throw new InvalidDataException("Signed SNB1: " + verified.Detail);
        var manifest = verified.Value!;
        if (manifest.BootVolumeId != header.BootVolumeId || manifest.ReplicaId != header.ReplicaId || manifest.ComponentCount != 1 || manifest.ComponentTableOffset != BootManifestCodec.HeaderSize || manifest.Extensions.Count != 1) throw new InvalidDataException("Identity/component layout mismatch.");
        var tlv = manifest.Extensions.Single();
        if (tlv.Type != ComponentTlv || tlv.Flags != BootRecordFlags.EvidenceOnly || tlv.Value.Length != 64) throw new InvalidDataException("Unsupported lab descriptor.");
        var desc = tlv.Value.Span;
        ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(desc), count = BinaryPrimitives.ReadUInt64LittleEndian(desc[8..]);
        if (offset != PayloadOffset || count == 0 || count > MaxPayloadBytes || count > (ulong)image.Length - offset || image.Length != ((PayloadOffset + (int)count + 7) & ~7)) throw new InvalidDataException("Component range mismatch.");
        byte[] payload = image.AsSpan(PayloadOffset, (int)count).ToArray();
        if (!CryptographicOperations.FixedTimeEquals(SHA384.HashData(payload), desc[16..])) throw new InvalidDataException("Payload SHA-384 mismatch.");
        // All unused transport bytes must be canonical zero so a whole-image round-trip is meaningful.
        byte[] canonical = new byte[image.Length]; image.AsSpan(0,4096).CopyTo(canonical); image.AsSpan(ManifestOffset,length).CopyTo(canonical.AsSpan(ManifestOffset)); image.AsSpan(SignatureOffset,96).CopyTo(canonical.AsSpan(SignatureOffset)); payload.CopyTo(canonical,PayloadOffset);
        if (!canonical.AsSpan().SequenceEqual(image)) throw new InvalidDataException("Noncanonical lab transport padding.");
        byte[] ram = new byte[payload.Length], bootInfo = new byte[HybridBootInfoCodec.HeaderSize], scratch = new byte[48];
        HybridBootInfoV1 info = new(0, Platform, 1, 1, ResetReason.ColdPowerOn, 1, []);
        var prepared = DirectBootCapsule.PrepareKernel(1, 1, new(1, 1, 0, VolumeBytes, 0, (ulong)image.Length), payload, desc[16..], ram, info, bootInfo, scratch);
        if (!prepared.IsSuccess || !ram.AsSpan().SequenceEqual(payload) || !HybridBootInfoCodec.Parse(bootInfo).IsSuccess) throw new InvalidDataException("Host PrepareKernel contract failed.");
        return new(image.Length, payload.Length, Convert.ToHexString(SHA384.HashData(image)), Convert.ToHexString(SHA384.HashData(payload)), true, true, true, false);
    }

    public static byte[] Fixture()
    {
        byte[] b = new byte[256]; Encoding.ASCII.GetBytes("CXL qualification DATA ONLY; not executable RuntimeKernel.").CopyTo(b,0);
        for(int i=64;i<b.Length;i++) b[i]=(byte)(i*37+11);
        return b;
    }
    public static void SelfTest()
    {
        var (bytes,key)=Create(Fixture()); Verify(bytes,key);
        int rejected=0;
        foreach(int offset in new[]{0,ManifestOffset+88,SignatureOffset,PayloadOffset,SignatureOffset+120})
        {
            byte[] copy=(byte[])bytes.Clone(); copy[offset]^=0x80;
            try { Verify(copy,key); } catch(InvalidDataException) { rejected++; }
        }
        using ECDsa wrong = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        try { Verify(bytes,wrong.ExportSubjectPublicKeyInfoPem()); } catch(InvalidDataException) { rejected++; }
        try { Verify(bytes[..^1],key); } catch(InvalidDataException) { rejected++; }
        if(rejected!=7) throw new Exception("Negative capsule tests failed.");
        Console.WriteLine("Capsule self-test: valid round-trip + 7 rejection cases passed.");
        using var stable=ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var first=Create(Fixture(),stable,3);var second=Create(Fixture(),stable,4);
        if(first.PublicKey!=second.PublicKey)throw new Exception("Reusable signing key changed.");
        Verify(first.Bytes,first.PublicKey,2);Verify(second.Bytes,first.PublicKey,2);
        var stale=Create(Fixture(),stable,1);bool denied=false;
        try{Verify(stale.Bytes,first.PublicKey,2);}catch(InvalidDataException){denied=true;}
        if(!denied)throw new Exception("Host accepted signed capsule below generation floor.");
        Console.WriteLine("Reusable signer and static generation floor host checks passed; durable anti-rollback not proven.");
    }
    private sealed class DevelopmentTrust(ECDsa key) : IBootTrustPolicyVerifier
    {
        public bool VerifyAuthorized(BootTrustVerificationRequest r, ReadOnlySpan<byte> keyId, ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> signature) =>
            r.PolicyKeySetId==KeySet && r.TrustEpoch==1 && r.RequiredRole==BootTrustRole.Development && !r.ProductionLocked && r.HardwareDevelopmentEnabled && r.Algorithm==BootSignatureAlgorithm.EcdsaP384 &&
            CryptographicOperations.FixedTimeEquals(keyId,SHA256.HashData(key.ExportSubjectPublicKeyInfo())) && key.VerifyData(bytes,signature,HashAlgorithmName.SHA384,DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }
}
internal sealed record CapsuleEvidence(int ImageBytes,int PayloadBytes,string ImageSha384,string PayloadSha384,bool ManifestSignatureVerified,bool PayloadHashVerified,bool HostPrepareKernelContractVerified,bool RuntimeKernelExecuted);
