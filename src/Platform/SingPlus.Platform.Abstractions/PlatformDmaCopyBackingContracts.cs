[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("SingPlus.Platform.HybridCpu")]

namespace SingPlus.Platform;

/// <summary>
/// Runtime-owned CPU access reservations for one original copy request. These bytes
/// do not grant DMA permission; the provider independently admits the exact grants,
/// cycles and generations. Retained bytes remain inaccessible to the buffer owner
/// until exact completion/visibility or explicit no-effect consumption.
/// </summary>
public sealed class PlatformDmaCopyBacking
{
    public PlatformDmaCopyBacking(ReadOnlyMemory<byte> source, Memory<byte> destination)
    {
        if (source.IsEmpty || source.Length != destination.Length)
            throw new ArgumentException("Copy backing requires equal nonempty byte ranges.");
        Source = source; Destination = destination;
    }
    public ReadOnlyMemory<byte> Source { get; }
    public Memory<byte> Destination { get; }
}

public interface IPlatformDmaCopyBackingProvider : IPlatformDmaCopySubmissionProvider
{
    // NotAccepted retains the V5 exact neither-leg-accepted guarantee. All other
    // failures retain backing; a missing response never means no effect.
    PlatformAuthorityResult<PlatformProviderDmaCopySubmission> SubmitDmaCopy(
        PlatformProviderDmaCopySubmitRequest request, PlatformDmaCopyBacking backing);
}

public sealed class PlatformOriginalDmaCopyRequest(PlatformProviderDmaCopySubmitRequest request,PlatformDmaCopyBacking backing)
{
    public Guid InvocationId { get; } = Guid.NewGuid();
    public PlatformProviderDmaCopySubmitRequest Request { get; } = request;
    private PlatformDmaCopyBacking? _backing = backing;
    public PlatformDmaCopyBacking Backing => _backing ?? throw new InvalidOperationException("Original producer byte lifetime already ended; lookup only.");
    internal void ReleaseBacking() => _backing = null;
}
public readonly record struct PlatformOriginalDmaCopyResolution(PlatformOriginalDmaCopyRequest Request,
    bool NoEffectGuaranteed,PlatformProviderDmaCopySubmission? Submission);
public interface IPlatformOriginalDmaCopyProvider : IPlatformDmaCopyBackingProvider
{
    PlatformAuthorityResult<PlatformProviderDmaCopySubmission> SubmitDmaCopy(PlatformOriginalDmaCopyRequest request);
    PlatformAuthorityResult<PlatformOriginalDmaCopyResolution> ResolveDmaCopy(PlatformOriginalDmaCopyRequest request);
}

public readonly record struct PlatformOriginalDmaCopyLifetime(PlatformOriginalDmaCopyRequest Request,
    bool NoFurtherByteUseGuaranteed);
public interface IPlatformOriginalDmaCopyLifetimeProvider : IPlatformOriginalDmaCopyProvider
{
    // Pure cleanup-only observation. Neither completion/publication nor grant/mapping closure.
    PlatformAuthorityResult<PlatformOriginalDmaCopyLifetime> ResolveDmaCopyLifetime(PlatformOriginalDmaCopyRequest request);
}

public interface IPlatformOriginalDmaCopyCompletionProvider : IPlatformOriginalDmaCopyLifetimeProvider,IPlatformDmaCompletionProvider
{
    // Pure metadata lookup of this original copy's exact leg; no new effect or byte user.
    PlatformAuthorityResult<PlatformProviderDmaCompletionEvidence> ObserveOriginalDmaCopyCompletion(
        PlatformOriginalDmaCopyRequest request,PlatformProviderDmaSubmission submission);
}
