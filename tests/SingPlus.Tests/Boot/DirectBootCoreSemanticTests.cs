using System.Buffers.Binary;
using System.Security.Cryptography;
using SingNext.Boot.Core;
using YAKSys_Hybrid_CPU.Boot.Contracts;

namespace SingPlus.Tests.Boot;

public sealed class DirectBootCoreSemanticTests
{
    private readonly Guid _volume = Guid.NewGuid();
    private readonly Guid _domain = Guid.NewGuid();
    private readonly Guid _image = Guid.NewGuid();

    [Fact]
    public void P15_03_SelectionIsPermutationStableAndPrefersConfiguredDsn()
    {
        var a = Candidate(Guid.Parse("20000000-0000-0000-0000-000000000000"), 7, 1);
        var b = Candidate(Guid.Parse("10000000-0000-0000-0000-000000000000"), 7, 2);
        var policy = Policy() with { PreferredDsn = 2 };

        var first = BootCandidateSelector.Select(policy, [a, b]);
        var second = BootCandidateSelector.Select(policy, [b, a]);

        Assert.True(first.IsSuccess);
        Assert.Equal(b.ReplicaId, first.Value!.ReplicaId);
        Assert.Equal(first.Value, second.Value);
    }

    [Fact]
    public void P15_03_SelectionRejectsRollbackAndSplitBrain()
    {
        var stale = Candidate(Guid.NewGuid(), 3, 1);
        Assert.Equal(BootFailure.RollbackRejected, BootCandidateSelector.Select(Policy() with { ImageRollbackFloor = 4 }, [stale]).Failure);

        var conflict = Candidate(Guid.NewGuid(), 3, 2) with { ImageId = Guid.NewGuid() };
        Assert.Equal(BootFailure.AmbiguousState, BootCandidateSelector.Select(Policy(), [stale, conflict]).Failure);
    }

    [Fact]
    public void P15_03_DuplicateVolumeIsEitherIdenticalOrAmbiguous()
    {
        var candidate = Candidate(Guid.NewGuid(), 5, 1);
        Assert.True(BootCandidateSelector.Select(Policy(), [candidate, candidate]).IsSuccess);

        var conflictingDigest = candidate with { SignedDigest = new byte[] { 9, 9, 9 } };
        Assert.Equal(BootFailure.AmbiguousState,
            BootCandidateSelector.Select(Policy(), [candidate, conflictingDigest]).Failure);
    }

    [Fact]
    public void P15_03_LoaderHashesDestinationAndClearsItOnMismatch()
    {
        var source = Enumerable.Range(0, 64).Select(static x => (byte)x).ToArray();
        var destination = new byte[source.Length];
        Assert.Equal(BootFailure.None, VerifiedImageLoader.CopyAndVerifyDestinationWithStackScratch(source, destination, SHA384.HashData(source)));
        Assert.Equal(source, destination);

        Array.Fill(destination, (byte)0xaa);
        Assert.Equal(BootFailure.HashMismatch, VerifiedImageLoader.CopyAndVerifyDestinationWithStackScratch(source, destination, new byte[48]));
        Assert.All(destination, static value => Assert.Equal(0, value));
    }

    [Fact]
    public void P15_03_LoaderRejectsSourceDestinationOverlap()
    {
        var shared = Enumerable.Range(0, 64).Select(static x => (byte)x).ToArray();
        Assert.Equal(BootFailure.BoundsViolation,
            VerifiedImageLoader.CopyAndVerifyDestinationWithStackScratch(shared, shared, SHA384.HashData(shared)));
    }

    [Fact]
    public void P15_03_ManifestRequiresSignatureAbiPlatformAndRollback()
    {
        var platform = Guid.NewGuid();
        var keySet = Guid.NewGuid();
        var policy = BootPolicy(platform, keySet);
        var trust = new BootTrustContext(keySet, 7, BootTrustRole.Production, true, false);
        var manifest = new SingNextBootManifestV1(_volume, Guid.NewGuid(), _image, _domain, 5, platform,
            1, 2, 1, 2, 0, 1, 1, 0, 0, 0, 0,
            BootHashAlgorithm.Sha384, BootSignatureAlgorithm.Ed25519, 0, new byte[32], 0, []);
        var bytes = BootManifestCodec.Encode(manifest);

        Assert.True(BootManifestVerifier.ParseAndVerify(bytes, [1], 0, 1, 1, platform, 5, policy, trust, new TrustVerifier(keySet, 7)).IsSuccess);
        Assert.Equal(BootFailure.RollbackRejected, BootManifestVerifier.ParseAndVerify(bytes, [1], 0, 1, 1, platform, 6, policy, trust, new TrustVerifier(keySet, 7)).Failure);
        var otherPlatform = Guid.NewGuid();
        Assert.Equal(BootFailure.SecurityPolicyDenied, BootManifestVerifier.ParseAndVerify(bytes, [1], 0, 1, 1, otherPlatform, 0,
            BootPolicy(otherPlatform, keySet), trust, new TrustVerifier(keySet, 7)).Failure);
        Assert.Equal(BootFailure.SecurityPolicyDenied, BootManifestVerifier.ParseAndVerify(bytes, [1], 0, 1, 1, platform, 0,
            policy, trust, new TrustVerifier(keySet, 7, reject: true)).Failure);

        var unsupportedHash = manifest with { HashAlgorithm = BootHashAlgorithm.Sha256 };
        Assert.Equal(BootFailure.SecurityPolicyDenied,
            BootManifestVerifier.ParseAndVerify(BootManifestCodec.Encode(unsupportedHash), [1], 0, 1, 1, platform, 0, policy, trust, new TrustVerifier(keySet, 7)).Failure);
        var invalidIndex = manifest with { KernelComponentIndex = 1 };
        Assert.Equal(BootFailure.SecurityPolicyDenied,
            BootManifestVerifier.ParseAndVerify(BootManifestCodec.Encode(invalidIndex), [1], 0, 1, 1, platform, 0, policy, trust, new TrustVerifier(keySet, 7)).Failure);

        bytes[0] ^= 0xff;
        Assert.Equal(BootFailure.Malformed,
            BootManifestVerifier.ParseAndVerify(bytes, [1], 0, 1, 1, platform, 0, policy, trust, new TrustVerifier(keySet, 7)).Failure);
    }

    [Fact]
    public void P15_03_TrustPolicyAndCanonicalSignedRegionFailClosed()
    {
        var platform = Guid.NewGuid();
        var keySet = Guid.NewGuid();
        var policy = BootPolicy(platform, keySet);
        var manifest = new SingNextBootManifestV1(_volume, Guid.NewGuid(), _image, _domain, 5, platform,
            1, 2, 1, 2, 0, 1, 1, 0, 0, 0, 0,
            BootHashAlgorithm.Sha384, BootSignatureAlgorithm.Ed25519, 0, new byte[32], 0, []);
        var bytes = BootManifestCodec.Encode(manifest);
        var production = new BootTrustContext(keySet, 7, BootTrustRole.Production, true, false);

        Assert.Equal(BootFailure.SecurityPolicyDenied, Verify(bytes, policy, production with { ActiveKeySetId = Guid.NewGuid() }, new TrustVerifier(keySet, 7)).Failure);
        Assert.Equal(BootFailure.SecurityPolicyDenied, Verify(bytes, policy, production with { ActiveTrustEpoch = 6 }, new TrustVerifier(keySet, 7)).Failure);
        Assert.Equal(BootFailure.SecurityPolicyDenied, Verify(bytes, policy,
            production with { RequiredRole = BootTrustRole.Development, HardwareDevelopmentEnabled = true }, new TrustVerifier(keySet, 7)).Failure);
        Assert.Equal(BootFailure.SecurityPolicyDenied, Verify(bytes, policy,
            production with { ProductionLocked = false, RequiredRole = BootTrustRole.Development }, new TrustVerifier(keySet, 7)).Failure);
        Assert.Equal(BootFailure.SecurityPolicyDenied, BootManifestVerifier.ParseAndVerify(bytes, [], 0, 1, 1, platform, 0,
            policy, production with { ProductionLocked = false, RequiredRole = BootTrustRole.Development, HardwareDevelopmentEnabled = true },
            new TrustVerifier(keySet, 7)).Failure);

        var truncatedSignedRegion = bytes.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(truncatedSignedRegion.AsSpan(16), (uint)bytes.Length - 1);
        Assert.Equal(BootFailure.Malformed, Verify(truncatedSignedRegion, policy, production, new TrustVerifier(keySet, 7)).Failure);
        var embeddedSignature = bytes.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(embeddedSignature.AsSpan(164), 8);
        Assert.Equal(BootFailure.SecurityPolicyDenied, Verify(embeddedSignature, policy, production, new TrustVerifier(keySet, 7)).Failure);

        BootResult<SingNextBootManifestV1> Verify(byte[] envelope, HybridBootPolicyV1 p, BootTrustContext context, IBootTrustPolicyVerifier verifier) =>
            BootManifestVerifier.ParseAndVerify(envelope, [1], 0, 1, 1, platform, 0, p, context, verifier);
    }

    private BootSelectionPolicy Policy() => new(_volume, _domain, 3, null, 0, null, false, true);

    private BootCandidate Candidate(Guid replica, ulong generation, ulong? dsn) =>
        new(_volume, replica, _image, _domain, generation, new byte[] { 1, 2, 3 }, 3, true, true,
            new("observation", 0, 1, 2, 3, dsn, "route"));

    private static HybridBootPolicyV1 BootPolicy(Guid platform, Guid keySet) =>
        new(1, platform, keySet, Guid.NewGuid(), 0, []);

    private sealed class TrustVerifier(Guid keySet, ulong epoch, bool reject = false) : IBootTrustPolicyVerifier
    {
        public bool VerifyAuthorized(BootTrustVerificationRequest request, ReadOnlySpan<byte> keyId,
            ReadOnlySpan<byte> signedBytes, ReadOnlySpan<byte> signature) =>
            !reject && request.PolicyKeySetId == keySet && request.TrustEpoch == epoch && !signature.IsEmpty;
    }
}
