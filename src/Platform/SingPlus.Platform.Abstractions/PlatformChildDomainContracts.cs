namespace SingPlus.Platform;

[Flags]
public enum PlatformChildAuthorityClass
{
    None = 0,
    Lifecycle = 1 << 0,
    Execution = 1 << 1,
    GuestMemory = 1 << 2,
    Events = 1 << 3,
    Traps = 1 << 4,
    Io = 1 << 5,
}

public readonly record struct PlatformChildDomainAuthoritySubset(
    PlatformChildAuthorityClass ParentAuthority,
    PlatformChildAuthorityClass ChildAuthority);

public readonly record struct PlatformChildDomainIntent(
    PlatformVirtualDomainProfile Profile,
    PlatformChildDomainAuthoritySubset Authority);

public readonly record struct PlatformProviderChildDomainLeaseId(ulong Value);

public readonly record struct PlatformProviderChildDomainLease(
    PlatformProviderChildDomainLeaseId LeaseId,
    PlatformProviderLeaseGeneration Generation,
    PlatformProviderDomainLease ParentDomainLease,
    PlatformChildDomainIntent Intent);

public enum PlatformChildDomainTransition
{
    Start = 0,
    Park,
    Resume,
    BeginDrain,
}

public enum PlatformChildDomainState
{
    Created = 0,
    Running,
    Parked,
    Draining,
    Closed,
    Revoked,
    Faulted,
}

public enum PlatformChildDomainClosureDisposition
{
    Closed = 0,
}

/// <summary>
/// Exact closure evidence for a child-domain provider lease. A receipt is evidence,
/// never authority, and callers must validate it against the exact lease they own.
/// </summary>
public readonly record struct PlatformChildDomainClosureReceipt(
    PlatformProviderChildDomainLeaseId LeaseId,
    PlatformProviderLeaseGeneration Generation,
    PlatformProviderDomainLeaseId ParentLeaseId,
    PlatformProviderLeaseGeneration ParentGeneration,
    PlatformChildDomainClosureDisposition Disposition);

public static class PlatformChildDomainContract
{
    public const uint ContractVersion = 2;

    private const PlatformChildAuthorityClass KnownAuthority =
        PlatformChildAuthorityClass.Lifecycle |
        PlatformChildAuthorityClass.Execution |
        PlatformChildAuthorityClass.GuestMemory |
        PlatformChildAuthorityClass.Events |
        PlatformChildAuthorityClass.Traps |
        PlatformChildAuthorityClass.Io;

    public static PlatformAuthorityResult ValidateCreateRequest(
        PlatformProviderDomainLease parentLease,
        PlatformChildDomainIntent intent)
    {
        var parent = PlatformDomainContract.ValidateLease(parentLease.Subject, parentLease);
        if (!parent.IsSuccess) return parent;

        if (intent.Profile.VirtualProcessorCount <= 0 ||
            intent.Profile.MaximumGuestMemoryBytes <= 0)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "Child-domain profiles require positive virtual-processor and guest-memory bounds.");
        }

        var parentAuthority = intent.Authority.ParentAuthority;
        var childAuthority = intent.Authority.ChildAuthority;
        if (parentAuthority == PlatformChildAuthorityClass.None ||
            childAuthority == PlatformChildAuthorityClass.None ||
            (parentAuthority & ~KnownAuthority) != 0 ||
            (childAuthority & ~KnownAuthority) != 0)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "Child-domain authority must use a non-empty subset of known semantic authority classes.");
        }

        if ((parentAuthority & PlatformChildAuthorityClass.Lifecycle) == 0 ||
            (childAuthority & PlatformChildAuthorityClass.Lifecycle) == 0)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "Parent and child authority must include lifecycle control so closure remains explicit.");
        }

        if ((childAuthority & parentAuthority) != childAuthority)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "Child-domain authority cannot exceed the semantic authority admitted for its parent.");
        }

        return PlatformAuthorityResult.Ok();
    }

    public static PlatformAuthorityResult ValidateLease(
        PlatformProviderDomainLease expectedParent,
        PlatformChildDomainIntent expectedIntent,
        PlatformProviderChildDomainLease lease)
    {
        var request = ValidateCreateRequest(expectedParent, expectedIntent);
        if (!request.IsSuccess) return request;

        if (lease.LeaseId.Value == 0 || lease.Generation.Value == 0)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "Provider child-domain lease identity must be materialized.");
        }

        if (lease.ParentDomainLease != expectedParent)
        {
            return PlatformAuthorityResult.Fail(
                lease.ParentDomainLease.Generation != expectedParent.Generation
                    ? PlatformAuthorityStatus.Stale
                    : PlatformAuthorityStatus.WrongDomain,
                "Provider child-domain lease belongs to a different parent domain lease.");
        }

        if (lease.Intent != expectedIntent)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "Provider child-domain lease does not match the exact admitted child intent.");
        }

        return PlatformAuthorityResult.Ok();
    }

    public static PlatformAuthorityResult ValidateTransition(
        PlatformProviderChildDomainLease lease,
        PlatformChildDomainTransition transition)
    {
        var leaseValidation = ValidateLease(lease.ParentDomainLease, lease.Intent, lease);
        if (!leaseValidation.IsSuccess) return leaseValidation;

        if (!Enum.IsDefined(transition))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "Child-domain transition is not defined by the semantic contract.");
        }

        return PlatformAuthorityResult.Ok();
    }

    public static PlatformAuthorityResult ValidateEffectState(PlatformChildDomainState state)
    {
        if (!Enum.IsDefined(state))
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Child-domain state is unknown.");

        return state switch
        {
            PlatformChildDomainState.Created or
            PlatformChildDomainState.Running or
            PlatformChildDomainState.Parked => PlatformAuthorityResult.Ok(),
            PlatformChildDomainState.Draining or
            PlatformChildDomainState.Closed => PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "Child-domain effects are rejected after drain begins."),
            PlatformChildDomainState.Revoked => PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Revoked,
                "Child-domain authority is revoked."),
            PlatformChildDomainState.Faulted => PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "Child domain is faulted."),
            _ => PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Child-domain state is unknown."),
        };
    }

    public static PlatformAuthorityResult ValidateClosureReceipt(
        PlatformProviderChildDomainLease expectedLease,
        PlatformChildDomainClosureReceipt receipt)
    {
        var leaseValidation = ValidateLease(
            expectedLease.ParentDomainLease,
            expectedLease.Intent,
            expectedLease);
        if (!leaseValidation.IsSuccess) return leaseValidation;

        if (receipt.LeaseId != expectedLease.LeaseId ||
            receipt.Generation != expectedLease.Generation)
        {
            return PlatformAuthorityResult.Fail(
                receipt.LeaseId == expectedLease.LeaseId
                    ? PlatformAuthorityStatus.Stale
                    : PlatformAuthorityStatus.Faulted,
                "Child-domain closure receipt does not identify the exact provider lease generation.");
        }

        if (receipt.ParentLeaseId != expectedLease.ParentDomainLease.LeaseId)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.WrongDomain,
                "Child-domain closure receipt belongs to a different parent domain lease.");
        }

        if (receipt.ParentGeneration != expectedLease.ParentDomainLease.Generation)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Stale,
                "Child-domain closure receipt belongs to a stale parent generation.");
        }

        if (!Enum.IsDefined(receipt.Disposition) ||
            receipt.Disposition != PlatformChildDomainClosureDisposition.Closed)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "Child-domain closure receipt does not prove definitive closure.");
        }

        return PlatformAuthorityResult.Ok();
    }
}

[Flags]
public enum PlatformGuestMemoryAccess
{
    None = 0,
    Read = 1 << 0,
    Write = 1 << 1,
    Execute = 1 << 2,
}

public readonly record struct PlatformGuestAddressRange(
    ulong GuestAddress,
    long ByteLength);

public readonly record struct PlatformGuestRegionMappingRequest(
    PlatformProviderChildDomainLease ChildLease,
    PlatformProviderOwnedRegionMapping ParentMapping,
    PlatformGuestAddressRange GuestRange,
    PlatformGuestMemoryAccess Access);

public readonly record struct PlatformProviderGuestRegionMappingLeaseId(ulong Value);

public readonly record struct PlatformProviderGuestRegionMappingLease(
    PlatformProviderGuestRegionMappingLeaseId LeaseId,
    PlatformProviderLeaseGeneration Generation,
    PlatformProviderChildDomainLease ChildLease,
    PlatformProviderRegionMappingLease ParentMappingLease,
    PlatformGuestAddressRange GuestRange,
    PlatformGuestMemoryAccess Access);

/// <summary>Exact terminal unmap evidence. It is not memory authority or a reuse grant.</summary>
public readonly record struct PlatformGuestRegionMappingClosureReceipt(
    PlatformProviderGuestRegionMappingLeaseId LeaseId,
    PlatformProviderLeaseGeneration Generation,
    PlatformProviderChildDomainLeaseId ChildLeaseId,
    PlatformProviderLeaseGeneration ChildGeneration,
    PlatformProviderDomainLeaseId ParentLeaseId,
    PlatformProviderLeaseGeneration ParentGeneration,
    bool IsTerminal);

public static class PlatformGuestMemoryContract
{
    public const uint ContractVersion = 1;

    public static PlatformAuthorityResult ValidateRequest(
        PlatformGuestRegionMappingRequest request,
        PlatformChildDomainState state = PlatformChildDomainState.Created)
    {
        var child = PlatformChildDomainContract.ValidateLease(
            request.ChildLease.ParentDomainLease,
            request.ChildLease.Intent,
            request.ChildLease);
        if (!child.IsSuccess) return child;

        var effect = PlatformChildDomainContract.ValidateEffectState(state);
        if (!effect.IsSuccess) return effect;

        if ((request.ChildLease.Intent.Authority.ChildAuthority & PlatformChildAuthorityClass.GuestMemory) == 0)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "The admitted child authority does not include guest-memory mapping.");
        }

        var parentMapping = PlatformOwnedRegionMappingContract.ValidateResult(
            request.ChildLease.ParentDomainLease,
            request.ParentMapping.Slice,
            request.ParentMapping);
        if (!parentMapping.IsSuccess) return parentMapping;

        if (request.ParentMapping.Slice.Region.Owner.DomainId != request.ChildLease.ParentDomainLease.Subject.DomainId ||
            request.ParentMapping.Slice.Region.Owner.ProcessGeneration != request.ChildLease.ParentDomainLease.Subject.ProcessGeneration)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.WrongDomain,
                "The exact parent mapping is owned by a different parent subject.");
        }

        if (request.GuestRange.ByteLength <= 0 ||
            request.GuestRange.GuestAddress > ulong.MaxValue - (ulong)request.GuestRange.ByteLength ||
            request.GuestRange.GuestAddress + (ulong)request.GuestRange.ByteLength >
                (ulong)request.ChildLease.Intent.Profile.MaximumGuestMemoryBytes ||
            request.GuestRange.ByteLength > request.ParentMapping.Slice.Length)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "Guest mapping range must fit both the admitted child address-space bound and the exact parent mapping slice.");
        }

        const PlatformGuestMemoryAccess knownAccess =
            PlatformGuestMemoryAccess.Read |
            PlatformGuestMemoryAccess.Write |
            PlatformGuestMemoryAccess.Execute;
        if (request.Access == PlatformGuestMemoryAccess.None ||
            (request.Access & ~knownAccess) != 0)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "Guest mapping access must be a non-empty semantic Read/Write/Execute subset.");
        }

        if ((request.Access & PlatformGuestMemoryAccess.Read) != 0 &&
            (request.ParentMapping.Slice.Access & PlatformMemoryAccess.Read) == 0)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "Guest read access cannot exceed the exact parent mapping access.");
        }

        if ((request.Access & PlatformGuestMemoryAccess.Write) != 0 &&
            (request.ParentMapping.Slice.Access & PlatformMemoryAccess.Write) == 0)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "Guest write access cannot exceed the exact parent mapping access.");
        }

        if ((request.Access & PlatformGuestMemoryAccess.Execute) != 0 &&
            (request.ChildLease.Intent.Authority.ChildAuthority & PlatformChildAuthorityClass.Execution) == 0)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "Guest execute access requires admitted child execution authority.");
        }

        return PlatformAuthorityResult.Ok();
    }

    public static PlatformAuthorityResult ValidateLease(
        PlatformGuestRegionMappingRequest request,
        PlatformProviderGuestRegionMappingLease lease)
    {
        var requestValidation = ValidateRequest(request);
        if (!requestValidation.IsSuccess) return requestValidation;

        if (lease.LeaseId.Value == 0 || lease.Generation.Value == 0)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "Provider guest-mapping lease identity must be materialized.");
        }

        if (lease.ChildLease != request.ChildLease ||
            lease.ParentMappingLease != request.ParentMapping.Lease ||
            lease.GuestRange != request.GuestRange ||
            lease.Access != request.Access)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "Provider guest-mapping lease does not match the exact child, parent mapping, range and access request.");
        }

        return PlatformAuthorityResult.Ok();
    }

    public static PlatformAuthorityResult ValidateClosureReceipt(
        PlatformProviderGuestRegionMappingLease expectedLease,
        PlatformGuestRegionMappingClosureReceipt receipt)
    {
        if (receipt.LeaseId != expectedLease.LeaseId)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Guest-unmap receipt identifies another mapping.");
        if (receipt.Generation != expectedLease.Generation || receipt.ChildGeneration != expectedLease.ChildLease.Generation)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Stale, "Guest-unmap receipt has a stale mapping generation.");
        if (receipt.ChildLeaseId != expectedLease.ChildLease.LeaseId)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.WrongDomain, "Guest-unmap receipt identifies another child.");
        if (receipt.ParentLeaseId != expectedLease.ChildLease.ParentDomainLease.LeaseId)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.WrongDomain, "Guest-unmap receipt identifies another parent.");
        if (receipt.ParentGeneration != expectedLease.ChildLease.ParentDomainLease.Generation)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Stale, "Guest-unmap receipt has a stale parent generation.");
        if (!receipt.IsTerminal)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Guest-unmap receipt is not exact terminal closure evidence.");
        return PlatformAuthorityResult.Ok();
    }
}

public enum PlatformVirtualEventClass
{
    ExternalSignal = 0,
    Timer,
    Preemption,
}

public readonly record struct PlatformVirtualEventRequest(
    PlatformProviderChildDomainLease ChildLease,
    PlatformVirtualEventClass EventClass,
    string SourceResourceId);

/// <summary>Delivery evidence only; it is not completion authority.</summary>
public readonly record struct PlatformVirtualEventReceipt(
    PlatformProviderChildDomainLeaseId ChildLeaseId,
    PlatformProviderLeaseGeneration ChildGeneration,
    PlatformProviderDomainLeaseId ParentLeaseId,
    PlatformProviderLeaseGeneration ParentGeneration,
    ulong Sequence,
    PlatformVirtualEventClass EventClass,
    string SourceResourceId);

public static class PlatformVirtualEventContract
{
    public const uint ContractVersion = 1;

    public static PlatformAuthorityResult ValidateRequest(
        PlatformVirtualEventRequest request,
        PlatformChildDomainState state = PlatformChildDomainState.Created)
    {
        var child = PlatformChildDomainContract.ValidateLease(
            request.ChildLease.ParentDomainLease,
            request.ChildLease.Intent,
            request.ChildLease);
        if (!child.IsSuccess) return child;

        var effect = PlatformChildDomainContract.ValidateEffectState(state);
        if (!effect.IsSuccess) return effect;

        if ((request.ChildLease.Intent.Authority.ChildAuthority & PlatformChildAuthorityClass.Events) == 0)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "The admitted child authority does not include virtual-event injection.");
        }

        if (!Enum.IsDefined(request.EventClass) ||
            string.IsNullOrWhiteSpace(request.SourceResourceId) ||
            request.SourceResourceId.Length > 256)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "Virtual events require a defined semantic class and bounded semantic source identity.");
        }

        return PlatformAuthorityResult.Ok();
    }

    public static PlatformAuthorityResult ValidateReceipt(
        PlatformVirtualEventRequest request,
        PlatformVirtualEventReceipt receipt)
    {
        if (receipt.ChildLeaseId != request.ChildLease.LeaseId)
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.WrongDomain,
                "Virtual-event receipt belongs to another child.");
        if (receipt.ChildGeneration != request.ChildLease.Generation)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Stale, "Virtual-event receipt belongs to another child generation.");
        if (receipt.ParentLeaseId != request.ChildLease.ParentDomainLease.LeaseId)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.WrongDomain, "Virtual-event receipt belongs to another parent.");
        if (receipt.ParentGeneration != request.ChildLease.ParentDomainLease.Generation)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Stale, "Virtual-event receipt belongs to another parent generation.");
        if (receipt.Sequence == 0 || receipt.EventClass != request.EventClass || receipt.SourceResourceId != request.SourceResourceId)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Virtual-event receipt is not exact delivery evidence.");
        return PlatformAuthorityResult.Ok();
    }
}

public readonly record struct PlatformVirtualIoProfile(
    PlatformDeviceRights Rights,
    long MaximumDmaTransferBytes);

public readonly record struct PlatformVirtualIoRequest(
    PlatformProviderChildDomainLease ChildLease,
    PlatformProviderDeviceLease ParentDeviceLease,
    PlatformVirtualIoProfile Profile);

public readonly record struct PlatformProviderVirtualIoLeaseId(ulong Value);

public readonly record struct PlatformProviderVirtualIoLease(
    PlatformProviderVirtualIoLeaseId LeaseId,
    PlatformProviderLeaseGeneration Generation,
    PlatformProviderChildDomainLease ChildLease,
    PlatformProviderDeviceLease ParentDeviceLease,
    PlatformVirtualIoProfile Profile);

/// <summary>Exact terminal virtual-I/O closure evidence; never device authority.</summary>
public readonly record struct PlatformVirtualIoClosureReceipt(
    PlatformProviderVirtualIoLeaseId LeaseId,
    PlatformProviderLeaseGeneration Generation,
    PlatformProviderChildDomainLeaseId ChildLeaseId,
    PlatformProviderLeaseGeneration ChildGeneration,
    PlatformProviderDomainLeaseId ParentLeaseId,
    PlatformProviderLeaseGeneration ParentGeneration,
    bool IsTerminal);

public static class PlatformVirtualIoContract
{
    public const uint ContractVersion = 1;

    public static PlatformAuthorityResult ValidateRequest(
        PlatformVirtualIoRequest request,
        PlatformChildDomainState state = PlatformChildDomainState.Created)
    {
        var child = PlatformChildDomainContract.ValidateLease(
            request.ChildLease.ParentDomainLease,
            request.ChildLease.Intent,
            request.ChildLease);
        if (!child.IsSuccess) return child;

        var effect = PlatformChildDomainContract.ValidateEffectState(state);
        if (!effect.IsSuccess) return effect;

        if ((request.ChildLease.Intent.Authority.ChildAuthority & PlatformChildAuthorityClass.Io) == 0)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "The admitted child authority does not include virtual I/O composition.");
        }

        var parentDevice = PlatformDeviceLeaseContract.ValidateLease(
            request.ChildLease.ParentDomainLease,
            request.ParentDeviceLease.Device,
            request.ParentDeviceLease.Rights,
            request.ParentDeviceLease);
        if (!parentDevice.IsSuccess) return parentDevice;

        if (request.Profile.Rights == PlatformDeviceRights.None ||
            (request.Profile.Rights & ~request.ParentDeviceLease.Rights) != 0 ||
            request.Profile.MaximumDmaTransferBytes <= 0)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "Child virtual-I/O rights and DMA bounds must be non-empty and may not exceed the exact parent device authority.");
        }

        return PlatformAuthorityResult.Ok();
    }

    public static PlatformAuthorityResult ValidateLease(
        PlatformVirtualIoRequest request,
        PlatformProviderVirtualIoLease lease)
    {
        var requestValidation = ValidateRequest(request);
        if (!requestValidation.IsSuccess) return requestValidation;

        if (lease.LeaseId.Value == 0 || lease.Generation.Value == 0)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "Provider virtual-I/O lease identity must be materialized.");
        }

        if (lease.ChildLease != request.ChildLease ||
            lease.ParentDeviceLease != request.ParentDeviceLease ||
            lease.Profile != request.Profile)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "Provider virtual-I/O lease does not match the exact child and parent device request.");
        }

        return PlatformAuthorityResult.Ok();
    }

    public static PlatformAuthorityResult ValidateClosureReceipt(
        PlatformProviderVirtualIoLease expectedLease,
        PlatformVirtualIoClosureReceipt receipt)
    {
        if (receipt.LeaseId != expectedLease.LeaseId)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Virtual-I/O close receipt identifies another binding.");
        if (receipt.Generation != expectedLease.Generation || receipt.ChildGeneration != expectedLease.ChildLease.Generation)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Stale, "Virtual-I/O close receipt has a stale binding generation.");
        if (receipt.ChildLeaseId != expectedLease.ChildLease.LeaseId)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.WrongDomain, "Virtual-I/O close receipt identifies another child.");
        if (receipt.ParentLeaseId != expectedLease.ChildLease.ParentDomainLease.LeaseId)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.WrongDomain, "Virtual-I/O close receipt identifies another parent.");
        if (receipt.ParentGeneration != expectedLease.ChildLease.ParentDomainLease.Generation)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Stale, "Virtual-I/O close receipt has a stale parent generation.");
        if (!receipt.IsTerminal)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Virtual-I/O close receipt is not exact terminal closure evidence.");
        return PlatformAuthorityResult.Ok();
    }
}

/// <summary>
/// Executable child-domain contour. This is deliberately distinct from the local
/// ModelOnly virtualization provider contract: ordinary domain lifetimes cannot
/// satisfy parent-bound child-domain authority.
/// </summary>
public interface IPlatformChildDomainProvider
{
    PlatformAuthorityResult<PlatformProviderChildDomainLease> CreateChildDomain(
        PlatformProviderDomainLease parentLease,
        PlatformChildDomainIntent intent);

    PlatformAuthorityResult TransitionChildDomain(
        PlatformProviderChildDomainLease lease,
        PlatformChildDomainTransition transition);

    PlatformAuthorityResult<PlatformChildDomainClosureReceipt> CloseChildDomain(
        PlatformProviderChildDomainLease lease);
}

public interface IPlatformGuestMemoryProvider
{
    PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease> MapGuestRegion(
        PlatformGuestRegionMappingRequest request);

    PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt> UnmapGuestRegion(
        PlatformProviderGuestRegionMappingLease lease);
}

public interface IPlatformVirtualEventProvider
{
    PlatformAuthorityResult<PlatformVirtualEventReceipt> InjectVirtualEvent(PlatformVirtualEventRequest request);
}

public interface IPlatformVirtualIoProvider
{
    PlatformAuthorityResult<PlatformProviderVirtualIoLease> BindVirtualIo(
        PlatformVirtualIoRequest request);

    PlatformAuthorityResult<PlatformVirtualIoClosureReceipt> RevokeVirtualIo(
        PlatformProviderVirtualIoLease lease);
}
