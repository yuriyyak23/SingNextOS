using HybridCPU.ExternalRuntime.Contracts;
using YAKSys_Hybrid_CPU.Core;
namespace YAKSys_Hybrid_CPU.ExecutableAdapter;
public sealed partial class HybridCpuExecutableChildAdapter : INeutralOriginalExternalParentBindNoEffectProvider
{
    public bool HasOriginalExternalParentBindNoEffect(NeutralOriginalChildCreateRequest request)
    {
        lock (sync)
            return request is not null && originalParents.TryGetValue(request.Parent, out var parent) && OriginalParentBindIdentitySources(parent) &&
                parent.OriginalChildCreates.SingleOrDefault(f => ReferenceEquals(f.Request, request)) is { Consumed: true, LocalNoEffect: true } fact &&
                ReferenceEquals(fact.OriginalOwner, fact) && ReferenceEquals(fact.Parent, parent) && !fact.Invoked &&
                parent.BindNoEffectConsumed &&
                (parent.BindLocalNoEffect && !parent.PrepareInvoked && !parent.BindInvoked || parent.NativeBindValidated is { NoEffect: true, Raw.Receipt: null } proof &&
                ReferenceEquals(proof.Request, parent.NativeBindRequest) && ReferenceEquals(proof.Request.Request, parent.BindRequest));
    }
    private bool OriginalParentBindIdentitySources(Parent parent) =>
        ReferenceEquals(parent.OriginalOwner, parent) && originalParents.TryGetValue(parent.Neutral, out var owner) && ReferenceEquals(owner, parent) &&
        parents.TryGetValue(parent.Neutral, out var route) && ReferenceEquals(route, parent) && parent.CloseRequest is null && !parent.Closed;
    private bool ExactOriginalParentBind(Parent parent, ExternalOriginalDomainBindObservation observation) =>
        ReferenceEquals(observation.Request, parent.NativeBindRequest) && ReferenceEquals(observation.Request.Request, parent.BindRequest) &&
        observation.Raw.Outcome == ExternalRuntimeOutcome.Bound && observation.Raw.Receipt is { } receipt && receipt.Lease.Handle.Value != Guid.Empty &&
        receipt.Lease.Epoch.Value != 0 && receipt.Operation == parent.BindRequest.Operation &&
        receipt.ContractVersion == externalFeatures.ContractVersion && receipt.ManifestGeneration == externalFeatures.Generation;
    private ExternalDomainBindResult InvokeOriginalParentBind(Parent parent)
    {
        var native = (IHybridCpuOriginalDomainBindRuntimeV1)root;
        lock (sync)
        {
            if (!OriginalParentBindIdentitySources(parent) || parent.BindInvoked || parent.BindInFlight) throw new InvalidOperationException("Original parent Bind source/pending changed.");
            parent.BindInFlight = true;
        }
        try
        {
            lock (sync) parent.PrepareInvoked = true;
            var prepared = native.PrepareOriginalDomainBind(parent.BindRequest);
            lock (sync) parent.NativeBindRequest = prepared;
            var features = root.QueryFeatures();
            lock (sync)
            {
                if (prepared is null || !ReferenceEquals(prepared.Request, parent.BindRequest) || !OriginalParentBindIdentitySources(parent) ||
                    features.Generation != externalFeatures.Generation || features.ContractVersion != externalFeatures.ContractVersion)
                    throw new InvalidOperationException("Original parent Bind captured source unavailable.");
                parent.BindInvoked = true;
            }
            var observation = native.BindOriginalDomain(prepared);
            lock (sync) parent.NativeBindRaw = observation;
            var canonical = native.ResolveOriginalDomainBind(prepared);
            lock (sync)
            {
                if (observation is null || !ReferenceEquals(observation, canonical) || !ReferenceEquals(observation.Request, prepared))
                    throw new InvalidOperationException("Original parent Bind outcome unknown/foreign.");
                parent.NativeBindValidated = observation;
                parent.BindObservation = observation.Raw;
            }
            features = root.QueryFeatures();
            lock (sync)
                if (!OriginalParentBindIdentitySources(parent) || features.Generation != externalFeatures.Generation || features.ContractVersion != externalFeatures.ContractVersion)
                    throw new InvalidOperationException("Known original parent Bind source changed before consumption.");
            return observation.Raw;
        }
        finally { lock (sync) { parent.BindInFlight = false; if (!parent.BindInvoked) parent.BindLocalNoEffect = true; } }
    }
    private bool ResolveOriginalParentBind(Parent parent)
    {
        lock (sync)
        {
            if (!OriginalParentBindIdentitySources(parent) || parent.BindInFlight || pendingParentBinds.Contains(parent.Neutral.Handle)) return false;
            if (parent.Admission is { } receipt)
                return ReferenceEquals(parent.BindObservation?.Receipt, receipt) && receipt.Operation == parent.BindRequest.Operation &&
                    receipt.ContractVersion == externalFeatures.ContractVersion && receipt.ManifestGeneration == externalFeatures.Generation;
            if (parent.BindNoEffectConsumed) return true;
            if (parent.BindLocalNoEffect && !parent.PrepareInvoked && !parent.BindInvoked) { parent.BindNoEffectConsumed = true; return true; }
            if (root is not IHybridCpuOriginalDomainBindRuntimeV1 || parent.NativeBindRequest is null || !parent.BindInvoked && !parent.BindLocalNoEffect) return false;
            parent.BindInFlight = true;
        }
        try
        {
            var native = (IHybridCpuOriginalDomainBindRuntimeV1)root;
            if (parent.BindLocalNoEffect && !parent.BindInvoked && !parent.BindRetirementInvoked)
            {
                if (root is not IHybridCpuOriginalDomainBindRetirementRuntimeV1 retirement) return false;
                lock (sync) parent.BindRetirementInvoked = true;
                var retired = retirement.RetireOriginalDomainBind(parent.NativeBindRequest!);
                lock (sync) parent.BindRetirementRaw = retired; // Before pure canonical queries/callbacks.
            }
            var observation = parent.NativeBindValidated;
            if (observation is null)
            {
                observation = native.ResolveOriginalDomainBind(parent.NativeBindRequest!);
                lock (sync) parent.NativeBindResolution = observation;
                var canonical = native.ResolveOriginalDomainBind(parent.NativeBindRequest!);
                if (observation is null || !ReferenceEquals(observation, canonical) || !ReferenceEquals(observation.Request, parent.NativeBindRequest)) return false;
                lock (sync) parent.NativeBindValidated = observation;
            }
            var features = root.QueryFeatures();
            lock (sync)
            {
                if (!OriginalParentBindIdentitySources(parent) || features.Generation != externalFeatures.Generation || features.ContractVersion != externalFeatures.ContractVersion) return false;
                if (observation.NoEffect && observation.Raw.Receipt is null)
                { parent.BindObservation = observation.Raw; parent.BindNoEffectConsumed = true; return true; }
                if (!ExactOriginalParentBind(parent, observation)) return false;
                parent.BindObservation = observation.Raw; parent.Admission = observation.Raw.Receipt; return true;
            }
        }
        finally { lock (sync) parent.BindInFlight = false; }
    }
}
