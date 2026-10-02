using HybridCPU.ExternalRuntime;
using HybridCPU.ExternalRuntime.Contracts;
using SingPlus.Contracts;
using System.Security.Cryptography;
using System.Text;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.ExecutableAdapter.Backend;

namespace YAKSys_Hybrid_CPU.ExecutableAdapter;

/// <summary>Exclusive translation boundary between neutral child authority and ExternalRuntime V3.</summary>
public sealed class HybridCpuExecutableChildAdapter : INeutralRuntimeFeatureProvider,
    INeutralChildDomainProvider, INeutralGuestMemoryProvider, INeutralVirtualEventProvider,
    INeutralVirtualIoProvider, INeutralChildExecutionProvider
{
    private sealed record Child(NeutralChildDomainLease Neutral, ExternalChildDomainLease External,
        ExternalDomainLease Parent, NeutralChildDomainState State)
    {
        public bool GuestMapMayHaveEffect { get; init; }
        public bool CloseMayHaveEffect { get; init; }
    }
    private sealed record Mapping(NeutralGuestMappingLease Neutral, ExternalGuestMappingLease External)
    {
        public bool ArtifactMayHaveEffect { get; init; }
        public bool ClosureMayHaveEffect { get; init; }
    }
    private sealed record Artifact(NeutralExecutableArtifactReceipt Neutral, ExternalChildArtifactBindReceipt External);
    private sealed record Io(NeutralVirtualIoLease Neutral, ExternalChildVirtualIoBindReceipt External);

    private readonly object sync = new();
    private readonly IHybridCpuExternalRuntime root;
    private readonly IHybridCpuChildDomainRuntimeV3 childRuntime;
    private readonly HybridCpuExternalFeatureManifest externalFeatures;
    private readonly ISemanticTraceSinkV1? traceSink;
    private readonly Dictionary<NeutralDomainBindingLease, ExternalDomainLease> parents = [];
    // Admission interlock only; durable dependency pins remain in mappings/artifacts.
    private readonly HashSet<NeutralGuestMappingHandle> pendingArtifactBinds = [];
    private readonly HashSet<NeutralGuestMappingHandle> pendingGuestUnmaps = [];
    private readonly HashSet<NeutralChildDomainHandle> pendingChildCloses = [];
    private readonly Dictionary<NeutralChildDomainHandle, int> pendingGuestMaps = [];
    private readonly HashSet<NeutralChildDomainHandle> pendingChildTransitions = [];
    private readonly HashSet<NeutralChildDomainHandle> pendingChildStarts = [];
    private readonly HashSet<NeutralDomainBindingHandle> pendingParentBinds = [];
    private readonly HashSet<NeutralDomainBindingHandle> parentCreateMayHaveEffect = [];
    private readonly Dictionary<NeutralChildDomainHandle, Child> children = [];
    private readonly Dictionary<NeutralGuestMappingHandle, Mapping> mappings = [];
    private readonly Dictionary<NeutralExecutableArtifactHandle, Artifact> artifacts = [];
    private readonly Dictionary<NeutralVirtualIoHandle, Io> ioBindings = [];
    private ulong nextIdentity = 1;
    private ulong nextOperation = 1;

    public HybridCpuExecutableChildAdapter() : this(new HybridCpuExternalRuntime(), null) { }

    public HybridCpuExecutableChildAdapter(ISemanticTraceSinkV1 traceSink) :
        this(new HybridCpuExternalRuntime(), traceSink ?? throw new ArgumentNullException(nameof(traceSink))) { }

    internal HybridCpuExecutableChildAdapter(HybridCpuExternalRuntime runtime, ISemanticTraceSinkV1? traceSink = null)
        : this(runtime, runtime, traceSink) { }

    internal HybridCpuExecutableChildAdapter(IHybridCpuExternalRuntime runtime,
        IHybridCpuChildDomainRuntimeV3 childRuntime, ISemanticTraceSinkV1? traceSink = null)
    {
        root = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.childRuntime = childRuntime ?? throw new ArgumentNullException(nameof(childRuntime));
        this.traceSink = traceSink;
        externalFeatures = root.QueryFeatures();
        if (!ExternalChildContractQualification.CanAdvertiseExecutable(externalFeatures))
            throw new InvalidOperationException("ExternalRuntime V3 executable artifact and bounded-I/O qualification is required.");
    }

    public NeutralRuntimeFeatureManifest QueryNeutralFeatures() => new(
    [
        new(NeutralRuntimeFeatureFamily.ChildDomainLifecycle, NeutralChildDomainContract.ContractVersion, NeutralRuntimeFeatureAvailability.Executable),
        new(NeutralRuntimeFeatureFamily.ChildGuestMemory, NeutralGuestMemoryContract.ContractVersion, NeutralRuntimeFeatureAvailability.Executable),
        new(NeutralRuntimeFeatureFamily.ChildEventDelivery, NeutralVirtualEventContract.ContractVersion, NeutralRuntimeFeatureAvailability.RuntimeAdmission),
        new(NeutralRuntimeFeatureFamily.ChildExecutableArtifact, NeutralChildExecutionContract.ContractVersion, NeutralRuntimeFeatureAvailability.Executable),
    ]);

    public NeutralVirtualizationResult<NeutralChildDomainLease> CreateChildDomain(
        NeutralDomainBindingLease parentLease, NeutralChildDomainIntent intent)
    {
        var valid = NeutralChildDomainContract.ValidateCreate(parentLease, intent);
        if (!valid.IsSuccess) return Fail<NeutralChildDomainLease>(valid);
        lock (sync)
        {
            if (parentCreateMayHaveEffect.Contains(parentLease.Handle))
                return Fail<NeutralChildDomainLease>(NeutralVirtualizationStatus.Ambiguous,
                    "Parent bind or child creation may have taken effect without exact evidence.");
            if (pendingParentBinds.Contains(parentLease.Handle))
                return Fail<NeutralChildDomainLease>(NeutralVirtualizationStatus.Denied,
                    "Parent bind admission is already in flight for this handle.");
            if (parents.Keys.Any(existing => existing.Handle == parentLease.Handle && existing != parentLease))
                return Fail<NeutralChildDomainLease>(NeutralVirtualizationStatus.Stale,
                    "Parent binding epoch changed without exact external rebind or closure.");
            ExternalChildAuthority authority = ToExternal(intent.Authority.ChildAuthority);
            if (authority == ExternalChildAuthority.None)
                return Fail<NeutralChildDomainLease>(NeutralVirtualizationStatus.Denied,
                    "No executable child authority was admitted.");
            if (!parents.TryGetValue(parentLease, out ExternalDomainLease parent))
            {
                var bindRequest = new ExternalDomainBindRequest(Guid.NewGuid(), ExternalDomainProfile.IsolatedDomain, Op());
                ExternalDomainBindResult bound;
                pendingParentBinds.Add(parentLease.Handle);
                try { bound = root.BindDomain(bindRequest); }
                catch (Exception exception) when (exception is not StackOverflowException)
                {
                    parentCreateMayHaveEffect.Add(parentLease.Handle);
                    return Fail<NeutralChildDomainLease>(NeutralVirtualizationStatus.Ambiguous,
                        $"External parent bind may have taken effect without a receipt: {exception.Message}");
                }
                finally { pendingParentBinds.Remove(parentLease.Handle); }
                if (bound.Outcome != ExternalRuntimeOutcome.Bound || bound.Receipt is not { } bindReceipt ||
                    bindReceipt.Lease.Handle.Value == Guid.Empty || bindReceipt.Lease.Epoch.Value == 0 ||
                    bindReceipt.Operation != bindRequest.Operation ||
                    bindReceipt.ContractVersion != externalFeatures.ContractVersion ||
                    bindReceipt.ManifestGeneration != externalFeatures.Generation ||
                    bindReceipt.State != ExternalDomainState.Ready)
                {
                    parentCreateMayHaveEffect.Add(parentLease.Handle);
                    return Fail<NeutralChildDomainLease>(NeutralVirtualizationStatus.Ambiguous,
                        "External parent bind lacks exact effect evidence; parent remains pinned.");
                }
                parent = bindReceipt.Lease;
                parents.Add(parentLease, parent);
            }
            var createRequest = new ExternalChildDomainCreateRequest(Guid.NewGuid(), authority,
                checked((ulong)intent.Profile.MaximumGuestMemoryBytes), Op());
            ExternalChildResult<ExternalChildDomainCreateReceipt> result;
            try { result = childRuntime.CreateChildDomain(parent, createRequest); }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                parentCreateMayHaveEffect.Add(parentLease.Handle);
                return Fail<NeutralChildDomainLease>(NeutralVirtualizationStatus.Ambiguous,
                    $"External child creation may have taken effect without a receipt: {exception.Message}");
            }
            if (parentCreateMayHaveEffect.Contains(parentLease.Handle) ||
                !parents.TryGetValue(parentLease, out ExternalDomainLease returnedParent) || returnedParent != parent)
            {
                parentCreateMayHaveEffect.Add(parentLease.Handle);
                return Fail<NeutralChildDomainLease>(NeutralVirtualizationStatus.Ambiguous,
                    "External child creation parent continuity changed during provider callback; parent remains pinned.");
            }
            if (result.Outcome != ExternalRuntimeOutcome.Bound || result.Receipt is not { } createReceipt ||
                createReceipt.Lease.Handle.Value == Guid.Empty || createReceipt.Lease.Epoch.Value == 0 ||
                createReceipt.Lease.Parent != parent || createReceipt.GrantedAuthority != authority ||
                createReceipt.GuestMemoryLimitBytes != createRequest.GuestMemoryLimitBytes ||
                createReceipt.Operation != createRequest.Operation ||
                createReceipt.ContractVersion != externalFeatures.ContractVersion ||
                createReceipt.ManifestGeneration != externalFeatures.Generation ||
                createReceipt.State != ExternalChildDomainState.Ready)
            {
                parentCreateMayHaveEffect.Add(parentLease.Handle);
                return Fail<NeutralChildDomainLease>(NeutralVirtualizationStatus.Ambiguous,
                    "External child creation lacks exact effect evidence; parent remains pinned.");
            }
            var lease = new NeutralChildDomainLease(parentLease, new(Next()), new(1), intent);
            children.Add(lease.Handle, new(lease, createReceipt.Lease, parent, NeutralChildDomainState.Created));
            return Ok(lease);
        }
    }

    public NeutralVirtualizationResult TransitionChildDomain(NeutralChildDomainLease lease, NeutralChildDomainTransition transition)
    {
        lock (sync)
        {
            if (!TryChild(lease, out Child? record, out var failure)) return failure;
            if (record!.State == NeutralChildDomainState.Closed)
                return Fail(NeutralVirtualizationStatus.Stale, "Exact child is already closed.");
            if (record!.State == NeutralChildDomainState.Faulted)
                return Fail(NeutralVirtualizationStatus.Ambiguous,
                    "Child transition outcome is quarantined pending external reconciliation.");
            if (transition == NeutralChildDomainTransition.BeginDrain)
            {
                children[lease.Handle] = record! with { State = NeutralChildDomainState.Draining };
                return Ok();
            }
            if (transition == NeutralChildDomainTransition.Start)
                return Fail(NeutralVirtualizationStatus.Denied,
                    "Executable Start requires the exact artifact/start operation contract.");
            if (pendingChildTransitions.Contains(lease.Handle) || pendingChildStarts.Contains(lease.Handle))
                return Fail(NeutralVirtualizationStatus.Denied, "Child provider transition or executable start is already in flight.");
            ExternalChildDomainTransition external = transition switch
            {
                NeutralChildDomainTransition.Park => ExternalChildDomainTransition.Park,
                NeutralChildDomainTransition.Resume => ExternalChildDomainTransition.Resume,
                _ => throw new ArgumentOutOfRangeException(nameof(transition)),
            };
            var operation = Op();
            ExternalChildResult<ExternalChildDomainTransitionReceipt> result;
            pendingChildTransitions.Add(lease.Handle);
            try { result = childRuntime.TransitionChildDomain(record!.External, external, operation); }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                FaultTransitionChild(lease.Handle);
                return Fail(NeutralVirtualizationStatus.Ambiguous,
                    $"External child transition may have taken effect without a receipt: {exception.Message}");
            }
            finally { pendingChildTransitions.Remove(lease.Handle); }
            if (!children.TryGetValue(lease.Handle, out Child? returnedChild) || returnedChild != record)
            {
                FaultTransitionChild(lease.Handle);
                return Fail(NeutralVirtualizationStatus.Ambiguous,
                    "External child transition owner continuity changed during provider callback; child remains quarantined.");
            }
            if (result.Outcome != ExternalRuntimeOutcome.Succeeded)
            {
                FaultTransitionChild(lease.Handle);
                return Fail(NeutralVirtualizationStatus.Ambiguous,
                    $"External child transition outcome requires reconciliation: {result.Outcome}: {result.Reason}");
            }
            var expectedState = transition == NeutralChildDomainTransition.Park
                ? ExternalChildDomainState.Parked : ExternalChildDomainState.Running;
            if (result.Receipt is not { } receipt ||
                receipt.Lease != record.External || receipt.Operation != operation ||
                receipt.ContractVersion != externalFeatures.ContractVersion ||
                receipt.ManifestGeneration != externalFeatures.Generation ||
                receipt.Transition != external || receipt.ResultingState != expectedState)
            {
                FaultTransitionChild(lease.Handle);
                return Fail(NeutralVirtualizationStatus.Ambiguous,
                    "External child transition may have occurred but its receipt is invalid; child is quarantined.");
            }
            var state = transition == NeutralChildDomainTransition.Park ? NeutralChildDomainState.Parked : NeutralChildDomainState.Running;
            children[lease.Handle] = record with { State = state };
            return Ok();
        }
    }

    private void FaultTransitionChild(NeutralChildDomainHandle handle)
    {
        if (children.TryGetValue(handle, out Child? currentChild))
            children[handle] = currentChild with { State = NeutralChildDomainState.Faulted };
    }

    public NeutralVirtualizationResult<NeutralChildDomainCloseReceipt> CloseChildDomain(NeutralChildDomainLease lease)
    {
        lock (sync)
        {
            if (!TryChild(lease, out Child? record, out var failure)) return Fail<NeutralChildDomainCloseReceipt>(failure);
            if (record!.State == NeutralChildDomainState.Closed)
                return Fail<NeutralChildDomainCloseReceipt>(NeutralVirtualizationStatus.Stale,
                    "Exact child is already closed.");
            if (pendingGuestMaps.ContainsKey(lease.Handle))
                return Fail<NeutralChildDomainCloseReceipt>(NeutralVirtualizationStatus.Denied,
                    "Guest mapping admission is in flight; child closure is prohibited.");
            if (pendingChildTransitions.Contains(lease.Handle))
                return Fail<NeutralChildDomainCloseReceipt>(NeutralVirtualizationStatus.Denied,
                    "Child provider transition is in flight; child closure is prohibited.");
            if (record!.GuestMapMayHaveEffect)
                return Fail<NeutralChildDomainCloseReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    "Guest mapping may exist without an exact lease or closure receipt; child remains pinned.");
            if (record.CloseMayHaveEffect)
                return Fail<NeutralChildDomainCloseReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    "External child close may have taken effect without exact evidence; retry is prohibited.");
            if (mappings.Values.Any(mapping => mapping.Neutral.ChildLease == lease))
                return Fail<NeutralChildDomainCloseReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    "Exact guest mappings must close before external child closure.");
            if (ioBindings.Values.Any(io => io.Neutral.ChildLease == lease))
                return Fail<NeutralChildDomainCloseReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    "Virtual-I/O bindings must close before external child closure.");
            var operation = Op();
            ExternalChildResult<ExternalChildDomainCloseReceipt> result;
            pendingChildCloses.Add(lease.Handle);
            try { result = childRuntime.CloseChildDomain(record.External, operation); }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                PinChildCloseUncertainty(lease.Handle);
                return Fail<NeutralChildDomainCloseReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    $"External child close may have taken effect without a receipt: {exception.Message}");
            }
            finally { pendingChildCloses.Remove(lease.Handle); }
            if (!children.TryGetValue(lease.Handle, out Child? returnedChild) || returnedChild != record)
            {
                PinChildCloseUncertainty(lease.Handle);
                return Fail<NeutralChildDomainCloseReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    "External child close owner continuity changed during provider callback; child remains pinned.");
            }
            if (result.Outcome != ExternalRuntimeOutcome.Closed || result.Receipt is not { } receipt ||
                receipt.Lease != record.External || receipt.Operation != operation ||
                receipt.ContractVersion != externalFeatures.ContractVersion ||
                receipt.ManifestGeneration != externalFeatures.Generation ||
                receipt.ResultingState != ExternalChildDomainState.Closed || !receipt.IsTerminal)
            {
                PinChildCloseUncertainty(lease.Handle);
                return Fail<NeutralChildDomainCloseReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    "External child close lacks exact terminal evidence; child remains quarantined.");
            }
            children[lease.Handle] = record with { State = NeutralChildDomainState.Closed };
            return Ok(new NeutralChildDomainCloseReceipt(lease.Handle, lease.Epoch, lease.ParentLease.Handle, lease.ParentLease.Epoch,
                NeutralChildDomainState.Closed, true));
        }
    }

    private void PinChildCloseUncertainty(NeutralChildDomainHandle handle)
    {
        if (children.TryGetValue(handle, out Child? currentChild))
            children[handle] = currentChild with { State = NeutralChildDomainState.Faulted, CloseMayHaveEffect = true };
    }

    public NeutralVirtualizationResult<NeutralGuestMappingLease> MapGuestRegion(NeutralGuestMappingRequest request)
    {
        var valid = NeutralGuestMemoryContract.ValidateMap(request, NeutralChildDomainState.Created);
        if (!valid.IsSuccess) return Fail<NeutralGuestMappingLease>(valid);
        lock (sync)
        {
            if (!TryChild(request.ChildLease, out Child? child, out var failure)) return Fail<NeutralGuestMappingLease>(failure);
            if (child!.State != NeutralChildDomainState.Created)
                return Fail<NeutralGuestMappingLease>(NeutralVirtualizationStatus.Denied,
                    "Child lifecycle must be Created for guest mapping admission.");
            var externalRequest = new ExternalGuestMemoryMapRequest(request.GuestRange.Offset,
                checked((ulong)request.GuestRange.Length), Op());
            ExternalChildResult<ExternalGuestMemoryMapReceipt> result;
            pendingGuestMaps.TryGetValue(request.ChildLease.Handle, out int pendingMaps);
            pendingGuestMaps[request.ChildLease.Handle] = checked(pendingMaps + 1);
            try { result = childRuntime.MapChildGuestMemory(child.External, externalRequest); }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                PinGuestMapUncertainty(request.ChildLease.Handle);
                return Fail<NeutralGuestMappingLease>(NeutralVirtualizationStatus.Ambiguous,
                    $"External guest map may have taken effect without a receipt: {exception.Message}");
            }
            finally
            {
                int remaining = pendingGuestMaps[request.ChildLease.Handle] - 1;
                if (remaining == 0) pendingGuestMaps.Remove(request.ChildLease.Handle);
                else pendingGuestMaps[request.ChildLease.Handle] = remaining;
            }
            if (!children.TryGetValue(request.ChildLease.Handle, out Child? returnedChild) || returnedChild != child)
            {
                PinGuestMapUncertainty(request.ChildLease.Handle);
                return Fail<NeutralGuestMappingLease>(NeutralVirtualizationStatus.Ambiguous,
                    "External guest map owner continuity changed during provider callback; mapping remains uncertain.");
            }
            if (result.Outcome != ExternalRuntimeOutcome.Succeeded || result.Receipt is null)
            {
                children[request.ChildLease.Handle] = child with
                {
                    State = NeutralChildDomainState.Faulted, GuestMapMayHaveEffect = true,
                };
                return Fail<NeutralGuestMappingLease>(NeutralVirtualizationStatus.Ambiguous,
                    $"External guest map lacks exact effect evidence: {result.Outcome}: {result.Reason}");
            }
            var externalReceipt = result.Receipt;
            if (externalReceipt.Mapping.Handle.Value == Guid.Empty || externalReceipt.Mapping.Epoch.Value == 0 ||
                externalReceipt.Mapping.Child != child.External ||
                externalReceipt.ChildOffsetBytes != externalRequest.ChildOffsetBytes ||
                externalReceipt.LengthBytes != externalRequest.LengthBytes ||
                externalReceipt.Operation != externalRequest.Operation ||
                externalReceipt.ContractVersion != externalFeatures.ContractVersion ||
                externalReceipt.ManifestGeneration != externalFeatures.Generation)
            {
                children[request.ChildLease.Handle] = child with
                {
                    State = NeutralChildDomainState.Faulted, GuestMapMayHaveEffect = true,
                };
                return Fail<NeutralGuestMappingLease>(NeutralVirtualizationStatus.Ambiguous,
                    "External guest map receipt does not match the exact child, range, operation, or provider generation.");
            }
            var lease = new NeutralGuestMappingLease(request.ChildLease, request.ParentMapping, request.GuestRange,
                request.Access, new(Next()), new(1));
            mappings.Add(lease.Handle, new(lease, externalReceipt.Mapping));
            return Ok(lease);
        }
    }

    public NeutralVirtualizationResult<NeutralGuestMappingCloseReceipt> UnmapGuestRegion(NeutralGuestMappingLease lease)
    {
        lock (sync)
        {
            if (!mappings.TryGetValue(lease.Handle, out Mapping? mapping) || mapping.Neutral != lease)
                return Fail<NeutralGuestMappingCloseReceipt>(NeutralVirtualizationStatus.Stale, "Guest mapping is absent or stale.");
            if (pendingGuestUnmaps.Contains(lease.Handle))
                return Fail<NeutralGuestMappingCloseReceipt>(NeutralVirtualizationStatus.Denied,
                    "Guest unmap admission is already in flight for this mapping.");
            if (pendingArtifactBinds.Contains(lease.Handle))
                return Fail<NeutralGuestMappingCloseReceipt>(NeutralVirtualizationStatus.Denied,
                    "Executable artifact bind admission is in flight for this mapping.");
            if (mapping.ClosureMayHaveEffect)
                return Fail<NeutralGuestMappingCloseReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    "Guest unmap may have taken effect without exact closure; retry is prohibited.");
            if (mapping.ArtifactMayHaveEffect ||
                artifacts.Values.Any(artifact => artifact.Neutral.MappingHandle == lease.Handle &&
                    artifact.Neutral.MappingEpoch == lease.Epoch))
                return Fail<NeutralGuestMappingCloseReceipt>(NeutralVirtualizationStatus.Denied,
                    "Executable artifact effect has no exact release evidence for this guest mapping.");
            var operation = Op();
            ExternalChildResult<ExternalGuestMemoryUnmapReceipt> result;
            pendingGuestUnmaps.Add(lease.Handle);
            try { result = childRuntime.UnmapChildGuestMemory(mapping.External, operation); }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                PinGuestUnmapUncertainty(mapping);
                return Fail<NeutralGuestMappingCloseReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    $"External guest unmap may have taken effect without a closure receipt: {exception.Message}");
            }
            finally { pendingGuestUnmaps.Remove(lease.Handle); }
            if (result.Outcome != ExternalRuntimeOutcome.Closed || result.Receipt is not { } receipt ||
                receipt.Mapping != mapping.External || receipt.Operation != operation ||
                receipt.ContractVersion != externalFeatures.ContractVersion ||
                receipt.ManifestGeneration != externalFeatures.Generation || !receipt.IsTerminal)
            {
                PinGuestUnmapUncertainty(mapping);
                return Fail<NeutralGuestMappingCloseReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    "External guest unmap lacks exact closure evidence; mapping remains pinned.");
            }
            mappings.Remove(lease.Handle);
            return Ok(new NeutralGuestMappingCloseReceipt(lease.Handle, lease.Epoch, lease.ChildLease.Handle, lease.ChildLease.Epoch,
                lease.ChildLease.ParentLease.Handle, lease.ChildLease.ParentLease.Epoch, true));
        }
    }

    private void PinGuestMapUncertainty(NeutralChildDomainHandle handle)
    {
        if (children.TryGetValue(handle, out Child? child))
            children[handle] = child with { State = NeutralChildDomainState.Faulted, GuestMapMayHaveEffect = true };
    }

    private void PinGuestUnmapUncertainty(Mapping mapping)
    {
        if (mappings.TryGetValue(mapping.Neutral.Handle, out Mapping? currentMapping))
            mappings[mapping.Neutral.Handle] = currentMapping with { ClosureMayHaveEffect = true };
        if (children.TryGetValue(mapping.Neutral.ChildLease.Handle, out Child? child))
            children[child.Neutral.Handle] = child with { State = NeutralChildDomainState.Faulted };
    }

    public NeutralVirtualizationResult<NeutralExecutableArtifactReceipt> BindExecutableArtifact(NeutralExecutableArtifactRequest request)
    {
        var valid = NeutralChildExecutionContract.ValidateAdmission(request);
        if (!valid.IsSuccess) return Fail<NeutralExecutableArtifactReceipt>(valid);
        lock (sync)
        {
            if (!TryChild(request.ChildLease, out Child? child, out var failure)) return Fail<NeutralExecutableArtifactReceipt>(failure);
            if (child!.State == NeutralChildDomainState.Faulted)
                return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    "Child execution outcome is quarantined pending external reconciliation.");
            if (child.State != NeutralChildDomainState.Created)
                return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.Denied,
                    "Executable artifact admission requires a Created child.");
            if (!mappings.TryGetValue(request.GuestMapping.Handle, out Mapping? mapping) || mapping.Neutral != request.GuestMapping)
                return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.Stale, "Exact guest mapping is absent or stale.");
            if (pendingGuestUnmaps.Contains(request.GuestMapping.Handle))
                return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.Denied,
                    "Guest unmap admission is in flight for this mapping.");
            if (pendingArtifactBinds.Contains(request.GuestMapping.Handle))
                return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.Denied,
                    "Executable artifact bind admission is already in flight for this mapping.");
            var externalRequest = new ExternalChildArtifactBindRequest(mapping.External,
                request.ImmutablePackage.ToArray(), request.MaximumExecutionSteps, Op());
            ExternalChildResult<ExternalChildArtifactBindReceipt> result;
            pendingArtifactBinds.Add(request.GuestMapping.Handle);
            try { result = childRuntime.BindChildExecutableArtifact(child!.External, externalRequest); }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                PinArtifactBindUncertainty(child, mapping);
                return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    $"External artifact bind may have taken effect without a receipt: {exception.Message}");
            }
            finally { pendingArtifactBinds.Remove(request.GuestMapping.Handle); }
            if (!children.TryGetValue(request.ChildLease.Handle, out Child? returnedChild) || returnedChild != child ||
                !mappings.TryGetValue(request.GuestMapping.Handle, out Mapping? returnedMapping) || returnedMapping != mapping)
            {
                PinArtifactBindUncertainty(child, mapping);
                return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    "External artifact bind owner continuity changed during provider callback; dependencies remain pinned.");
            }
            if (result.Outcome != ExternalRuntimeOutcome.Succeeded || result.Receipt is null)
            {
                PinArtifactBindUncertainty(child, mapping);
                return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    $"External artifact bind lacks exact effect evidence: {result.Outcome}: {result.Reason}");
            }
            var externalReceipt = result.Receipt;
            if (externalReceipt.ArtifactHandle.Value == Guid.Empty || externalReceipt.ArtifactEpoch.Value == 0 ||
                externalReceipt.ChildHandle != child.External.Handle || externalReceipt.ChildEpoch != child.External.Epoch ||
                externalReceipt.MappingHandle != mapping.External.Handle || externalReceipt.MappingEpoch != mapping.External.Epoch ||
                externalReceipt.Parent != child.External.Parent || externalReceipt.Operation != externalRequest.Operation ||
                externalReceipt.ContractVersion != externalFeatures.ContractVersion ||
                externalReceipt.ManifestGeneration != externalFeatures.Generation ||
                externalReceipt.MaximumPipelineCycles != externalRequest.MaximumPipelineCycles ||
                !string.Equals(externalReceipt.PackageSha256,
                    Convert.ToHexStringLower(SHA256.HashData(externalRequest.PackageBytes)), StringComparison.Ordinal))
            {
                PinArtifactBindUncertainty(child, mapping);
                return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    "External artifact receipt does not match the exact child, mapping, operation, or provider generation.");
            }
            var receipt = new NeutralExecutableArtifactReceipt(new(Next()), new(1), request.ChildLease.Handle,
                request.ChildLease.Epoch, request.GuestMapping.Handle, request.GuestMapping.Epoch,
                request.ChildLease.ParentLease.Handle, request.ChildLease.ParentLease.Epoch,
                externalReceipt.PackageSha256, request.MaximumExecutionSteps);
            var exact = NeutralChildExecutionContract.ValidateAdmissionReceipt(request, receipt);
            if (!exact.IsSuccess)
            {
                PinArtifactBindUncertainty(child, mapping);
                return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    "External artifact receipt failed neutral admission validation; dependencies remain pinned.");
            }
            artifacts.Add(receipt.ArtifactHandle, new(receipt, externalReceipt));
            return Ok(receipt);
        }
    }

    private void PinArtifactBindUncertainty(Child child, Mapping mapping)
    {
        if (children.TryGetValue(child.Neutral.Handle, out Child? currentChild))
            children[child.Neutral.Handle] = currentChild with { State = NeutralChildDomainState.Faulted,
                GuestMapMayHaveEffect = currentChild.GuestMapMayHaveEffect || !mappings.ContainsKey(mapping.Neutral.Handle) };
        if (mappings.TryGetValue(mapping.Neutral.Handle, out Mapping? currentMapping))
            mappings[mapping.Neutral.Handle] = currentMapping with { ArtifactMayHaveEffect = true };
    }

    public NeutralVirtualizationResult<NeutralChildExecutionReceipt> StartExecutableArtifact(
        NeutralChildExecutionStartRequest request)
    {
        var events = new List<SemanticTraceEventV1>(3);
        try { return StartExecutableArtifactCore(request, events); }
        finally
        {
            // Deliver committed observations after releasing the owner lock.
            // Sink failure and reentry cannot participate in start admission.
            foreach (var item in events)
            {
                try { _ = traceSink?.TryRecord(item); }
                catch { /* Observation is not execution authority. */ }
            }
        }
    }

    private NeutralVirtualizationResult<NeutralChildExecutionReceipt> StartExecutableArtifactCore(
        NeutralChildExecutionStartRequest request, List<SemanticTraceEventV1> events)
    {
        var admission = NeutralChildExecutionContract.ValidateStart(request);
        if (!admission.IsSuccess) return Fail<NeutralChildExecutionReceipt>(admission);
        lock (sync)
        {
            if (!TryChild(request.ChildLease, out Child? child, out var failure)) return Fail<NeutralChildExecutionReceipt>(failure);
            if (child!.State == NeutralChildDomainState.Faulted)
                return Fail<NeutralChildExecutionReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    "Child execution outcome is quarantined pending external reconciliation.");
            if (child.State != NeutralChildDomainState.Created)
                return Fail<NeutralChildExecutionReceipt>(NeutralVirtualizationStatus.Denied,
                    "Executable start requires a Created child.");
            if (!artifacts.TryGetValue(request.Artifact.ArtifactHandle, out Artifact? found) || found.Neutral != request.Artifact)
                return Fail<NeutralChildExecutionReceipt>(NeutralVirtualizationStatus.Stale, "Artifact admission is absent or stale.");
            if (pendingChildStarts.Contains(request.ChildLease.Handle) ||
                pendingChildTransitions.Contains(request.ChildLease.Handle))
                return Fail<NeutralChildExecutionReceipt>(NeutralVirtualizationStatus.Denied,
                    "Executable start or provider transition admission is already in flight for this child.");
            string correlation = $"hybridcpu-child-start:{request.OperationId.Value}:{request.OperationGeneration.Value}";
            string generationDigest = Digest(FormattableString.Invariant(
                $"child-start-generation/v2|{request.ChildLease.ParentLease.Handle.Value}|{request.ChildLease.ParentLease.Epoch.Value}|{request.ChildLease.Handle.Value}|{request.ChildLease.Epoch.Value}|{request.Artifact.MappingHandle.Value}|{request.Artifact.MappingEpoch.Value}|{request.Artifact.ArtifactHandle.Value}|{request.Artifact.ArtifactEpoch.Value}|{request.OperationId.Value}|{request.OperationGeneration.Value}|{externalFeatures.Generation}"));
            RecordTrace(events, correlation, 1, SemanticTraceEventKindV1.Submit, generationDigest,
                Digest($"{request.Artifact.ContentDigest}|{request.Artifact.MaximumExecutionSteps}"));
            RecordTrace(events, correlation, 2, SemanticTraceEventKindV1.EffectPossible, generationDigest,
                Digest($"{found.External.ArtifactHandle}|{found.External.ArtifactEpoch}"));
            if (!TryChild(request.ChildLease, out var currentChild, out failure) ||
                currentChild!.State != NeutralChildDomainState.Created ||
                !artifacts.TryGetValue(request.Artifact.ArtifactHandle, out var currentArtifact) ||
                currentArtifact != found)
            {
                RecordTrace(events, correlation, 3, SemanticTraceEventKindV1.Quarantined, generationDigest,
                    Digest("admission-changed-during-observation"));
                return Fail<NeutralChildExecutionReceipt>(NeutralVirtualizationStatus.Denied,
                    "Executable admission changed during trace observation.");
            }
            var operation = Op();
            ExternalChildResult<ExternalChildExecutionReceipt> result;
            pendingChildStarts.Add(request.ChildLease.Handle);
            try
            {
                result = childRuntime.StartChildExecution(child.External,
                    new(found.External.ArtifactHandle, found.External.ArtifactEpoch, operation));
            }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                if (children.TryGetValue(request.ChildLease.Handle, out var failedChild))
                    children[request.ChildLease.Handle] = failedChild with { State = NeutralChildDomainState.Faulted };
                RecordTrace(events, correlation, 3, SemanticTraceEventKindV1.Quarantined, generationDigest,
                    Digest("external-execution-callback-failed"));
                return Fail<NeutralChildExecutionReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    $"External execution may have had an effect without a receipt: {exception.Message}");
            }
            finally { pendingChildStarts.Remove(request.ChildLease.Handle); }
            if (!children.TryGetValue(request.ChildLease.Handle, out var returnedChild) ||
                returnedChild != child ||
                !artifacts.TryGetValue(request.Artifact.ArtifactHandle, out var returnedArtifact) ||
                returnedArtifact != found)
            {
                if (returnedChild is not null)
                    children[request.ChildLease.Handle] = returnedChild with { State = NeutralChildDomainState.Faulted };
                RecordTrace(events, correlation, 3, SemanticTraceEventKindV1.Quarantined, generationDigest,
                    Digest("external-start-owner-continuity-lost"));
                return Fail<NeutralChildExecutionReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    "External start owner continuity changed during the provider callback; effect remains quarantined.");
            }
            if (result.Outcome != ExternalRuntimeOutcome.Succeeded || result.Receipt is null)
            {
                children[request.ChildLease.Handle] = child with { State = NeutralChildDomainState.Faulted };
                RecordTrace(events, correlation, 3, SemanticTraceEventKindV1.Quarantined, generationDigest,
                    Digest($"{result.Outcome}|{result.Reason}"));
                return Fail<NeutralChildExecutionReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    "External execution may have had an effect; result requires reconciliation.");
            }
            var externalReceipt = result.Receipt;
            if (externalReceipt.Operation != operation ||
                externalReceipt.ContractVersion != externalFeatures.ContractVersion ||
                externalReceipt.ManifestGeneration != externalFeatures.Generation ||
                externalReceipt.ChildHandle != child.External.Handle ||
                externalReceipt.ChildEpoch != child.External.Epoch ||
                externalReceipt.Parent != child.External.Parent ||
                externalReceipt.ArtifactHandle != found.External.ArtifactHandle ||
                externalReceipt.ArtifactEpoch != found.External.ArtifactEpoch ||
                externalReceipt.MappingHandle != found.External.MappingHandle ||
                externalReceipt.MappingEpoch != found.External.MappingEpoch ||
                externalReceipt.PackageSha256 != found.External.PackageSha256 ||
                !externalReceipt.IsTerminal)
            {
                children[request.ChildLease.Handle] = child with { State = NeutralChildDomainState.Faulted };
                RecordTrace(events, correlation, 3, SemanticTraceEventKindV1.Quarantined, generationDigest,
                    Digest("external-execution-receipt-mismatch"));
                return Fail<NeutralChildExecutionReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    "External execution receipt does not match the admitted tuple; child is quarantined.");
            }
            var receipt = ToNeutral(externalReceipt, found, request);
            var exact = NeutralChildExecutionContract.ValidateExecutionReceipt(request, receipt);
            if (!exact.IsSuccess)
                children[request.ChildLease.Handle] = child with { State = NeutralChildDomainState.Faulted };
            RecordTrace(events, correlation, 3,
                exact.IsSuccess ? SemanticTraceEventKindV1.RetireOrComplete : SemanticTraceEventKindV1.Quarantined,
                generationDigest,
                Digest($"{receipt.ExecutionGeneration.Value}|{receipt.RetiredWorkUnits}|{receipt.RetiredSequence}|{receipt.LastRetiredCodeOffset}|{receipt.IsTerminal}"));
            return exact.IsSuccess ? Ok(receipt) : Fail<NeutralChildExecutionReceipt>(NeutralVirtualizationStatus.Ambiguous,
                "Execution receipt failed neutral validation; child is quarantined.");
        }
    }

    public NeutralVirtualizationResult<NeutralVirtualIoLease> BindVirtualIo(NeutralVirtualIoRequest request)
    {
        var valid = NeutralVirtualIoContract.ValidateBind(request, NeutralChildDomainState.Created);
        if (!valid.IsSuccess) return Fail<NeutralVirtualIoLease>(valid);
        return Fail<NeutralVirtualIoLease>(NeutralVirtualizationStatus.Unsupported,
            "No provider-owned external parent device identity is bound to the exact neutral device lease.");
    }

    public NeutralVirtualizationResult<NeutralVirtualIoCloseReceipt> CloseVirtualIo(NeutralVirtualIoLease lease)
    {
        lock (sync)
        {
            if (!ioBindings.TryGetValue(lease.Handle, out Io? io) || io.Neutral != lease)
                return Fail<NeutralVirtualIoCloseReceipt>(NeutralVirtualizationStatus.Stale, "I/O binding is absent or stale.");
            if (!TryChild(lease.ChildLease, out Child? child, out var failure)) return Fail<NeutralVirtualIoCloseReceipt>(failure);
            var result = childRuntime.CloseChildVirtualIo(child!.External, io.External.IoHandle, io.External.IoEpoch, Op());
            if (result.Outcome != ExternalRuntimeOutcome.Closed || result.Receipt is null || !result.Receipt.IsTerminal)
                return Fail<NeutralVirtualIoCloseReceipt>(result.Outcome, result.Reason);
            ioBindings.Remove(lease.Handle);
            return Ok(new NeutralVirtualIoCloseReceipt(lease.Handle, lease.Epoch, lease.ChildLease.Handle, lease.ChildLease.Epoch,
                lease.ChildLease.ParentLease.Handle, lease.ChildLease.ParentLease.Epoch, true));
        }
    }

    public NeutralVirtualizationResult<NeutralVirtualEventReceipt> InjectVirtualEvent(NeutralVirtualEventRequest request) =>
        Fail<NeutralVirtualEventReceipt>(NeutralVirtualizationStatus.Unsupported, "Executable event injection is not yet owned by the V3 runner.");

    private bool TryChild(NeutralChildDomainLease lease, out Child? child, out NeutralVirtualizationResult failure)
    {
        if (!children.TryGetValue(lease.Handle, out child)) { failure = Fail(NeutralVirtualizationStatus.Denied, "Child was not found."); return false; }
        if (child.Neutral.Epoch != lease.Epoch) { failure = Fail(NeutralVirtualizationStatus.Stale, "Child epoch is stale."); return false; }
        if (child.Neutral != lease) { failure = Fail(NeutralVirtualizationStatus.WrongParent, "Child parent or intent differs."); return false; }
        if (pendingChildCloses.Contains(lease.Handle))
        {
            failure = Fail(NeutralVirtualizationStatus.Denied, "Child close admission is in flight.");
            return false;
        }
        failure = default; return true;
    }

    private NeutralChildExecutionReceipt ToNeutral(
        ExternalChildExecutionReceipt receipt, Artifact artifact, NeutralChildExecutionStartRequest request)
    {
        return new(artifact.Neutral.ArtifactHandle, artifact.Neutral.ArtifactEpoch, artifact.Neutral.ChildHandle,
            artifact.Neutral.ChildEpoch, artifact.Neutral.MappingHandle, artifact.Neutral.MappingEpoch,
            artifact.Neutral.ParentHandle, artifact.Neutral.ParentEpoch, artifact.Neutral.ContentDigest,
            request.OperationId, request.OperationGeneration,
            new(receipt.ExecutionGeneration.Value), receipt.RetiredPipelineCycles, receipt.LastRetireSequence,
            receipt.LastRetiredBundleOffset, receipt.IsTerminal);
    }

    private ExternalOperationIdentity Op() => new(new(Guid.NewGuid()), new(nextOperation++));
    private void RecordTrace(List<SemanticTraceEventV1> events, string correlation, ulong sequence, SemanticTraceEventKindV1 kind,
        string generationDigest, string evidenceDigest)
    {
        if (traceSink is null) return;
        var traceEvent = new SemanticTraceEventV1(SemanticTraceEventV1.CurrentVersion, correlation, sequence,
            kind, "HybridCPU.ExternalRuntime.V3", generationDigest, evidenceDigest).Validate();
        events.Add(traceEvent);
    }

    private static string Digest(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private ulong Next() => nextIdentity++;
    private static ExternalChildAuthority ToExternal(NeutralChildAuthorityClass value) =>
        (value.HasFlag(NeutralChildAuthorityClass.Execution) ? ExternalChildAuthority.Execute : 0) |
        (value.HasFlag(NeutralChildAuthorityClass.GuestMemory) ? ExternalChildAuthority.GuestMemory : 0) |
        (value.HasFlag(NeutralChildAuthorityClass.Events) ? ExternalChildAuthority.EventInjection : 0) |
        (value.HasFlag(NeutralChildAuthorityClass.Traps) ? ExternalChildAuthority.TrapDelivery : 0) |
        (value.HasFlag(NeutralChildAuthorityClass.Io) ? ExternalChildAuthority.VirtualIo : 0);
    private static NeutralVirtualizationStatus Status(ExternalRuntimeOutcome outcome) => outcome switch
    {
        ExternalRuntimeOutcome.Unsupported => NeutralVirtualizationStatus.Unsupported,
        ExternalRuntimeOutcome.Denied => NeutralVirtualizationStatus.Denied,
        ExternalRuntimeOutcome.NotFound => NeutralVirtualizationStatus.Denied,
        ExternalRuntimeOutcome.Stale => NeutralVirtualizationStatus.Stale,
        ExternalRuntimeOutcome.Revoked => NeutralVirtualizationStatus.Revoked,
        ExternalRuntimeOutcome.Faulted => NeutralVirtualizationStatus.Faulted,
        _ => NeutralVirtualizationStatus.Ambiguous,
    };
    private static NeutralVirtualizationResult Ok() => new(NeutralVirtualizationStatus.Success, string.Empty);
    private static NeutralVirtualizationResult<T> Ok<T>(T value) => new(NeutralVirtualizationStatus.Success, value, string.Empty);
    private static NeutralVirtualizationResult Fail(NeutralVirtualizationStatus status, string reason) => new(status, reason);
    private static NeutralVirtualizationResult Fail(ExternalRuntimeOutcome outcome, string reason) => new(Status(outcome), reason);
    private static NeutralVirtualizationResult<T> Fail<T>(NeutralVirtualizationStatus status, string reason) => new(status, default!, reason);
    private static NeutralVirtualizationResult<T> Fail<T>(ExternalRuntimeOutcome outcome, string reason) => Fail<T>(Status(outcome), reason);
    private static NeutralVirtualizationResult<T> Fail<T>(NeutralVirtualizationResult failure) => new(failure.Status, default!, failure.Reason);
}
