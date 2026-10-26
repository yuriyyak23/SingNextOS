using HybridCPU.ExternalRuntime.Contracts;
using YAKSys_Hybrid_CPU.Core;
namespace YAKSys_Hybrid_CPU.ExecutableAdapter;
public sealed partial class HybridCpuExecutableChildAdapter : INeutralOriginalChildCreateProvider
{
    private sealed class OriginalChildCreateInvocation
    {
        public OriginalChildCreateInvocation(Parent parent, NeutralOriginalChildCreateRequest request, ulong identity)
        { OriginalOwner = this; Parent = parent; Request = request; Identity = identity; }
        public OriginalChildCreateInvocation OriginalOwner { get; }
        public Parent Parent { get; }
        public NeutralOriginalChildCreateRequest Request { get; }
        public ulong Identity { get; }
        public ExternalOriginalChildCreateRequest? NativeRequest { get; set; }
        public ExternalOriginalChildCreateObservation? Raw { get; set; }
        public ExternalOriginalChildCreateObservation? Resolution { get; set; }
        public ExternalOriginalChildCreateObservation? Validated { get; set; }
        public Child? Child { get; set; }
        public bool Invoked, InFlight, OwnPin, Consumed, LocalNoEffect;
    }
    public bool SupportsOriginalChildCreateResolution => childRuntime is IHybridCpuOriginalChildCreateRuntimeV1;
    public NeutralVirtualizationResult<NeutralChildDomainLease> CreateOriginalChild(NeutralOriginalChildCreateRequest request) =>
        request is not null && SupportsOriginalChildCreateResolution ? CreateChildDomainCore(request.Parent, request.Intent, request) :
        Fail<NeutralChildDomainLease>(NeutralVirtualizationStatus.NotInvoked, "Original child Create capability unavailable.");
    private bool OriginalChildCreateSources(OriginalChildCreateInvocation fact) =>
        HasOriginalParentBindCustody(fact.Parent) &&
        ReferenceEquals(fact.OriginalOwner, fact) && fact.Parent.OriginalChildCreates.Contains(fact) && ReferenceEquals(fact.Parent.OriginalOwner, fact.Parent) &&
        originalParents.TryGetValue(fact.Parent.Neutral, out Parent? original) && ReferenceEquals(original, fact.Parent) &&
        parents.TryGetValue(fact.Parent.Neutral, out Parent? route) && ReferenceEquals(route, fact.Parent) &&
        fact.Parent.Admission is { } admission && ReferenceEquals(fact.Parent.BindObservation?.Receipt, admission) &&
        admission.Operation == fact.Parent.BindRequest.Operation && admission.ContractVersion == externalFeatures.ContractVersion && admission.ManifestGeneration == externalFeatures.Generation &&
        fact.Parent.CloseRequest is null && !fact.Parent.Closed && !pendingParentBinds.Contains(fact.Parent.Neutral.Handle);
    private ExternalChildResult<ExternalChildDomainCreateReceipt> InvokeOriginalChildCreate(OriginalChildCreateInvocation fact, ExternalChildDomainCreateRequest request)
    {
        IHybridCpuOriginalChildCreateRuntimeV1 native = (IHybridCpuOriginalChildCreateRuntimeV1)childRuntime;
        lock (sync)
        {
            if (!OriginalChildCreateSources(fact)) throw new InvalidOperationException("Original child Create parent changed before effect.");
            fact.NativeRequest = new(fact.Parent.Admission!, request)
                { AllowNestedCreation = SupportsOriginalNestedDomains && fact.Request.Intent.Authority.ChildAuthority.HasFlag(NeutralChildAuthorityClass.Lifecycle) };
            fact.InFlight = true;
        }
        try
        {
            HybridCpuExternalFeatureManifest before = root.QueryFeatures();
            lock (sync)
            {
                if (!OriginalChildCreateSources(fact) || before.Generation != externalFeatures.Generation || before.ContractVersion != externalFeatures.ContractVersion)
                    throw new InvalidOperationException("Original child Create source changed before invocation.");
                fact.Invoked = true;
            }
            ExternalOriginalChildCreateObservation? raw = native.CreateOriginalChild(fact.NativeRequest);
            lock (sync) fact.Raw = raw;
            ExternalOriginalChildCreateObservation? canonical = native.ResolveOriginalChildCreate(fact.NativeRequest);
            lock (sync)
            {
                if (raw is null || !ReferenceEquals(raw, canonical) || !ReferenceEquals(raw.Request, fact.NativeRequest))
                    throw new InvalidOperationException("Original child Create response unknown/foreign.");
                fact.Validated = raw;
            }
            HybridCpuExternalFeatureManifest features = root.QueryFeatures();
            lock (sync)
                if (!OriginalChildCreateSources(fact) || features.Generation != externalFeatures.Generation || features.ContractVersion != externalFeatures.ContractVersion)
                    throw new InvalidOperationException("Known child Create parent changed before consumption.");
            return raw.Raw;
        }
        finally { lock (sync) { fact.InFlight = false; if (!fact.Invoked) fact.LocalNoEffect = true; } }
    }
    public NeutralVirtualizationResult<NeutralChildDomainLease> ResolveOriginalChildCreate(NeutralOriginalChildCreateRequest request)
    {
        OriginalChildCreateInvocation fact;
        lock (sync)
        {
            if (request is null || childRuntime is not IHybridCpuOriginalChildCreateRuntimeV1 ||
                !originalParents.TryGetValue(request.Parent, out Parent? parent) || parent.OriginalChildCreates.SingleOrDefault(f => ReferenceEquals(f.Request, request)) is not { } saved ||
                (!OriginalChildCreateSources(saved) && !(saved.LocalNoEffect && OriginalParentBindIdentitySources(saved.Parent))) ||
                saved.InFlight || saved.Parent.BindInFlight || saved.NativeRequest is null && !saved.LocalNoEffect || !saved.Invoked && !saved.LocalNoEffect)
                return Fail<NeutralChildDomainLease>(NeutralVirtualizationStatus.Ambiguous, "Original child Create request/source unavailable.");
            fact = saved;
            if (fact.Child is { } known) return Ok(known.Neutral);
            if (fact.Consumed && (fact.LocalNoEffect || fact.Validated is { NoEffect: true })) return Fail<NeutralChildDomainLease>(NeutralVirtualizationStatus.NotInvoked, "Exact original no-effect consumed.");
            fact.InFlight = true;
        }
        try
        {
            if (fact.LocalNoEffect)
            {
                if (!ResolveOriginalParentBind(fact.Parent)) return Fail<NeutralChildDomainLease>(NeutralVirtualizationStatus.Ambiguous, "Original external parent Bind outcome unresolved.");
                HybridCpuExternalFeatureManifest current = root.QueryFeatures();
                lock (sync)
                {
                    if (!(OriginalChildCreateSources(fact) || fact.Parent.BindNoEffectConsumed && OriginalParentBindIdentitySources(fact.Parent)) ||
                        current.Generation != externalFeatures.Generation || current.ContractVersion != externalFeatures.ContractVersion)
                        return Fail<NeutralChildDomainLease>(NeutralVirtualizationStatus.Ambiguous, "Uninvoked original Create source unavailable.");
                    ConsumeOriginalChildCreatePin(fact);
                    return Fail<NeutralChildDomainLease>(NeutralVirtualizationStatus.NotInvoked, "Exact original child Create was never invoked.");
                }
            }
            IHybridCpuOriginalChildCreateRuntimeV1 native = (IHybridCpuOriginalChildCreateRuntimeV1)childRuntime;
            ExternalOriginalChildCreateObservation? observation = fact.Validated;
            if (observation is null)
            {
                observation = native.ResolveOriginalChildCreate(fact.NativeRequest!);
                lock (sync) fact.Resolution = observation;
                ExternalOriginalChildCreateObservation? canonical = native.ResolveOriginalChildCreate(fact.NativeRequest!);
                if (observation is null || !ReferenceEquals(observation, canonical) || !ReferenceEquals(observation.Request, fact.NativeRequest))
                    return Fail<NeutralChildDomainLease>(NeutralVirtualizationStatus.Ambiguous, "Original child Create outcome unknown/foreign.");
                lock (sync) fact.Validated = observation;
            }
            HybridCpuExternalFeatureManifest features = root.QueryFeatures();
            lock (sync)
            {
                if (!OriginalChildCreateSources(fact) || features.Generation != externalFeatures.Generation || features.ContractVersion != externalFeatures.ContractVersion)
                    return Fail<NeutralChildDomainLease>(NeutralVirtualizationStatus.Ambiguous, "Known original child source changed.");
                if (observation.NoEffect && observation.Raw.Receipt is null)
                { ConsumeOriginalChildCreatePin(fact); return Fail<NeutralChildDomainLease>(NeutralVirtualizationStatus.NotInvoked, "Exact original no-effect consumed."); }
                ExternalChildDomainCreateReceipt? receipt = observation.Raw.Receipt;
                ExternalChildDomainCreateRequest external = fact.NativeRequest!.CreationRequest;
                if (observation.NoEffect || observation.Raw.Outcome != ExternalRuntimeOutcome.Bound || receipt is null || receipt.Lease.Handle.Value == Guid.Empty ||
                    receipt.Lease.Epoch.Value == 0 || receipt.Lease.Parent != fact.Parent.External || receipt.Operation != external.Operation ||
                    receipt.GrantedAuthority != external.RequestedAuthority || receipt.GuestMemoryLimitBytes != external.GuestMemoryLimitBytes ||
                    receipt.ContractVersion != externalFeatures.ContractVersion || receipt.ManifestGeneration != externalFeatures.Generation || receipt.State != ExternalChildDomainState.Ready)
                    return Fail<NeutralChildDomainLease>(NeutralVirtualizationStatus.Ambiguous, "Original child Create receipt malformed.");
                NeutralChildDomainLease lease = new NeutralChildDomainLease(request.Parent, new(fact.Identity), new(1), request.Intent);
                Child child = new Child(lease, receipt.Lease, fact.Parent.External, NeutralChildDomainState.Created)
                    { AdmissionPublished = false, CreateReceipt = receipt, ParentSource = fact.Parent };
                originalChildren.Add(lease.Handle, child); children.Add(lease.Handle, child); fact.Child = child;
                ConsumeOriginalChildCreatePin(fact);
                return Ok(lease);
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return Fail<NeutralChildDomainLease>(NeutralVirtualizationStatus.Ambiguous, "Original child Create lookup unavailable: " + exception.Message); }
        finally { lock (sync) fact.InFlight = false; }
    }
    private void ConsumeOriginalChildCreatePin(OriginalChildCreateInvocation fact)
    {
        fact.Consumed = true;
        if (fact.OwnPin && !fact.Parent.OriginalChildCreates.Any(f => !f.Consumed)) parentCreateMayHaveEffect.Remove(fact.Parent.Neutral.Handle);
        fact.OwnPin = false;
    }
}
