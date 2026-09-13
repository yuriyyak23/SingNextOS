namespace YAKSys_Hybrid_CPU.Core;

public enum NeutralVirtualizationStatus
{
    Success = 0,
    Unavailable,
    Unsupported,
    Denied,
    Stale,
    Revoked,
    WrongParent,
    WrongOwner,
    Faulted,
    Ambiguous,
}

public readonly record struct NeutralVirtualizationResult(
    NeutralVirtualizationStatus Status,
    string Reason)
{
    public bool IsSuccess => Status == NeutralVirtualizationStatus.Success;
}

public readonly record struct NeutralVirtualizationResult<T>(
    NeutralVirtualizationStatus Status,
    T Value,
    string Reason)
{
    public bool IsSuccess => Status == NeutralVirtualizationStatus.Success;
}

[Flags]
public enum NeutralChildAuthorityClass
{
    None = 0,
    Lifecycle = 1 << 0,
    Execution = 1 << 1,
    GuestMemory = 1 << 2,
    Events = 1 << 3,
    Traps = 1 << 4,
    Io = 1 << 5,
}

public readonly record struct NeutralChildAuthoritySubset(
    NeutralChildAuthorityClass ParentAuthority,
    NeutralChildAuthorityClass ChildAuthority);

public readonly record struct NeutralChildDomainProfile(
    int VirtualProcessorCount,
    long MaximumGuestMemoryBytes);

public readonly record struct NeutralChildDomainIntent(
    NeutralChildDomainProfile Profile,
    NeutralChildAuthoritySubset Authority);

public readonly record struct NeutralChildDomainHandle(ulong Value);
public readonly record struct NeutralChildDomainEpoch(ulong Value);

public readonly record struct NeutralChildDomainLease(
    NeutralDomainBindingLease ParentLease,
    NeutralChildDomainHandle Handle,
    NeutralChildDomainEpoch Epoch,
    NeutralChildDomainIntent Intent);

public enum NeutralChildDomainTransition
{
    Start = 0,
    Park,
    Resume,
    BeginDrain,
}

public enum NeutralChildDomainState
{
    Created = 0,
    Running,
    Parked,
    Draining,
    Closed,
    Revoked,
    Faulted,
}

/// <summary>Definitive closure evidence. It is not authority and cannot authorize another operation.</summary>
public readonly record struct NeutralChildDomainCloseReceipt(
    NeutralChildDomainHandle ChildHandle,
    NeutralChildDomainEpoch ChildEpoch,
    NeutralDomainBindingHandle ParentHandle,
    NeutralDomainBindingEpoch ParentEpoch,
    NeutralChildDomainState ResultingState,
    bool IsTerminal);

public static class NeutralChildDomainContract
{
    public const uint ContractVersion = 1;

    private const NeutralChildAuthorityClass KnownAuthority =
        NeutralChildAuthorityClass.Lifecycle |
        NeutralChildAuthorityClass.Execution |
        NeutralChildAuthorityClass.GuestMemory |
        NeutralChildAuthorityClass.Events |
        NeutralChildAuthorityClass.Traps |
        NeutralChildAuthorityClass.Io;

    public static NeutralVirtualizationResult ValidateCreate(
        NeutralDomainBindingLease parentLease,
        NeutralChildDomainIntent intent)
    {
        if (parentLease.Handle.Value == 0 || parentLease.Epoch.Value == 0)
            return Fail(NeutralVirtualizationStatus.Denied, "Parent identity is empty or unmaterialized.");

        if (intent.Profile.VirtualProcessorCount <= 0 || intent.Profile.MaximumGuestMemoryBytes <= 0)
            return Fail(NeutralVirtualizationStatus.Denied, "Child profile bounds must be positive.");

        var parent = intent.Authority.ParentAuthority;
        var child = intent.Authority.ChildAuthority;
        if (parent == NeutralChildAuthorityClass.None || child == NeutralChildAuthorityClass.None ||
            (parent & ~KnownAuthority) != 0 || (child & ~KnownAuthority) != 0)
            return Fail(NeutralVirtualizationStatus.Denied, "Authority must be a non-empty known semantic subset.");

        if ((parent & NeutralChildAuthorityClass.Lifecycle) == 0 ||
            (child & NeutralChildAuthorityClass.Lifecycle) == 0 ||
            (child & parent) != child)
            return Fail(NeutralVirtualizationStatus.Denied, "Child authority cannot exceed its parent and must retain lifecycle control.");

        return Ok();
    }

    public static NeutralVirtualizationResult ValidateLease(
        NeutralDomainBindingLease expectedParent,
        NeutralChildDomainIntent expectedIntent,
        NeutralChildDomainLease lease)
    {
        var create = ValidateCreate(expectedParent, expectedIntent);
        if (!create.IsSuccess) return create;
        if (lease.Handle.Value == 0)
            return Fail(NeutralVirtualizationStatus.Faulted, "Child identity is empty.");
        if (lease.Epoch.Value == 0)
            return Fail(NeutralVirtualizationStatus.Stale, "Child epoch is empty or stale.");
        if (lease.ParentLease.Handle != expectedParent.Handle)
            return Fail(NeutralVirtualizationStatus.WrongParent, "Child belongs to another parent.");
        if (lease.ParentLease.Epoch != expectedParent.Epoch)
            return Fail(NeutralVirtualizationStatus.Stale, "Parent epoch is stale.");
        if (lease.Intent != expectedIntent)
            return Fail(NeutralVirtualizationStatus.Faulted, "Child lease does not match the admitted intent.");
        return Ok();
    }

    public static NeutralVirtualizationResult ValidateEffectState(NeutralChildDomainState state)
    {
        if (!Enum.IsDefined(state)) return Fail(NeutralVirtualizationStatus.Faulted, "Child state is unknown.");
        return state switch
        {
            NeutralChildDomainState.Created or NeutralChildDomainState.Running or NeutralChildDomainState.Parked => Ok(),
            NeutralChildDomainState.Draining or NeutralChildDomainState.Closed => Fail(NeutralVirtualizationStatus.Denied, "Child is draining or closed."),
            NeutralChildDomainState.Revoked => Fail(NeutralVirtualizationStatus.Revoked, "Child authority is revoked."),
            NeutralChildDomainState.Faulted => Fail(NeutralVirtualizationStatus.Faulted, "Child is faulted."),
            _ => Fail(NeutralVirtualizationStatus.Faulted, "Child state is unknown."),
        };
    }

    public static NeutralVirtualizationResult ValidateClose(
        NeutralChildDomainLease expectedLease,
        NeutralVirtualizationResult<NeutralChildDomainCloseReceipt> result)
    {
        if (!Enum.IsDefined(result.Status) || result.Status != NeutralVirtualizationStatus.Success)
            return Fail(NormalizeFailure(result.Status), "Close did not return definitive success.");
        var receipt = result.Value;
        if (receipt.ChildHandle != expectedLease.Handle)
            return Fail(NeutralVirtualizationStatus.Faulted, "Close receipt identifies another child.");
        if (receipt.ChildEpoch != expectedLease.Epoch || receipt.ParentEpoch != expectedLease.ParentLease.Epoch)
            return Fail(NeutralVirtualizationStatus.Stale, "Close receipt does not match the exact child or parent epoch.");
        if (receipt.ParentHandle != expectedLease.ParentLease.Handle)
            return Fail(NeutralVirtualizationStatus.WrongParent, "Close receipt identifies another parent.");
        if (!receipt.IsTerminal || receipt.ResultingState != NeutralChildDomainState.Closed)
            return Fail(NeutralVirtualizationStatus.Ambiguous, "Close receipt is not terminal closure evidence.");
        return Ok();
    }

    internal static NeutralVirtualizationStatus NormalizeFailure(NeutralVirtualizationStatus status) =>
        Enum.IsDefined(status) && status != NeutralVirtualizationStatus.Success
            ? status
            : NeutralVirtualizationStatus.Faulted;

    internal static NeutralVirtualizationResult Ok() => new(NeutralVirtualizationStatus.Success, string.Empty);
    internal static NeutralVirtualizationResult Fail(NeutralVirtualizationStatus status, string reason) => new(status, reason);
}

[Flags]
public enum NeutralGuestMemoryAccess
{
    None = 0,
    Read = 1 << 0,
    Write = 1 << 1,
    Execute = 1 << 2,
}

public readonly record struct NeutralGuestAddressRange(ulong Offset, long Length);
public readonly record struct NeutralGuestMappingHandle(ulong Value);
public readonly record struct NeutralGuestMappingEpoch(ulong Value);

public readonly record struct NeutralGuestMappingRequest(
    NeutralChildDomainLease ChildLease,
    NeutralOwnedRegionMappingLease ParentMapping,
    NeutralGuestAddressRange GuestRange,
    NeutralGuestMemoryAccess Access);

public readonly record struct NeutralGuestMappingLease(
    NeutralChildDomainLease ChildLease,
    NeutralOwnedRegionMappingLease ParentMapping,
    NeutralGuestAddressRange GuestRange,
    NeutralGuestMemoryAccess Access,
    NeutralGuestMappingHandle Handle,
    NeutralGuestMappingEpoch Epoch);

/// <summary>Terminal unmap evidence; never a memory capability or reuse grant.</summary>
public readonly record struct NeutralGuestMappingCloseReceipt(
    NeutralGuestMappingHandle MappingHandle,
    NeutralGuestMappingEpoch MappingEpoch,
    NeutralChildDomainHandle ChildHandle,
    NeutralChildDomainEpoch ChildEpoch,
    NeutralDomainBindingHandle ParentHandle,
    NeutralDomainBindingEpoch ParentEpoch,
    bool IsTerminal);

public static class NeutralGuestMemoryContract
{
    public const uint ContractVersion = 1;

    public static NeutralVirtualizationResult ValidateMap(
        NeutralGuestMappingRequest request,
        NeutralChildDomainState state)
    {
        var child = NeutralChildDomainContract.ValidateLease(request.ChildLease.ParentLease, request.ChildLease.Intent, request.ChildLease);
        if (!child.IsSuccess) return child;
        var effect = NeutralChildDomainContract.ValidateEffectState(state);
        if (!effect.IsSuccess) return effect;
        if ((request.ChildLease.Intent.Authority.ChildAuthority & NeutralChildAuthorityClass.GuestMemory) == 0)
            return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.Denied, "Child has no guest-memory authority.");
        if (request.ParentMapping.DomainLease != request.ChildLease.ParentLease)
            return NeutralChildDomainContract.Fail(
                request.ParentMapping.DomainLease.Handle == request.ChildLease.ParentLease.Handle
                    ? NeutralVirtualizationStatus.Stale : NeutralVirtualizationStatus.WrongOwner,
                "Parent mapping is not owned by the exact parent lease.");
        if (request.ParentMapping.Handle.Value == 0 || request.ParentMapping.Epoch.Value == 0)
            return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.Stale, "Parent mapping identity is empty or stale.");
        if (request.GuestRange.Length <= 0 ||
            request.GuestRange.Offset > ulong.MaxValue - (ulong)request.GuestRange.Length ||
            request.GuestRange.Offset + (ulong)request.GuestRange.Length > (ulong)request.ChildLease.Intent.Profile.MaximumGuestMemoryBytes ||
            request.GuestRange.Length > request.ParentMapping.Slice.Length)
            return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.Denied, "Guest range exceeds the child bound or exact parent-owned range.");
        const NeutralGuestMemoryAccess known = NeutralGuestMemoryAccess.Read | NeutralGuestMemoryAccess.Write | NeutralGuestMemoryAccess.Execute;
        if (request.Access == NeutralGuestMemoryAccess.None || (request.Access & ~known) != 0)
            return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.Denied, "Guest access is empty or unknown.");
        if ((request.Access & NeutralGuestMemoryAccess.Read) != 0 && (request.ParentMapping.Slice.Access & NeutralMemoryAccess.Read) == 0 ||
            (request.Access & NeutralGuestMemoryAccess.Write) != 0 && (request.ParentMapping.Slice.Access & NeutralMemoryAccess.Write) == 0 ||
            (request.Access & NeutralGuestMemoryAccess.Execute) != 0 && (request.ChildLease.Intent.Authority.ChildAuthority & NeutralChildAuthorityClass.Execution) == 0)
            return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.Denied, "Guest access exceeds exact parent mapping or child execution authority.");
        return NeutralChildDomainContract.Ok();
    }

    public static NeutralVirtualizationResult ValidateLease(NeutralGuestMappingRequest request, NeutralGuestMappingLease lease)
    {
        var map = ValidateMap(request, NeutralChildDomainState.Created);
        if (!map.IsSuccess) return map;
        if (lease.Handle.Value == 0) return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.Faulted, "Guest mapping identity is empty.");
        if (lease.Epoch.Value == 0) return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.Stale, "Guest mapping epoch is empty or stale.");
        if (lease.ChildLease != request.ChildLease || lease.ParentMapping != request.ParentMapping ||
            lease.GuestRange != request.GuestRange || lease.Access != request.Access)
            return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.Faulted, "Guest mapping lease does not match the exact request.");
        return NeutralChildDomainContract.Ok();
    }

    public static NeutralVirtualizationResult ValidateClose(
        NeutralGuestMappingLease expectedLease,
        NeutralVirtualizationResult<NeutralGuestMappingCloseReceipt> result)
    {
        if (!Enum.IsDefined(result.Status) || result.Status != NeutralVirtualizationStatus.Success)
            return NeutralChildDomainContract.Fail(NeutralChildDomainContract.NormalizeFailure(result.Status), "Unmap did not return definitive success.");
        if (result.Value.MappingHandle != expectedLease.Handle)
            return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.Faulted, "Unmap receipt identifies another mapping.");
        if (result.Value.MappingEpoch != expectedLease.Epoch || result.Value.ChildEpoch != expectedLease.ChildLease.Epoch)
            return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.Stale, "Unmap receipt has a stale mapping epoch.");
        if (result.Value.ChildHandle != expectedLease.ChildLease.Handle)
            return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.WrongParent, "Unmap receipt identifies another child.");
        if (result.Value.ParentHandle != expectedLease.ChildLease.ParentLease.Handle)
            return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.WrongParent, "Unmap receipt identifies another parent.");
        if (result.Value.ParentEpoch != expectedLease.ChildLease.ParentLease.Epoch)
            return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.Stale, "Unmap receipt has a stale parent epoch.");
        if (!result.Value.IsTerminal)
            return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.Ambiguous, "Unmap receipt is not exact terminal evidence.");
        return NeutralChildDomainContract.Ok();
    }
}

public enum NeutralVirtualEventClass { ExternalSignal = 0, Timer, Preemption }
public readonly record struct NeutralVirtualEventRequest(NeutralChildDomainLease ChildLease, NeutralVirtualEventClass EventClass, string SourceResourceId);
public readonly record struct NeutralVirtualEventReceipt(NeutralChildDomainHandle ChildHandle, NeutralChildDomainEpoch ChildEpoch, NeutralDomainBindingHandle ParentHandle, NeutralDomainBindingEpoch ParentEpoch, ulong Sequence, NeutralVirtualEventClass EventClass, string SourceResourceId);

public static class NeutralVirtualEventContract
{
    public const uint ContractVersion = 1;
    public static NeutralVirtualizationResult ValidateRequest(NeutralVirtualEventRequest request, NeutralChildDomainState state)
    {
        var child = NeutralChildDomainContract.ValidateLease(request.ChildLease.ParentLease, request.ChildLease.Intent, request.ChildLease);
        if (!child.IsSuccess) return child;
        var effect = NeutralChildDomainContract.ValidateEffectState(state);
        if (!effect.IsSuccess) return effect;
        if ((request.ChildLease.Intent.Authority.ChildAuthority & NeutralChildAuthorityClass.Events) == 0 ||
            !Enum.IsDefined(request.EventClass) || string.IsNullOrWhiteSpace(request.SourceResourceId) || request.SourceResourceId.Length > 256)
            return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.Denied, "Event authority and bounded semantic identity are required.");
        return NeutralChildDomainContract.Ok();
    }
    public static NeutralVirtualizationResult ValidateReceipt(NeutralVirtualEventRequest request, NeutralVirtualEventReceipt receipt)
    {
        if (receipt.ChildHandle != request.ChildLease.Handle) return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.WrongParent, "Event receipt belongs to another child.");
        if (receipt.ChildEpoch != request.ChildLease.Epoch) return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.Stale, "Event receipt belongs to another child epoch.");
        if (receipt.ParentHandle != request.ChildLease.ParentLease.Handle) return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.WrongParent, "Event receipt belongs to another parent.");
        if (receipt.ParentEpoch != request.ChildLease.ParentLease.Epoch) return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.Stale, "Event receipt belongs to another parent epoch.");
        if (receipt.Sequence == 0 || receipt.EventClass != request.EventClass || receipt.SourceResourceId != request.SourceResourceId) return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.Faulted, "Event receipt is not exact evidence.");
        return NeutralChildDomainContract.Ok();
    }
}

public enum NeutralVirtualTrapKind
{
    MemoryFault = 0,
    IllegalInstruction,
    Hypercall,
    ExternalEvent,
    Timer,
    Preemption,
    DeviceOrIoFault,
}

/// <summary>Semantic trap evidence. It is neither a capability nor completion authority.</summary>
public readonly record struct NeutralVirtualTrapEvidence(
    NeutralChildDomainHandle ChildHandle,
    NeutralChildDomainEpoch ChildEpoch,
    NeutralDomainBindingHandle ParentHandle,
    NeutralDomainBindingEpoch ParentEpoch,
    ulong Sequence,
    NeutralVirtualTrapKind Kind);

public static class NeutralVirtualTrapContract
{
    public const uint ContractVersion = 1;
    public static NeutralVirtualizationResult ValidateEvidence(NeutralChildDomainLease expectedChild, NeutralVirtualTrapEvidence evidence)
    {
        var child = NeutralChildDomainContract.ValidateLease(expectedChild.ParentLease, expectedChild.Intent, expectedChild);
        if (!child.IsSuccess) return child;
        if (evidence.ChildHandle != expectedChild.Handle)
            return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.WrongParent, "Trap evidence belongs to another child.");
        if (evidence.ChildEpoch != expectedChild.Epoch)
            return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.Stale, "Trap evidence belongs to another child epoch.");
        if (evidence.ParentHandle != expectedChild.ParentLease.Handle)
            return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.WrongParent, "Trap evidence belongs to another parent.");
        if (evidence.ParentEpoch != expectedChild.ParentLease.Epoch)
            return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.Stale, "Trap evidence belongs to another parent epoch.");
        if (evidence.Sequence == 0 || !Enum.IsDefined(evidence.Kind))
            return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.Faulted, "Trap evidence is empty or unknown.");
        return NeutralChildDomainContract.Ok();
    }
}

public readonly record struct NeutralVirtualIoProfile(NeutralDeviceRights Rights, long MaximumTransferBytes);
public readonly record struct NeutralVirtualIoRequest(NeutralChildDomainLease ChildLease, NeutralDeviceLease ParentDeviceLease, NeutralVirtualIoProfile Profile);
public readonly record struct NeutralVirtualIoHandle(ulong Value);
public readonly record struct NeutralVirtualIoEpoch(ulong Value);
public readonly record struct NeutralVirtualIoLease(NeutralChildDomainLease ChildLease, NeutralDeviceLease ParentDeviceLease, NeutralVirtualIoProfile Profile, NeutralVirtualIoHandle Handle, NeutralVirtualIoEpoch Epoch);
public readonly record struct NeutralVirtualIoCloseReceipt(NeutralVirtualIoHandle IoHandle, NeutralVirtualIoEpoch IoEpoch, NeutralChildDomainHandle ChildHandle, NeutralChildDomainEpoch ChildEpoch, NeutralDomainBindingHandle ParentHandle, NeutralDomainBindingEpoch ParentEpoch, bool IsTerminal);

public static class NeutralVirtualIoContract
{
    public const uint ContractVersion = 1;
    public static NeutralVirtualizationResult ValidateBind(NeutralVirtualIoRequest request, NeutralChildDomainState state)
    {
        var child = NeutralChildDomainContract.ValidateLease(request.ChildLease.ParentLease, request.ChildLease.Intent, request.ChildLease);
        if (!child.IsSuccess) return child;
        var effect = NeutralChildDomainContract.ValidateEffectState(state);
        if (!effect.IsSuccess) return effect;
        if ((request.ChildLease.Intent.Authority.ChildAuthority & NeutralChildAuthorityClass.Io) == 0)
            return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.Denied, "Child has no virtual-I/O authority.");
        if (request.ParentDeviceLease.DomainLease != request.ChildLease.ParentLease)
            return NeutralChildDomainContract.Fail(
                request.ParentDeviceLease.DomainLease.Handle == request.ChildLease.ParentLease.Handle
                    ? NeutralVirtualizationStatus.Stale : NeutralVirtualizationStatus.WrongOwner,
                "Device is not owned by the exact parent lease.");
        if (request.ParentDeviceLease.Handle.Value == 0 || request.ParentDeviceLease.Epoch.Value == 0 ||
            request.Profile.Rights == NeutralDeviceRights.None ||
            (request.Profile.Rights & ~request.ParentDeviceLease.Rights) != 0 || request.Profile.MaximumTransferBytes <= 0)
            return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.Denied, "Virtual-I/O identity, rights, and bound are invalid or exceed parent authority.");
        return NeutralChildDomainContract.Ok();
    }
    public static NeutralVirtualizationResult ValidateLease(NeutralVirtualIoRequest request, NeutralVirtualIoLease lease)
    {
        var bind = ValidateBind(request, NeutralChildDomainState.Created);
        if (!bind.IsSuccess) return bind;
        if (lease.Handle.Value == 0) return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.Faulted, "Virtual-I/O identity is empty.");
        if (lease.Epoch.Value == 0) return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.Stale, "Virtual-I/O epoch is empty or stale.");
        if (lease.ChildLease != request.ChildLease || lease.ParentDeviceLease != request.ParentDeviceLease || lease.Profile != request.Profile)
            return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.Faulted, "Virtual-I/O lease does not match the exact request.");
        return NeutralChildDomainContract.Ok();
    }
    public static NeutralVirtualizationResult ValidateClose(NeutralVirtualIoLease expectedLease, NeutralVirtualizationResult<NeutralVirtualIoCloseReceipt> result)
    {
        if (!Enum.IsDefined(result.Status) || result.Status != NeutralVirtualizationStatus.Success)
            return NeutralChildDomainContract.Fail(NeutralChildDomainContract.NormalizeFailure(result.Status), "Virtual-I/O close did not return definitive success.");
        if (result.Value.IoHandle != expectedLease.Handle) return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.Faulted, "Close receipt identifies another I/O binding.");
        if (result.Value.IoEpoch != expectedLease.Epoch || result.Value.ChildEpoch != expectedLease.ChildLease.Epoch) return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.Stale, "Close receipt has a stale I/O epoch.");
        if (result.Value.ChildHandle != expectedLease.ChildLease.Handle) return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.WrongParent, "Close receipt identifies another child.");
        if (result.Value.ParentHandle != expectedLease.ChildLease.ParentLease.Handle) return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.WrongParent, "Close receipt identifies another parent.");
        if (result.Value.ParentEpoch != expectedLease.ChildLease.ParentLease.Epoch) return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.Stale, "Close receipt has a stale parent epoch.");
        if (!result.Value.IsTerminal) return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.Ambiguous, "Close receipt is not exact terminal evidence.");
        return NeutralChildDomainContract.Ok();
    }
}

public interface INeutralChildDomainProvider
{
    NeutralVirtualizationResult<NeutralChildDomainLease> CreateChildDomain(NeutralDomainBindingLease parentLease, NeutralChildDomainIntent intent);
    NeutralVirtualizationResult TransitionChildDomain(NeutralChildDomainLease lease, NeutralChildDomainTransition transition);
    NeutralVirtualizationResult<NeutralChildDomainCloseReceipt> CloseChildDomain(NeutralChildDomainLease lease);
}

public interface INeutralGuestMemoryProvider
{
    NeutralVirtualizationResult<NeutralGuestMappingLease> MapGuestRegion(NeutralGuestMappingRequest request);
    NeutralVirtualizationResult<NeutralGuestMappingCloseReceipt> UnmapGuestRegion(NeutralGuestMappingLease lease);
}

public interface INeutralVirtualEventProvider
{
    NeutralVirtualizationResult<NeutralVirtualEventReceipt> InjectVirtualEvent(NeutralVirtualEventRequest request);
}

public interface INeutralVirtualTrapProvider
{
    NeutralVirtualizationResult<NeutralVirtualTrapEvidence> ObserveVirtualTrap(NeutralChildDomainLease childLease);
}

public interface INeutralVirtualIoProvider
{
    NeutralVirtualizationResult<NeutralVirtualIoLease> BindVirtualIo(NeutralVirtualIoRequest request);
    NeutralVirtualizationResult<NeutralVirtualIoCloseReceipt> CloseVirtualIo(NeutralVirtualIoLease lease);
}
