namespace YAKSys_Hybrid_CPU.Core;

public sealed partial class NeutralDomainRuntimeFacade : INeutralOriginalDmaGrantProvider
{
    public NeutralOriginalDmaGrantObservation BindDmaGrant(NeutralOriginalDmaGrantRequest request)
    {
        lock (_lifecycleGate)
        {
            NeutralOriginalDmaGrantObservation Refuse(bool matched) => new(request, matched, true,
                new() { Decision = NeutralDmaGrantDecision.Faulted, Reason = "Original DMA admission source unavailable." });
            if (request.RuntimeIncarnation != MappingClosureIncarnation) return Refuse(false);
            // Consult retained invocation before fresh active-source admission. A previous
            // possibly effected invocation never becomes no-effect because its parent disappeared.
            if (_dependencies.Devices.TryGetValue(request.Device.Handle, out var retainedDevice))
            {
                if (!retainedDevice.HasIntactOriginalDmaHistory) return new(request,true,false,new() { Decision = NeutralDmaGrantDecision.Faulted });
                var history = retainedDevice.OriginalDmaAdmissions.Where(old => old.Request.InvocationId == request.InvocationId).ToArray();
                if (history.Length != 0)
                    return history.Length == 1 && history[0].Request == request && ReferenceEquals(history[0].OwnerSelf, history[0]) &&
                        ReferenceEquals(history[0].Device, retainedDevice) && ReferenceEquals(retainedDevice.OwnerSelf, retainedDevice) &&
                        history[0].Observation is { } known && (known.NoEffectGuaranteed && history[0].Source is null ||
                            history[0].Source is { } source && OriginalDmaSources(source, history[0], request.RuntimeIncarnation, false))
                        ? known : new(request, true, false, new() { Decision = NeutralDmaGrantDecision.Faulted });
            }
            var device = _leases.Validate(request.Device); var mapping = _leases.Validate(request.Mapping);
            var parent = _leases.Validate(request.Device.DomainLease);
            if (request.InvocationId == Guid.Empty || !device.IsValid || !mapping.IsValid || !parent.IsValid ||
                request.Device.DomainLease != request.Mapping.DomainLease || !ReferenceEquals(device.State!.OriginalParent, parent.State) ||
                parent.State!.OriginalAdmission is not { } root || !ReferenceEquals(root.Source, parent.State) || root.Request.InvocationId != request.DomainBindInvocationId ||
                root.Request.RuntimeIncarnation != request.RuntimeIncarnation || mapping.State!.OriginalAdmission is not { } map ||
                !ReferenceEquals(map.Source, mapping.State) || !ReferenceEquals(map.Parent, parent.State) || map.Request.InvocationId != request.MappingAdmissionInvocationId ||
                map.Request.RuntimeIncarnation != request.RuntimeIncarnation) return Refuse(true);
            if (!device.State.HasIntactOriginalDmaHistory || device.State.OriginalDmaIssued == ulong.MaxValue)
                return new(request,true,false,new() { Decision = NeutralDmaGrantDecision.Faulted });
            device.State.OriginalDmaAdmissions.EnsureCapacity(device.State.OriginalDmaAdmissions.Count + 1);
            var fact = new NeutralOriginalDmaAdmissionFact(request, device.State, mapping.State, parent.State);
            device.State.OriginalDmaAdmissions.Add(fact); device.State.OriginalDmaIssued++; // Original intent before possible Bind effect.
            var result = BindDmaGrant(request.Device, request.Mapping, request.Range, request.Direction);
            if (result is { IsGranted: true, Decision: NeutralDmaGrantDecision.Granted })
            {
                fact.Source = _dependencies.Dma[result.Grant.Handle]; fact.Source.OriginalAdmission = fact;
            }
            var raw = new NeutralOriginalDmaGrantObservation(request, true, !result.IsGranted && result.Grant == default, result);
            fact.Observation = raw;
            return raw;
        }
    }

    public NeutralOriginalDmaGrantObservation? ResolveDmaGrantBind(NeutralOriginalDmaGrantRequest request)
    {
        lock (_lifecycleGate)
        {
            if (request.RuntimeIncarnation != MappingClosureIncarnation || request.InvocationId == Guid.Empty ||
                !_dependencies.Devices.TryGetValue(request.Device.Handle, out var device) || device.Lease != request.Device || !ReferenceEquals(device.OwnerSelf, device) || !device.HasIntactOriginalDmaHistory) return null;
            var fact = device.OriginalDmaAdmissions.SingleOrDefault(old => old.Request.InvocationId == request.InvocationId);
            if (fact is null || fact.Request != request || !ReferenceEquals(fact.OwnerSelf, fact) || !ReferenceEquals(fact.Device, device) || fact.Observation is not { } raw || raw.Request != request) return null;
            if (raw.NoEffectGuaranteed && fact.Source is null && raw.Observation is { IsGranted: false } && raw.Observation.Grant == default) return raw;
            return fact.Source is { } source && _dependencies.Dma.TryGetValue(source.Handle, out var routed) && ReferenceEquals(source, routed) &&
                OriginalDmaSources(source, fact, request.RuntimeIncarnation, false) ? raw : null;
        }
    }

    public NeutralOriginalDmaGrantCloseObservation CloseDmaGrant(NeutralOriginalDmaGrantCloseRequest request)
    {
        lock (_lifecycleGate)
        {
            NeutralOriginalDmaGrantCloseObservation Refuse(bool matched, bool noEffect) => new(request, matched, noEffect,
                new() { Decision = NeutralDmaGrantCloseDecision.Faulted, Reason = "Original DMA Close source unavailable." });
            if (request.RuntimeIncarnation != MappingClosureIncarnation) return Refuse(false, true);
            if (_dependencies.Dma.TryGetValue(request.Grant.Handle, out var retained) && retained.Lease == request.Grant &&
                retained.OriginalClose is { } prior && prior.Request.InvocationId == request.InvocationId)
                return prior.Request == request && ReferenceEquals(prior.OwnerSelf, prior) && ReferenceEquals(prior.Source, retained) &&
                    prior.Observation is { } known && OriginalDmaSources(retained, prior.Admission, request.RuntimeIncarnation, false)
                    ? known : Refuse(true, false);
            var valid = _leases.Validate(request.Grant);
            if (request.InvocationId == Guid.Empty || !valid.IsValid || valid.State!.OriginalAdmission is not { } admission ||
                !OriginalDmaSources(valid.State, admission, request.RuntimeIncarnation) || admission.Request.InvocationId != request.DmaBindInvocationId)
                return Refuse(true, true);
            if (valid.State.OriginalClose is not null) return Refuse(true, false); // Existing invocation may have effected; lookup only.
            if (valid.State.PendingCopy is not null) return Refuse(true,true);
            var fact = new NeutralOriginalDmaCloseFact(request, valid.State, admission);
            valid.State.OriginalClose = fact; // Before mutation under the same producing lock.
            valid.State.Lifecycle = NeutralResourceLifecycle.Revoked; valid.State.Visibility = null;
            var raw = new NeutralOriginalDmaGrantCloseObservation(request, true, false, new() { Decision = NeutralDmaGrantCloseDecision.Closed });
            fact.Observation = raw; return raw;
        }
    }

    public NeutralOriginalDmaGrantCloseObservation? ResolveDmaGrantClose(NeutralOriginalDmaGrantCloseRequest request)
    {
        lock (_lifecycleGate)
        {
            if (request.RuntimeIncarnation != MappingClosureIncarnation || request.InvocationId == Guid.Empty ||
                !_dependencies.Dma.TryGetValue(request.Grant.Handle, out var source) || source.Lease != request.Grant || source.IsActive ||
                source.OriginalClose is not { } fact || fact.Request != request || !ReferenceEquals(fact.OwnerSelf, fact) || !ReferenceEquals(fact.Source, source) ||
                source.OriginalAdmission is not { } admission || !ReferenceEquals(fact.Admission, admission) ||
                admission.Request.InvocationId != request.DmaBindInvocationId || !OriginalDmaSources(source, admission, request.RuntimeIncarnation, false)) return null;
            return fact.Observation; // Pure same original surviving outcome, never an effect or absence-derived closure.
        }
    }

    private bool OriginalDmaSources(NeutralDmaGrantState source, NeutralOriginalDmaAdmissionFact admission, Guid incarnation, bool requireActiveParents = true) =>
        ReferenceEquals(source.OwnerSelf, source) && ReferenceEquals(admission.OwnerSelf, admission) && ReferenceEquals(admission.Source, source) &&
        admission.Request.RuntimeIncarnation == incarnation && admission.Observation is { SourceFenceMatched: true, NoEffectGuaranteed: false } raw &&
        raw.Request == admission.Request && raw.Observation is { IsGranted: true, Decision: NeutralDmaGrantDecision.Granted } && raw.Observation.Grant == source.Lease &&
        _dependencies.Devices.TryGetValue(source.Lease.DeviceLease.Handle, out var device) && ReferenceEquals(device, admission.Device) && ReferenceEquals(device.OwnerSelf, device) &&
        ReferenceEquals(device.OriginalAdmission,admission.DeviceAdmission) && (device.OriginalAdmission is not { } deviceAdmission || OriginalDeviceSources(device,deviceAdmission,incarnation,requireActiveParents)) &&
        (!requireActiveParents || device.IsActive) && device.Lease == source.Lease.DeviceLease && device.HasIntactOriginalDmaHistory && device.OriginalDmaAdmissions.Count(fact => ReferenceEquals(fact, admission)) == 1 &&
        _dependencies.Mappings.TryGetValue(source.Lease.MappingLease.Handle, out var mapping) && ReferenceEquals(mapping, admission.Mapping) && (!requireActiveParents || mapping.IsActive) &&
        _dependencies.Domains.TryGetValue(source.Lease.DeviceLease.DomainLease.Handle, out var parent) && ReferenceEquals(parent, admission.Parent) && (!requireActiveParents || parent.IsActive) &&
        ReferenceEquals(device.OriginalParent, parent) && parent.OriginalAdmission is { } root && ReferenceEquals(root.Source, parent) && ReferenceEquals(root, admission.RootAdmission) &&
        root.Request.InvocationId == admission.Request.DomainBindInvocationId && mapping.OriginalAdmission is { } map && ReferenceEquals(map.Source, mapping) && ReferenceEquals(map, admission.MappingAdmission) &&
        ReferenceEquals(map.Parent, parent) && map.Request.InvocationId == admission.Request.MappingAdmissionInvocationId;
}
