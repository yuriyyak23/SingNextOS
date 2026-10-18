using SingPlus.Platform;
using SingPlus.Contracts;

namespace SingPlus.Runtime;

public sealed partial class PlatformAuthorityBridge
{
    private KernelResult ResolveArtifactBindForCleanup(ExecutableArtifactRecord artifact)
    {
        var provider = _provider as IPlatformOriginalArtifactBindProvider;
        ExecutableBindInvocationFact fact;
        ChildBindingRecord[] lineage;
        lock (_secureDomainLifecycleGate)
        {
            if (artifact.BindInvocation is not { Invoked: true } saved || artifact.ReleaseInFlight ||
                saved.Source.Child.PendingChildEffects != 0 || saved.Source.Guest.PendingExecutableEffects != 0 || saved.Source.Guest.ClosureInFlight ||
                saved.Source.Child.PendingChildEffects == int.MaxValue || saved.Source.Guest.PendingExecutableEffects == int.MaxValue)
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Original artifact Bind cleanup is in flight or absent.");
            if (provider is null)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original artifact Bind resolution unavailable; backing remains pinned.");
            fact = saved;
            if (!GuestEffectLineageCurrent(fact.Ancestors))
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original Bind ancestor source changed before cleanup lookup.");
            lineage = fact.Ancestors.Keys.Append(fact.Source.Child).ToArray();
            if (lineage.Any(source => source.PendingChildEffects != 0 || source.CloseInFlight || source.TransitionInFlight))
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Original Bind ancestor lookup is in flight.");
            artifact.ReleaseInFlight = true;
            foreach (var source in lineage) source.PendingChildEffects++;
            fact.Source.Guest.PendingExecutableEffects++;
        }
        try
        {
            var source = CurrentProviderIncarnation();
            lock (_secureDomainLifecycleGate) if (!HasOriginalArtifactReleaseSources(artifact, source))
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original Bind source changed before pure lookup.");
            var result = fact.Observation is { IsSuccess: true } known ? known :
                fact.ResolutionObservation is { IsSuccess: true } retained ? retained : provider.ResolveExecutableArtifactBind(fact.Request);
            lock (_secureDomainLifecycleGate) fact.ResolutionObservation = result;
            source = CurrentProviderIncarnation();
            lock (_secureDomainLifecycleGate)
            {
                if (_provider is IPlatformOriginalArtifactBindNoEffectProvider && HasOriginalArtifactReleaseSources(artifact, source) && result.Status == PlatformAuthorityStatus.NotAccepted &&
                    HasOwnExecutableStartFailure(artifact))
                {
                    fact.NoEffectConsumed = true;
                    artifact.Released = true; // No admitted backing exists; no Release invocation or receipt invented.
                    ConsumeExecutableStartFailureAfterRelease(artifact);
                    return fact.DependencyFailureConsumed ? KernelResult.Ok() : KernelResult.Fail(KernelError.PlatformFaulted, "Independent Bind dependencies remain pinned.");
                }
                if (!HasOriginalArtifactReleaseSources(artifact, source) || !result.IsSuccess ||
                    !PlatformChildExecutionContract.ValidateAdmissionReceipt(fact.Request, result.Value).IsSuccess ||
                    result.Value.ContentDigest != Convert.ToHexStringLower(global::System.Security.Cryptography.SHA256.HashData(fact.Request.ImmutablePackage.Span)))
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Original Bind outcome unknown/malformed; backing remains pinned.");
                artifact.ProviderReceipt = result.Value; // Cleanup-only identity; no publication/Start.
                return KernelResult.Ok();
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return KernelResult.Fail(KernelError.PlatformFaulted, $"Original Bind lookup retained/unknown: {exception.GetType().Name}"); }
        finally
        {
            lock (_secureDomainLifecycleGate)
            {
                artifact.ReleaseInFlight = false;
                foreach (var source in lineage) source.PendingChildEffects--;
                fact.Source.Guest.PendingExecutableEffects--;
            }
        }
    }
}
