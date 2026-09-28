namespace SingNext.Boot.Capsule;

using SingNext.Boot.Core;
using YAKSys_Hybrid_CPU.Boot.Contracts;

public static class BootCapsuleEntry
{
    // This root intentionally stays allocation-free. Full composition is added only through
    // admitted value-type/Span paths; unavailable platform services return a terminal code.
    public static int Run() => (int)BootFailure.Unsupported;

    public static int Prepare(
        ReadOnlySpan<byte> authenticatedImage,
        Span<byte> normalRamDestination,
        ReadOnlySpan<byte> expectedDestinationSha384,
        HybridBootInfoV1 bootInfo,
        Span<byte> bootInfoDestination,
        Span<byte> sha384Scratch,
        out int bootInfoBytes)
    {
        bootInfoBytes = 0;
        var load = VerifiedImageLoader.CopyAndVerifyDestination(
            authenticatedImage, normalRamDestination, expectedDestinationSha384, sha384Scratch);
        if (load != BootFailure.None) return (int)load;
        var write = HybridBootInfoBoundedWriter.TryWrite(bootInfo, bootInfoDestination, sha384Scratch, out bootInfoBytes);
        if (write == BootFailure.None) return 0;
        normalRamDestination.Clear();
        return (int)write;
    }
}
