using SingNext.Boot.Core;
using YAKSys_Hybrid_CPU.Boot.Contracts;

namespace SingNext.Boot.Capsule;

public readonly record struct CapsuleLaunchEvidence(
    ulong BootCapsuleGeneration,
    ulong ImageGeneration,
    ulong BootMappingGeneration,
    int ImageBytes,
    int BootInfoBytes);

public static class DirectBootCapsule
{
    public static BootResult<CapsuleLaunchEvidence> PrepareKernelResetAware(
        ulong bootCapsuleGeneration,
        ulong imageGeneration,
        BootMappingEvidence mapping,
        ReadOnlySpan<byte> authenticatedImage,
        ReadOnlySpan<byte> expectedDestinationSha384,
        Span<byte> normalRamDestination,
        HybridBootInfoV1 bootInfo,
        Span<byte> bootInfoDestination,
        Span<byte> sha384Scratch,
        BootResetSnapshot expectedReset,
        IBootResetControl resetControl)
    {
        ArgumentNullException.ThrowIfNull(resetControl);
        if (resetControl.Observe() != expectedReset)
            return BootResult<CapsuleLaunchEvidence>.Fail(BootFailure.ResetObserved, "Reset occurred before the verified copy.");
        var result = PrepareKernel(bootCapsuleGeneration, imageGeneration, mapping, authenticatedImage,
            expectedDestinationSha384, normalRamDestination, bootInfo, bootInfoDestination, sha384Scratch);
        if (!result.IsSuccess) return result;
        if (resetControl.Observe() == expectedReset) return result;
        normalRamDestination.Clear();
        bootInfoDestination.Clear();
        return BootResult<CapsuleLaunchEvidence>.Fail(BootFailure.ResetObserved, "Reset invalidated the copy or BootInfo handoff.");
    }

    public static BootResult<CapsuleLaunchEvidence> PrepareKernel(
        ulong bootCapsuleGeneration,
        ulong imageGeneration,
        BootMappingEvidence mapping,
        ReadOnlySpan<byte> authenticatedImage,
        ReadOnlySpan<byte> expectedDestinationSha384,
        Span<byte> normalRamDestination,
        HybridBootInfoV1 bootInfo,
        Span<byte> bootInfoDestination,
        Span<byte> sha384Scratch)
    {
        if (bootCapsuleGeneration == 0 || imageGeneration == 0 || mapping.BootMappingGeneration == 0)
            return BootResult<CapsuleLaunchEvidence>.Fail(BootFailure.Malformed, "Boot generations are required and remain distinct domains.");
        var load = VerifiedImageLoader.CopyAndVerifyDestination(
            authenticatedImage, normalRamDestination, expectedDestinationSha384, sha384Scratch);
        if (load != BootFailure.None)
            return BootResult<CapsuleLaunchEvidence>.Fail(load, "Kernel image did not verify in normal RAM.");
        var write = HybridBootInfoBoundedWriter.TryWrite(bootInfo, bootInfoDestination, sha384Scratch, out var bootInfoBytes);
        if (write != BootFailure.None)
        {
            normalRamDestination.Clear();
            return BootResult<CapsuleLaunchEvidence>.Fail(write, "BootInfo could not be emitted into the bounded kernel-owned handoff buffer.");
        }
        return BootResult<CapsuleLaunchEvidence>.Success(new(
            bootCapsuleGeneration, imageGeneration, mapping.BootMappingGeneration, authenticatedImage.Length, bootInfoBytes));
    }
}
