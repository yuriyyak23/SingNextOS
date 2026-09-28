using System.Security.Cryptography;
using System.Text;
using SingPlus.Contracts;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public readonly record struct CheckpointBufferSource(OwnedBuffer<byte> Buffer);

public sealed record OrdinaryRestoreResult(
    OrdinaryRestoreReceipt Receipt,
    ComponentLifecycleSnapshot Component,
    IReadOnlyList<OwnedBuffer<byte>> RestoredBuffers);

internal sealed record CheckpointInspectionRecord(
    CheckpointHandle Handle,
    ComponentIdentity Component,
    ProcessHandle SourceProcess,
    CheckpointLifecycleState State,
    BudgetReservationHandle StorageReservation);

public sealed partial class RuntimeKernel
{
    private sealed class CheckpointRecord
    {
        public required CheckpointHandle Handle { get; init; }
        public required ComponentIdentity Component { get; init; }
        public required ProcessHandle SourceProcess { get; init; }
        public required List<CheckpointResourceDisposition> Resources { get; init; }
        public CheckpointLifecycleState State { get; set; }
        public string? Failure { get; set; }
        public OrdinaryCheckpointImage? Image { get; set; }
        public BudgetReservationHandle? StorageReservation { get; set; }
    }

    private readonly object _checkpointGate = new();
    private readonly Dictionary<CheckpointId, CheckpointRecord> _checkpoints = [];
    private ulong _nextCheckpointId = 1;

    internal CheckpointInspectionRecord[] CheckpointInspectionSnapshot()
    {
        lock (_checkpointGate)
            return _checkpoints.Values
                .Where(static checkpoint => checkpoint.StorageReservation.HasValue &&
                    checkpoint.State != CheckpointLifecycleState.Deleted)
                .Select(static checkpoint => new CheckpointInspectionRecord(
                    checkpoint.Handle,
                    checkpoint.Component,
                    checkpoint.SourceProcess,
                    checkpoint.State,
                    checkpoint.StorageReservation!.Value))
                .OrderBy(static checkpoint => checkpoint.Handle.CheckpointId.Value)
                .ToArray();
    }

    public KernelResult<OrdinaryCheckpointImage> CreateOrdinaryCheckpoint(
        ProcessHandle principal,
        CapabilityId administrationCapability,
        ComponentIdentity component,
        ReadOnlyMemory<byte> logicalState,
        IReadOnlyList<CheckpointBufferSource>? buffers = null)
    {
        var authority = ValidateCheckpointAdministration(principal, administrationCapability);
        return authority.IsSuccess
            ? CreateOrdinaryCheckpointCore(component, logicalState, buffers)
            : KernelResult<OrdinaryCheckpointImage>.Fail(authority.Error, authority.Message!);
    }

    private KernelResult<OrdinaryCheckpointImage> CreateOrdinaryCheckpointCore(
        ComponentIdentity component,
        ReadOnlyMemory<byte> logicalState,
        IReadOnlyList<CheckpointBufferSource>? buffers)
    {
        CheckpointRecord checkpoint;
        lock (_checkpointGate)
        {
            if (_nextCheckpointId == 0)
                return KernelResult<OrdinaryCheckpointImage>.Fail(KernelError.CapacityExhausted, "Checkpoint identity space is exhausted.");
            if (!_components.TryGetValue(component, out var record))
                return KernelResult<OrdinaryCheckpointImage>.Fail(KernelError.ComponentNotFound, "Component was not found.");
            checkpoint = new CheckpointRecord
            {
                Handle = new(new(_nextCheckpointId++), new(1)),
                Component = component,
                SourceProcess = record.Process,
                State = CheckpointLifecycleState.Requested,
                Resources = [],
            };
            _checkpoints.Add(checkpoint.Handle.CheckpointId, checkpoint);
        }

        if (!_components.TryGetValue(component, out var source) || source.Process != checkpoint.SourceProcess)
            return FailCheckpoint(checkpoint, KernelError.StaleGeneration, "Component generation changed during checkpoint request.");
        if (logicalState.Length > OrdinaryCheckpointContract.MaximumLogicalStateBytes)
            return FailCheckpoint(checkpoint, KernelError.CapacityExhausted, "Logical checkpoint state exceeds the bounded contract maximum.");

        checkpoint.State = CheckpointLifecycleState.Quiescing;
        var classification = ClassifyCheckpoint(source, buffers ?? []);
        checkpoint.Resources.AddRange(classification);
        var blocker = classification.FirstOrDefault(static item => item.Classification is CheckpointResourceClassification.NonCheckpointable or CheckpointResourceClassification.RequiresDrain);
        if (blocker is not null)
            return FailCheckpoint(checkpoint, KernelError.CheckpointBlocked,
                $"Checkpoint blocked by {blocker.Kind}/{blocker.Correlation}: {blocker.Reason}");

        ulong storageBytes = checked((ulong)logicalState.Length);
        foreach (var item in buffers ?? []) storageBytes = checked(storageBytes + (ulong)item.Buffer.Length);
        var reserved = Budgets.ReserveCheckpointStorage(source.Process, Math.Max(1UL, storageBytes));
        if (!reserved.IsSuccess) return FailCheckpoint(checkpoint, reserved.Error, reserved.Message!);
        checkpoint.StorageReservation = reserved.Value!.Reservation;

        checkpoint.State = CheckpointLifecycleState.Snapshotting;
        var before = SelectedDescriptors(source, buffers ?? []);
        if (!before.IsSuccess) return FailCheckpointAndRelease(checkpoint, before.Error, before.Message!);
        var regionImages = new List<CheckpointRegionImage>();
        try
        {
            var ordinal = 0;
            foreach (var item in buffers ?? [])
            {
                var content = item.Buffer.Span.ToArray();
                regionImages.Add(new(ordinal++, typeof(byte).FullName!, content, Digest(content)));
            }
        }
        catch (InvalidOperationException exception)
        {
            return FailCheckpointAndRelease(checkpoint, KernelError.CheckpointBlocked, exception.Message);
        }

        var after = SelectedDescriptors(source, buffers ?? []);
        if (!after.IsSuccess || !before.Value!.SequenceEqual(after.Value!))
            return FailCheckpointAndRelease(checkpoint, KernelError.SnapshotUnstable, "Owned-memory generation/state changed during snapshotting.");
        var sourceOwner = new RegionOwner(source.Manifest.Process.DomainId, source.Process.Generation);
        if (ExternalOperations.InspectionSnapshot().Any(operation => operation.Principal == sourceOwner && operation.State != ExternalOperationState.Released))
            return FailCheckpointAndRelease(checkpoint, KernelError.CheckpointBlocked, "An ExternalOperation became live during snapshotting.");

        checkpoint.State = CheckpointLifecycleState.Validating;
        var logical = logicalState.ToArray();
        var image = new OrdinaryCheckpointImage(
            checkpoint.Handle, CheckpointLifecycleState.Committed, component, source.Manifest.Version,
            source.Manifest.ImageDigest, source.Manifest.NormalizedDigest, source.Process, logical,
            regionImages, checkpoint.Resources.ToArray(), string.Empty, Complete: true);
        image = image with { ImageDigest = ComputeCheckpointDigest(image) };
        checkpoint.Image = Clone(image);
        checkpoint.State = CheckpointLifecycleState.Committed;
        RecordTrace(source.Process, TraceEventKind.CheckpointLifecycle, null, "checkpoint",
            checkpoint.Handle.CheckpointId.Value.ToString(), "committed", "validated");
        return KernelResult<OrdinaryCheckpointImage>.Ok(Clone(image));
    }

    public KernelResult<OrdinaryCheckpointSnapshot> QueryOrdinaryCheckpoint(
        ProcessHandle requester,
        CheckpointHandle handle,
        CapabilityId? administrationCapability = null)
    {
        var caller = Processes.Resolve(requester);
        if (!caller.IsSuccess)
            return KernelResult<OrdinaryCheckpointSnapshot>.Fail(caller.Error, caller.Message!);

        ProcessHandle sourceProcess;
        lock (_checkpointGate)
        {
            var resolved = ResolveCheckpoint(handle);
            if (!resolved.IsSuccess)
                return KernelResult<OrdinaryCheckpointSnapshot>.Fail(resolved.Error, resolved.Message!);
            sourceProcess = resolved.Value!.SourceProcess;
        }

        if (requester != sourceProcess)
        {
            if (administrationCapability is not { } capability)
                return KernelResult<OrdinaryCheckpointSnapshot>.Fail(KernelError.SupervisorDenied, "Cross-service checkpoint inspection requires the exact administration capability.");
            var authority = ValidateCheckpointAdministration(requester, capability);
            if (!authority.IsSuccess)
                return KernelResult<OrdinaryCheckpointSnapshot>.Fail(authority.Error, authority.Message!);
        }

        lock (_checkpointGate)
        {
            var resolved = ResolveCheckpoint(handle);
            return resolved.IsSuccess
                ? KernelResult<OrdinaryCheckpointSnapshot>.Ok(Snapshot(resolved.Value!))
                : KernelResult<OrdinaryCheckpointSnapshot>.Fail(resolved.Error, resolved.Message!);
        }
    }

    public KernelResult<OrdinaryRestoreResult> RestoreOrdinaryCheckpoint(
        ProcessHandle principal,
        CapabilityId administrationCapability,
        OrdinaryCheckpointImage image,
        ComponentAdmissionPlan freshAdmission)
    {
        var authority = ValidateCheckpointAdministration(principal, administrationCapability);
        return authority.IsSuccess
            ? RestoreOrdinaryCheckpointCore(image, freshAdmission)
            : KernelResult<OrdinaryRestoreResult>.Fail(authority.Error, authority.Message!);
    }

    private KernelResult<OrdinaryRestoreResult> RestoreOrdinaryCheckpointCore(
        OrdinaryCheckpointImage image,
        ComponentAdmissionPlan freshAdmission)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(freshAdmission);
        CheckpointRecord record;
        lock (_checkpointGate)
        {
            var resolved = ResolveCheckpoint(image.Handle);
            if (!resolved.IsSuccess) return KernelResult<OrdinaryRestoreResult>.Fail(resolved.Error, resolved.Message!);
            record = resolved.Value!;
            if (record.State != CheckpointLifecycleState.Committed || record.Image is null || !image.Complete || image.State != CheckpointLifecycleState.Committed)
                return KernelResult<OrdinaryRestoreResult>.Fail(KernelError.CheckpointInvalid, "Only a complete committed checkpoint image may be restored.");
        }
        if (!string.Equals(ComputeCheckpointDigest(image), image.ImageDigest, StringComparison.Ordinal) ||
            !string.Equals(record.Image.ImageDigest, image.ImageDigest, StringComparison.Ordinal))
            return KernelResult<OrdinaryRestoreResult>.Fail(KernelError.CheckpointInvalid, "Checkpoint image digest mismatch or tampering detected.");

        var manifest = freshAdmission.Manifest;
        if (manifest.Identity != image.Component || manifest.Version != image.Version ||
            !string.Equals(manifest.ImageDigest, image.ComponentImageDigest, StringComparison.Ordinal) ||
            manifest.Process.ProcessId != image.SourceProcess.ProcessId || manifest.Process.Generation <= image.SourceProcess.Generation)
            return KernelResult<OrdinaryRestoreResult>.Fail(KernelError.CheckpointIncompatible, "Restore requires compatible identity/version/image and a fresh process generation.");

        if (_components.TryGetValue(image.Component, out var existing))
        {
            if (existing.Process != image.SourceProcess || existing.State != ComponentLifecycleState.Reclaimable)
                return KernelResult<OrdinaryRestoreResult>.Fail(KernelError.CheckpointBlocked, "Source component must reach exact reclaimable state before restore.");
            var retired = RetireReclaimableComponent(image.Component, image.SourceProcess);
            if (!retired.IsSuccess) return KernelResult<OrdinaryRestoreResult>.Fail(retired.Error, retired.Message!);
        }

        var admitted = AdmitComponent(freshAdmission);
        if (!admitted.IsSuccess) return KernelResult<OrdinaryRestoreResult>.Fail(admitted.Error, admitted.Message!);
        var restored = new List<OwnedBuffer<byte>>(image.Regions.Count);
        foreach (var region in image.Regions.OrderBy(static item => item.Ordinal))
        {
            if (!string.Equals(Digest(region.Content), region.ContentDigest, StringComparison.Ordinal))
            {
                _ = FaultComponent(image.Component);
                return KernelResult<OrdinaryRestoreResult>.Fail(KernelError.CheckpointInvalid, "Checkpoint region digest mismatch.");
            }
            var allocated = AllocateBuffer<byte>(admitted.Value!.Process, region.Content.Length);
            if (!allocated.IsSuccess)
            {
                _ = FaultComponent(image.Component);
                return KernelResult<OrdinaryRestoreResult>.Fail(allocated.Error, allocated.Message!);
            }
            region.Content.CopyTo(allocated.Value!.Span);
            restored.Add(allocated.Value);
        }
        var receipt = new OrdinaryRestoreReceipt(image.Handle, image.SourceProcess, image.Component,
            admitted.Value!.Process, restored.Select(static item => item.Handle).ToArray(), image.LogicalState.ToArray());
        RecordTrace(admitted.Value.Process, TraceEventKind.CheckpointLifecycle, null, "checkpoint",
            image.Handle.CheckpointId.Value.ToString(), "restored", "fresh-admission");
        return KernelResult<OrdinaryRestoreResult>.Ok(new(receipt, admitted.Value, restored));
    }

    public KernelResult<OrdinaryRestoreResult> CheckpointDrainAndRestore(
        ProcessHandle principal,
        CapabilityId administrationCapability,
        ComponentIdentity component,
        ReadOnlyMemory<byte> logicalState,
        IReadOnlyList<CheckpointBufferSource> buffers,
        ComponentAdmissionPlan freshAdmission)
    {
        var authority = ValidateCheckpointAdministration(principal, administrationCapability);
        if (!authority.IsSuccess) return KernelResult<OrdinaryRestoreResult>.Fail(authority.Error, authority.Message!);
        var image = CreateOrdinaryCheckpointCore(component, logicalState, buffers);
        if (!image.IsSuccess) return KernelResult<OrdinaryRestoreResult>.Fail(image.Error, image.Message!);
        var drained = DrainComponent(component);
        if (!drained.IsSuccess) return KernelResult<OrdinaryRestoreResult>.Fail(drained.Error, drained.Message!);
        if (!drained.Value!.Reclaimable)
            return KernelResult<OrdinaryRestoreResult>.Fail(KernelError.CheckpointBlocked, "Source component did not reach exact reclaimable state.");
        return RestoreOrdinaryCheckpointCore(image.Value!, freshAdmission);
    }

    public KernelResult DeleteOrdinaryCheckpoint(
        ProcessHandle principal,
        CapabilityId administrationCapability,
        CheckpointHandle handle)
    {
        var authority = ValidateCheckpointAdministration(principal, administrationCapability);
        if (!authority.IsSuccess) return authority;
        lock (_checkpointGate)
        {
            var resolved = ResolveCheckpoint(handle);
            if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (record.State == CheckpointLifecycleState.Deleted) return KernelResult.Ok();
            if (record.StorageReservation is { } reservation)
            {
                var released = Budgets.Release(record.SourceProcess, reservation);
                if (!released.IsSuccess) return KernelResult.Fail(released.Error, released.Message!);
                record.StorageReservation = null;
            }
            record.Image = null;
            record.State = CheckpointLifecycleState.Deleted;
            return KernelResult.Ok();
        }
    }

    private List<CheckpointResourceDisposition> ClassifyCheckpoint(
        ComponentAdmissionRecord record,
        IReadOnlyList<CheckpointBufferSource> selected)
    {
        var result = new List<CheckpointResourceDisposition>
        {
            new(CheckpointResourceKind.Manifest, record.Manifest.NormalizedDigest, CheckpointResourceClassification.Checkpointable),
            new(CheckpointResourceKind.LogicalState, record.Manifest.Identity.Name, CheckpointResourceClassification.Checkpointable),
            new(CheckpointResourceKind.Capability, "fresh-admission", CheckpointResourceClassification.RecreateOnRestore, "Capability identities are never serialized."),
            new(CheckpointResourceKind.Budget, "fresh-admission", CheckpointResourceClassification.RecreateOnRestore),
            new(CheckpointResourceKind.TraceTelemetry, "fresh-subscription", CheckpointResourceClassification.RecreateOnRestore),
            new(CheckpointResourceKind.CancellationScope, "fresh-generation-scope", CheckpointResourceClassification.RecreateOnRestore, "Cancellation scopes are generation-bound and never serialized."),
        };
        if (record.State != ComponentLifecycleState.Running)
            result.Add(new(CheckpointResourceKind.Unknown, "component-state", CheckpointResourceClassification.NonCheckpointable, "Only a running ordinary component may enter checkpoint quiescence."));
        if (record.Manifest.CheckpointPolicy.Mode == ServiceCheckpointMode.Disabled)
            result.Add(new(CheckpointResourceKind.Manifest, "checkpoint-policy", CheckpointResourceClassification.NonCheckpointable, "Manifest checkpoint policy is disabled."));
        if (record.Manifest.Process.ExecutionRole != ExecutionRole.Sip)
            result.Add(new(CheckpointResourceKind.Unknown, "execution-role", CheckpointResourceClassification.NonCheckpointable, "Only ordinary SIP state is checkpointable."));
        if (record.PlatformBinding is not null || record.DeviceResources is not null)
            result.Add(new(CheckpointResourceKind.PlatformResource, "live-platform-authority", CheckpointResourceClassification.NonCheckpointable, "Raw platform/device/DMA state is excluded."));
        if (record.Manifest.PlatformRequirements.Any(requirement => requirement.Family == ComponentPlatformFeatureFamily.SecureDomains))
            result.Add(new(CheckpointResourceKind.SecureDomain, "secure-domain", CheckpointResourceClassification.NonCheckpointable, "Secure/private state is excluded."));
        if (record.Manifest.PlatformRequirements.Any(requirement => requirement.Family is ComponentPlatformFeatureFamily.VirtualizationDomains or ComponentPlatformFeatureFamily.NestedDomains))
            result.Add(new(CheckpointResourceKind.VirtualDomain, "virtual-domain", CheckpointResourceClassification.NonCheckpointable, "Live virtual-domain state is excluded."));
        if (record.Sessions.Count != 0)
            result.Add(new(CheckpointResourceKind.IpcSession, "active-session", CheckpointResourceClassification.RequiresDrain, "Active IPC sessions must drain before checkpoint."));
        var process = Processes.Resolve(record.Process);
        if (process.IsSuccess && process.Value!.Channels.Count != 0)
            result.Add(new(CheckpointResourceKind.IpcSession, "live-channel", CheckpointResourceClassification.RequiresDrain, "Live IPC channels must close before checkpoint."));

        var operationOwner = new RegionOwner(record.Manifest.Process.DomainId, record.Process.Generation);
        foreach (var operation in ExternalOperations.InspectionSnapshot().Where(operation => operation.Principal == operationOwner && operation.State != ExternalOperationState.Released))
            result.Add(new(CheckpointResourceKind.ExternalOperation, $"operation:{operation.Operation.OperationId.Value}", CheckpointResourceClassification.NonCheckpointable, "Live or uncontained external operation is excluded."));

        foreach (var reservation in Budgets.InspectionSnapshot().Where(reservation =>
                     reservation.Owner == record.Process && reservation.Amounts.Any(amount =>
                         amount.Dimension == ServiceBudgetDimension.ComputeTimeNanoseconds)))
            result.Add(new(CheckpointResourceKind.Budget,
                $"resource-lease:{reservation.Reservation.ReservationId.Value}:{reservation.Reservation.Generation.Value}",
                CheckpointResourceClassification.NonCheckpointable,
                "Live resource reservations and leases are never serialized or recreated from checkpoint bytes."));

        var selectedHandles = selected.Select(static item => item.Buffer.Handle).ToHashSet();
        foreach (var region in Regions.InspectionSnapshot().Where(region => region.Region.Owner == operationOwner && region.Region.State != RegionState.Released))
        {
            var correlation = $"region:{region.Region.Handle.RegionId.Value}";
            if (!selectedHandles.Contains(region.Region.Handle))
                result.Add(new(CheckpointResourceKind.OwnedMemory, correlation, CheckpointResourceClassification.NonCheckpointable, "Unknown/unselected owned memory defaults to NonCheckpointable."));
            else if (region.Region.State != RegionState.Owned || region.Borrow is not null || region.Uses.Any(static use => use.State == RegionUseState.Active) || region.PlatformMappingReserved || region.ExternalBorrowReadGrantReserved || region.BackingLease is not null)
                result.Add(new(CheckpointResourceKind.OwnedMemory, correlation, CheckpointResourceClassification.RequiresDrain, "Region has a borrow, use, mapping or external backing pin."));
            else
                result.Add(new(CheckpointResourceKind.OwnedMemory, correlation, CheckpointResourceClassification.Checkpointable));
        }
        return result;
    }

    private KernelResult<RegionDescriptor[]> SelectedDescriptors(ComponentAdmissionRecord record, IReadOnlyList<CheckpointBufferSource> selected)
    {
        var descriptors = new List<RegionDescriptor>(selected.Count);
        var owner = new RegionOwner(record.Manifest.Process.DomainId, record.Process.Generation);
        var unique = new HashSet<RegionId>();
        foreach (var source in selected)
        {
            if (source.Buffer is null || !unique.Add(source.Buffer.Handle.RegionId))
                return KernelResult<RegionDescriptor[]>.Fail(KernelError.CheckpointInvalid, "Checkpoint buffers must be non-null and unique.");
            var descriptor = Regions.Validate(source.Buffer.Handle, owner);
            if (!descriptor.IsSuccess) return KernelResult<RegionDescriptor[]>.Fail(descriptor.Error, descriptor.Message!);
            descriptors.Add(descriptor.Value!);
        }
        return KernelResult<RegionDescriptor[]>.Ok(descriptors.ToArray());
    }

    private KernelResult ValidateCheckpointAdministration(ProcessHandle principal, CapabilityId capabilityId)
    {
        var capability = ValidateCapability(principal, capabilityId, CapabilityRights.Configure);
        if (!capability.IsSuccess) return KernelResult.Fail(capability.Error, capability.Message!);
        return capability.Value!.ResourceKind == ResourceKind.KernelService &&
               capability.Value.ResourceId == CapabilityResourceIds.CheckpointAdministration
            ? KernelResult.Ok()
            : KernelResult.Fail(KernelError.SupervisorDenied, "Checkpoint mutation requires the exact administration capability.");
    }

    private KernelResult<OrdinaryCheckpointImage> FailCheckpoint(CheckpointRecord record, KernelError error, string message)
    {
        record.State = CheckpointLifecycleState.Failed;
        record.Failure = message;
        return KernelResult<OrdinaryCheckpointImage>.Fail(error, message);
    }

    private KernelResult<OrdinaryCheckpointImage> FailCheckpointAndRelease(CheckpointRecord record, KernelError error, string message)
    {
        if (record.StorageReservation is { } reservation)
        {
            _ = Budgets.Release(record.SourceProcess, reservation);
            record.StorageReservation = null;
        }
        return FailCheckpoint(record, error, message);
    }

    private KernelResult<CheckpointRecord> ResolveCheckpoint(CheckpointHandle handle)
    {
        if (!_checkpoints.TryGetValue(handle.CheckpointId, out var record))
            return KernelResult<CheckpointRecord>.Fail(KernelError.CheckpointNotFound, "Checkpoint does not exist.");
        return record.Handle == handle
            ? KernelResult<CheckpointRecord>.Ok(record)
            : KernelResult<CheckpointRecord>.Fail(KernelError.StaleGeneration, "Checkpoint generation is stale.");
    }

    private static OrdinaryCheckpointSnapshot Snapshot(CheckpointRecord record) => new(
        record.Handle, record.State, record.Component, record.SourceProcess,
        record.Resources.ToArray(), record.Failure, record.State == CheckpointLifecycleState.Committed);

    private static OrdinaryCheckpointImage Clone(OrdinaryCheckpointImage image) => image with
    {
        LogicalState = image.LogicalState.ToArray(),
        Regions = image.Regions.Select(static region => region with { Content = region.Content.ToArray() }).ToArray(),
        Resources = image.Resources.ToArray(),
    };

    private static string Digest(ReadOnlySpan<byte> content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static string ComputeCheckpointDigest(OrdinaryCheckpointImage image)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        static void Add(IncrementalHash hash, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
        }
        Add(hash, image.ContractVersion.ToString());
        Add(hash, image.Handle.CheckpointId.Value.ToString());
        Add(hash, image.Handle.Generation.Value.ToString());
        Add(hash, image.Component.Name);
        Add(hash, image.Version.Value);
        Add(hash, image.ComponentImageDigest);
        Add(hash, image.ManifestDigest);
        Add(hash, image.SourceProcess.ProcessId.Value.ToString());
        Add(hash, image.SourceProcess.Generation.ToString());
        hash.AppendData(BitConverter.GetBytes(image.LogicalState.Length));
        hash.AppendData(image.LogicalState);
        foreach (var region in image.Regions.OrderBy(static item => item.Ordinal))
        {
            Add(hash, region.Ordinal.ToString());
            Add(hash, region.ElementType);
            Add(hash, region.ContentDigest);
            hash.AppendData(BitConverter.GetBytes(region.Content.Length));
            hash.AppendData(region.Content);
        }
        foreach (var resource in image.Resources)
        {
            Add(hash, ((int)resource.Kind).ToString());
            Add(hash, resource.Correlation);
            Add(hash, ((int)resource.Classification).ToString());
            Add(hash, resource.Reason ?? string.Empty);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
