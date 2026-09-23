using System.Buffers.Binary;
using System.Security.Cryptography;
using YAKSys_Hybrid_CPU.Boot.Contracts;

namespace SingNext.Boot.Core;

public enum BootTrustRole : byte
{
    Production = 1,
    Development = 2,
    Recovery = 3,
}

public readonly record struct BootTrustContext(
    Guid ActiveKeySetId,
    ulong ActiveTrustEpoch,
    BootTrustRole RequiredRole,
    bool ProductionLocked,
    bool HardwareDevelopmentEnabled);

public readonly record struct BootTrustVerificationRequest(
    Guid PolicyKeySetId,
    ulong TrustEpoch,
    BootTrustRole RequiredRole,
    bool ProductionLocked,
    bool HardwareDevelopmentEnabled,
    BootSignatureAlgorithm Algorithm);

/// <summary>
/// Authoritative boot trust-policy port. Implementations must resolve exactly one key in the
/// requested key set and epoch, reject revoked/duplicate keys, enforce key role and production
/// lock, and only then verify the signature over the supplied canonical signed bytes.
/// </summary>
public interface IBootTrustPolicyVerifier
{
    bool VerifyAuthorized(
        BootTrustVerificationRequest request,
        ReadOnlySpan<byte> keyId,
        ReadOnlySpan<byte> signedBytes,
        ReadOnlySpan<byte> signature);
}

public static class BootManifestVerifier
{
    public static BootResult<SingNextBootManifestV1> ParseAndVerify(
        ReadOnlySpan<byte> envelope,
        ReadOnlySpan<byte> signature,
        ulong supportedFeatures,
        uint cpuAbi,
        uint firmwareAbi,
        Guid expectedPlatformFamily,
        ulong imageRollbackFloor,
        HybridBootPolicyV1 policy,
        BootTrustContext trust,
        IBootTrustPolicyVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        if (envelope.Length > BootAbiV1.MaxManifestBytes)
            return BootResult<SingNextBootManifestV1>.Fail(BootFailure.LimitExceeded, "Manifest exceeds the v1 bound.");
        if (envelope.Length < BootManifestCodec.HeaderSize)
            return BootResult<SingNextBootManifestV1>.Fail(BootFailure.Malformed, "Manifest header is truncated.");
        var totalLength = BinaryPrimitives.ReadUInt32LittleEndian(envelope[12..]);
        var signedLength = BinaryPrimitives.ReadUInt32LittleEndian(envelope[16..]);
        var parsed = BootManifestCodec.Parse(envelope, supportedFeatures);
        if (!parsed.IsSuccess)
            return BootResult<SingNextBootManifestV1>.Fail(BootFailure.Malformed, parsed.Detail ?? "Manifest parse failed.");
        var manifest = parsed.Value!;
        if (manifest.BootVolumeId == Guid.Empty || manifest.ReplicaId == Guid.Empty || manifest.ImageId == Guid.Empty ||
            manifest.RollbackDomainId == Guid.Empty || manifest.PlatformFamilyId != expectedPlatformFamily ||
            manifest.ComponentCount == 0 || manifest.RequiredCpuAbiMin > manifest.RequiredCpuAbiMax ||
            manifest.RequiredFirmwareAbiMin > manifest.RequiredFirmwareAbiMax ||
            manifest.KernelComponentIndex >= manifest.ComponentCount || manifest.Stage1ComponentIndex >= manifest.ComponentCount ||
            cpuAbi < manifest.RequiredCpuAbiMin || cpuAbi > manifest.RequiredCpuAbiMax ||
            firmwareAbi < manifest.RequiredFirmwareAbiMin || firmwareAbi > manifest.RequiredFirmwareAbiMax)
            return BootResult<SingNextBootManifestV1>.Fail(BootFailure.SecurityPolicyDenied, "Manifest identity or ABI constraints do not match this boot environment.");
        if (totalLength != envelope.Length || signedLength != totalLength || manifest.SignatureBlockOffset != 0)
            return BootResult<SingNextBootManifestV1>.Fail(BootFailure.SecurityPolicyDenied, "Manifest signed region is not the canonical detached-signature form.");
        if (manifest.HashAlgorithm != BootHashAlgorithm.Sha384)
            return BootResult<SingNextBootManifestV1>.Fail(BootFailure.SecurityPolicyDenied, "The production loader supports only SHA-384 component hashes.");
        if (manifest.ImageGeneration < imageRollbackFloor)
            return BootResult<SingNextBootManifestV1>.Fail(BootFailure.RollbackRejected, "Manifest image generation is below the protected floor.");
        if (policy.PolicyGeneration == 0 || policy.PlatformId != expectedPlatformFamily || policy.KeySetId == Guid.Empty ||
            trust.ActiveKeySetId != policy.KeySetId || trust.ActiveTrustEpoch == 0 || !Enum.IsDefined(trust.RequiredRole) ||
            (trust.ProductionLocked && trust.RequiredRole != BootTrustRole.Production) ||
            (trust.RequiredRole == BootTrustRole.Development && !trust.HardwareDevelopmentEnabled))
            return BootResult<SingNextBootManifestV1>.Fail(BootFailure.SecurityPolicyDenied, "Boot policy key set, trust epoch, role, or hardware lock is not admissible.");
        var request = new BootTrustVerificationRequest(policy.KeySetId, trust.ActiveTrustEpoch, trust.RequiredRole,
            trust.ProductionLocked, trust.HardwareDevelopmentEnabled, manifest.SignatureAlgorithm);
        if (signature.IsEmpty || !verifier.VerifyAuthorized(request, manifest.SigningKeyId.Span, envelope[..checked((int)signedLength)], signature))
            return BootResult<SingNextBootManifestV1>.Fail(BootFailure.SecurityPolicyDenied, "Manifest key authorization or signature is invalid.");
        return BootResult<SingNextBootManifestV1>.Success(manifest);
    }
}

public static class VerifiedImageLoader
{
    public static BootFailure CopyAndVerifyDestination(
        ReadOnlySpan<byte> source,
        Span<byte> destination,
        ReadOnlySpan<byte> expectedSha384,
        Span<byte> sha384Scratch)
    {
        if (source.IsEmpty || source.Length > BootLimits.MaxComponentBytes || destination.Length != source.Length ||
            expectedSha384.Length != 48 || sha384Scratch.Length < 48 || source.Overlaps(destination))
            return BootFailure.BoundsViolation;
        source.CopyTo(destination);
        var actual = sha384Scratch[..48];
        if (!SHA384.TryHashData(destination, actual, out var written) || written != actual.Length)
        {
            CryptographicOperations.ZeroMemory(destination);
            return BootFailure.Unsupported;
        }
        if (!CryptographicOperations.FixedTimeEquals(actual, expectedSha384))
        {
            CryptographicOperations.ZeroMemory(destination);
            return BootFailure.HashMismatch;
        }
        return BootFailure.None;
    }

    public static BootFailure CopyAndVerifyDestinationWithStackScratch(
        ReadOnlySpan<byte> source,
        Span<byte> destination,
        ReadOnlySpan<byte> expectedSha384)
    {
        Span<byte> actual = stackalloc byte[48];
        return CopyAndVerifyDestination(source, destination, expectedSha384, actual);
    }
}
