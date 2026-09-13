using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

/// <summary>
/// Joins existing Sing authority to narrow CXL provider bindings. Provider evidence never
/// substitutes for a live RegionUse or DeviceResourceSet-derived device lease.
/// </summary>
public sealed class CxlAuthorityBridge : ICxlTeardownParticipant
{
    private sealed record TrackedFabric(CxlFabricBinding Binding, RegionOwner Owner, ProcessHandle Process,
        RegionBackingLeaseHandle? BackingLease = null);
    private readonly RegionAuthority _regions;
    private readonly PlatformAuthorityBridge _platform;
    private readonly ICxlDiscoveryProvider _discovery;
    private readonly ICxlIoProvider _io;
    private readonly ICxlFabricProvider _fabric;
    private readonly ICxlMemoryProvider _memory;
    private readonly ICxlCoherentAccessProvider _coherent;
    private readonly ICxlSecurityEvidenceProvider _security;
    private readonly Dictionary<CxlFabricBindingId, TrackedFabric> _trackedFabric = [];
    private readonly Dictionary<CxlMemoryBindingId, CxlMemoryBinding> _trackedMemory = [];
    private readonly Dictionary<CxlCoherentBindingId, CxlCoherentBinding> _trackedCoherent = [];
    private readonly HashSet<(ProcessHandle Process, RegionOwner Owner)> _uncontainedEffects = [];

    public CxlAuthorityBridge(
        RuntimeKernel kernel,
        ICxlDiscoveryProvider discovery,
        ICxlIoProvider io,
        ICxlFabricProvider fabric,
        ICxlMemoryProvider memory,
        ICxlCoherentAccessProvider coherent,
        ICxlSecurityEvidenceProvider security)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        _regions = kernel.Regions;
        _platform = kernel.PlatformAuthority;
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _io = io ?? throw new ArgumentNullException(nameof(io));
        _fabric = fabric ?? throw new ArgumentNullException(nameof(fabric));
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _coherent = coherent ?? throw new ArgumentNullException(nameof(coherent));
        _security = security ?? throw new ArgumentNullException(nameof(security));
        kernel.RegisterCxlTeardownParticipant(this);
    }

    public KernelResult<PlatformDeviceIdentity> ResolveIoDevice(CxlEndpointSnapshot expected)
    {
        var endpoint = ValidateEndpoint(expected, CxlEndpointFeatures.Io);
        if (!endpoint.IsSuccess) return KernelResult<PlatformDeviceIdentity>.Fail(endpoint.Error, endpoint.Message!);
        return FromProvider(_io.ResolveDevice(expected.EndpointId, expected.DeviceGeneration));
    }

    public KernelResult ValidateDeviceAuthority(
        PlatformDomainIdentity subject,
        PlatformDeviceLease deviceLease,
        CxlEndpointSnapshot endpoint)
    {
        var lease = _platform.ValidateDeviceLease(deviceLease, subject);
        if (!lease.IsSuccess) return lease;
        var current = ValidateEndpoint(endpoint, CxlEndpointFeatures.Io);
        if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
        var device = FromProvider(_io.ResolveDevice(endpoint.EndpointId, endpoint.DeviceGeneration));
        if (!device.IsSuccess) return KernelResult.Fail(device.Error, device.Message!);
        return device.Value == deviceLease.Device
            ? KernelResult.Ok()
            : KernelResult.Fail(KernelError.InsufficientRights, "The device lease does not authorize this CXL endpoint.");
    }

    public KernelResult<CxlFabricBinding> BindFabric(
        RegionOwner principal,
        RegionUseHandle regionUse,
        PlatformDomainIdentity subject,
        PlatformDeviceLease deviceLease,
        CxlFabricBindingRequest request)
    {
        var authority = ValidateAuthority(principal, regionUse, subject, deviceLease, request.EndpointId, request.DeviceGeneration);
        if (!authority.IsSuccess) return KernelResult<CxlFabricBinding>.Fail(authority.Error, authority.Message!);
        if (request.CapacityBytes <= 0 || !Enum.IsDefined(request.Persistence) || !Enum.IsDefined(request.Sharing))
            return KernelResult<CxlFabricBinding>.Fail(KernelError.PlatformDenied, "CXL fabric request has invalid semantic placement properties.");

        var providerResult = InvokeCreation(() => _fabric.Bind(request), subject.Process, principal, "CXL fabric bind");
        if (!providerResult.IsSuccess) return providerResult;
        var result = KernelResult<CxlFabricBinding>.Ok(providerResult.Value!);
        var binding = result.Value!;
        _trackedFabric[binding.BindingId] = new(binding, principal, subject.Process);
        var currentBinding = FromProvider(_fabric.Query(binding.BindingId));
        if (binding.BindingId.Value == 0 || binding.Generation.Value == 0 ||
            binding.EndpointId != request.EndpointId || binding.DeviceGeneration != request.DeviceGeneration ||
            binding.CapacityBytes < request.CapacityBytes || !currentBinding.IsSuccess || currentBinding.Value != binding)
        {
            var compensation = _fabric.Unbind(binding);
            if (compensation.IsSuccess) _trackedFabric.Remove(binding.BindingId);
            else return KernelResult<CxlFabricBinding>.Fail(KernelError.ExternalEffectUncontained, "Malformed CXL fabric binding could not be closed and remains quarantined.");
            return KernelResult<CxlFabricBinding>.Fail(KernelError.PlatformFaulted, "CXL fabric provider returned a malformed or widened binding.");
        }
        return result;
    }

    public KernelResult<CxlFabricBinding> BindFabricForBacking(
        RegionOwner principal, RegionBackingLeaseHandle backingLease,
        PlatformDomainIdentity subject, PlatformDeviceLease deviceLease,
        CxlFabricBindingRequest request)
    {
        var backing = _regions.ValidateBacking(backingLease, principal);
        if (!backing.IsSuccess) return KernelResult<CxlFabricBinding>.Fail(backing.Error, backing.Message!);
        var device = ValidateDeviceAuthority(subject, deviceLease,
            new(request.EndpointId, request.DeviceGeneration, CxlEndpointFeatures.Io | CxlEndpointFeatures.Memory, true));
        if (!device.IsSuccess) return KernelResult<CxlFabricBinding>.Fail(device.Error, device.Message!);
        if (request.CapacityBytes < backing.Value!.ByteLength || !Enum.IsDefined(request.Persistence) || !Enum.IsDefined(request.Sharing))
            return KernelResult<CxlFabricBinding>.Fail(KernelError.PlatformDenied, "CXL backing request is invalid or too small.");
        var providerResult = InvokeCreation(() => _fabric.Bind(request), subject.Process, principal, "CXL backing fabric bind");
        if (!providerResult.IsSuccess) return providerResult;
        var result = KernelResult<CxlFabricBinding>.Ok(providerResult.Value!);
        var binding = result.Value!;
        _trackedFabric[binding.BindingId] = new(binding, principal, subject.Process, backingLease);
        var currentBinding = FromProvider(_fabric.Query(binding.BindingId));
        if (binding.BindingId.Value == 0 || binding.Generation.Value == 0 || binding.EndpointId != request.EndpointId ||
            binding.DeviceGeneration != request.DeviceGeneration || binding.CapacityBytes < request.CapacityBytes ||
            !currentBinding.IsSuccess || currentBinding.Value != binding)
        {
            var compensation = _fabric.Unbind(binding);
            if (compensation.IsSuccess) _trackedFabric.Remove(binding.BindingId);
            else return KernelResult<CxlFabricBinding>.Fail(KernelError.ExternalEffectUncontained, "Malformed CXL backing fabric binding could not be closed and remains quarantined.");
            return KernelResult<CxlFabricBinding>.Fail(KernelError.PlatformFaulted, "CXL fabric provider returned a malformed backing binding.");
        }
        return result;
    }

    public KernelResult<CxlMemoryBinding> BindMemory(
        RegionOwner principal,
        RegionBackingLeaseHandle backingLease,
        CxlFabricBinding expectedFabric)
    {
        var backing = _regions.ValidateBacking(backingLease, principal);
        if (!backing.IsSuccess) return KernelResult<CxlMemoryBinding>.Fail(backing.Error, backing.Message!);
        var fabric = ValidateFabric(expectedFabric);
        if (!fabric.IsSuccess) return KernelResult<CxlMemoryBinding>.Fail(fabric.Error, fabric.Message!);
        if (!_trackedFabric.TryGetValue(expectedFabric.BindingId, out var trackedFabric) || trackedFabric.Binding != expectedFabric)
            return KernelResult<CxlMemoryBinding>.Fail(KernelError.PlatformBindingNotFound, "CXL fabric binding is not tracked for memory creation.");
        var providerResult = InvokeCreation(() => _memory.BindMemory(expectedFabric, backing.Value!),
            trackedFabric.Process, principal, "CXL memory bind");
        var result = providerResult.IsSuccess
            ? KernelResult<CxlMemoryBinding>.Ok(providerResult.Value!)
            : KernelResult<CxlMemoryBinding>.Fail(providerResult.Error, providerResult.Message!);
        if (result.IsSuccess) _trackedMemory[result.Value!.BindingId] = result.Value;
        var validated = ValidateMemoryResult(result, expectedFabric, backingLease);
        return validated;
    }

    public KernelResult<CxlCoherentBinding> BindCoherentAccess(
        RegionOwner principal,
        RegionUseHandle regionUse,
        CxlFabricBinding expectedFabric)
    {
        var endpoint = ValidateEndpoint(
            new CxlEndpointSnapshot(expectedFabric.EndpointId, expectedFabric.DeviceGeneration, CxlEndpointFeatures.CoherentAccess, true),
            CxlEndpointFeatures.CoherentAccess);
        if (!endpoint.IsSuccess) return KernelResult<CxlCoherentBinding>.Fail(endpoint.Error, endpoint.Message!);
        var use = _regions.ValidateUse(regionUse, principal);
        if (!use.IsSuccess) return KernelResult<CxlCoherentBinding>.Fail(use.Error, use.Message!);
        if (use.Value!.Mode != RegionUseMode.DirectCoherentWrite && use.Value.Mode != RegionUseMode.SharedReadMostly)
            return KernelResult<CxlCoherentBinding>.Fail(KernelError.InsufficientRights, "Coherent access requires an explicitly coherent RegionUse mode.");
        var fabric = ValidateFabric(expectedFabric);
        if (!fabric.IsSuccess) return KernelResult<CxlCoherentBinding>.Fail(fabric.Error, fabric.Message!);
        if (!_trackedFabric.TryGetValue(expectedFabric.BindingId, out var trackedFabric) || trackedFabric.Binding != expectedFabric)
            return KernelResult<CxlCoherentBinding>.Fail(KernelError.PlatformBindingNotFound,
                "CXL fabric binding is not tracked for coherent-access creation.");

        var providerResult = InvokeCreation(() => _coherent.BindCoherentAccess(expectedFabric, use.Value),
            trackedFabric.Process, principal, "CXL coherent-access bind");
        if (!providerResult.IsSuccess) return providerResult;
        var result = KernelResult<CxlCoherentBinding>.Ok(providerResult.Value!);
        var binding = result.Value!;
        _trackedCoherent[binding.BindingId] = binding;
        var current = FromProvider(_coherent.QueryCoherentAccess(binding.BindingId));
        if (binding.BindingId.Value == 0 || binding.Generation.Value == 0 ||
            binding.FabricBinding != expectedFabric || binding.RegionUse != regionUse ||
            !current.IsSuccess || current.Value != binding)
        {
            var compensation = _coherent.ReleaseCoherentAccess(binding);
            if (compensation.IsSuccess) _trackedCoherent.Remove(binding.BindingId);
            else return KernelResult<CxlCoherentBinding>.Fail(KernelError.ExternalEffectUncontained,
                "Malformed CXL coherent binding could not be closed and remains quarantined.");
            return KernelResult<CxlCoherentBinding>.Fail(KernelError.PlatformFaulted, "CXL coherent provider returned a malformed binding.");
        }
        return result;
    }

    public KernelResult RevalidateBeforeEffect(
        RegionOwner principal,
        RegionUseHandle regionUse,
        CxlEndpointSnapshot endpoint,
        CxlFabricBinding fabric,
        CxlCoherentBinding? coherent = null)
    {
        var use = _regions.ValidateUse(regionUse, principal);
        if (!use.IsSuccess) return KernelResult.Fail(use.Error, use.Message!);
        var currentEndpoint = ValidateEndpoint(endpoint, CxlEndpointFeatures.None);
        if (!currentEndpoint.IsSuccess) return KernelResult.Fail(currentEndpoint.Error, currentEndpoint.Message!);
        var currentFabric = ValidateFabric(fabric);
        if (!currentFabric.IsSuccess) return currentFabric;
        if (fabric.EndpointId != endpoint.EndpointId || fabric.DeviceGeneration != endpoint.DeviceGeneration)
            return KernelResult.Fail(KernelError.StaleGeneration, "CXL endpoint and fabric binding generations no longer describe the same binding.");

        if (coherent is not null)
        {
            var current = FromProvider(_coherent.QueryCoherentAccess(coherent.BindingId));
            if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
            if (current.Value != coherent || coherent.RegionUse != regionUse || coherent.FabricBinding != fabric)
                return KernelResult.Fail(KernelError.StaleGeneration, "CXL coherent binding generation changed.");
            if ((currentEndpoint.Value!.Features & CxlEndpointFeatures.CoherentAccess) == 0)
                return KernelResult.Fail(KernelError.StaleGeneration, "CXL coherent capability changed after binding.");
        }
        return KernelResult.Ok();
    }

    public KernelResult RevalidateBacking(RegionOwner principal, RegionBackingLeaseHandle backingLease,
        CxlEndpointSnapshot endpoint, CxlFabricBinding fabric, CxlMemoryBinding memory)
    {
        var backing = _regions.ValidateBacking(backingLease, principal);
        if (!backing.IsSuccess) return KernelResult.Fail(backing.Error, backing.Message!);
        var currentEndpoint = ValidateEndpoint(endpoint, CxlEndpointFeatures.Memory);
        if (!currentEndpoint.IsSuccess) return KernelResult.Fail(currentEndpoint.Error, currentEndpoint.Message!);
        var currentFabric = ValidateFabric(fabric);
        if (!currentFabric.IsSuccess) return currentFabric;
        var current = FromProvider(_memory.QueryMemory(memory.BindingId));
        if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
        return current.Value == memory && memory.BackingLease == backingLease && memory.FabricBinding == fabric
            ? KernelResult.Ok()
            : KernelResult.Fail(KernelError.StaleGeneration, "CXL backing generation changed.");
    }

    internal KernelResult<CxlFabricBinding> QueryFabric(CxlFabricBindingId id) => FromProvider(_fabric.Query(id));
    internal KernelResult<CxlMemoryBinding> QueryMemory(CxlMemoryBindingId id) => FromProvider(_memory.QueryMemory(id));

    public KernelResult<EvidenceRecord> QuerySecurityEvidence(CxlEndpointSnapshot expected)
    {
        var endpoint = ValidateEndpoint(expected, CxlEndpointFeatures.None);
        if (!endpoint.IsSuccess) return KernelResult<EvidenceRecord>.Fail(endpoint.Error, endpoint.Message!);
        return FromProvider(_security.QuerySecurityEvidence(expected.EndpointId, expected.DeviceGeneration));
    }

    public KernelResult ReleaseMemory(CxlMemoryBinding binding)
    {
        var current = FromProvider(_memory.QueryMemory(binding.BindingId));
        if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
        if (current.Value != binding) return KernelResult.Fail(KernelError.StaleGeneration, "CXL memory release requires the exact current binding generation.");
        var released = _memory.ReleaseMemory(binding);
        if (!released.IsSuccess) return KernelResult.Fail(Map(released.Status), released.Message ?? "CXL memory release failed.");
        _trackedMemory.Remove(binding.BindingId);
        return KernelResult.Ok();
    }

    public KernelResult ReleaseFabric(CxlFabricBinding binding)
    {
        var current = FromProvider(_fabric.Query(binding.BindingId));
        if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
        if (current.Value != binding) return KernelResult.Fail(KernelError.StaleGeneration, "CXL fabric release requires the exact current binding generation.");
        var released = _fabric.Unbind(binding);
        if (!released.IsSuccess) return KernelResult.Fail(Map(released.Status), released.Message ?? "CXL fabric release failed.");
        _trackedFabric.Remove(binding.BindingId);
        return KernelResult.Ok();
    }

    public KernelResult ReleaseCoherent(CxlCoherentBinding binding)
    {
        var current = FromProvider(_coherent.QueryCoherentAccess(binding.BindingId));
        if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
        if (current.Value != binding) return KernelResult.Fail(KernelError.StaleGeneration, "CXL coherent release requires the exact current binding generation.");
        var released = _coherent.ReleaseCoherentAccess(binding);
        if (!released.IsSuccess) return KernelResult.Fail(Map(released.Status), released.Message ?? "CXL coherent release failed.");
        _trackedCoherent.Remove(binding.BindingId);
        return KernelResult.Ok();
    }

    KernelResult ICxlTeardownParticipant.CloseForProcess(ProcessHandle process, RegionOwner owner)
    {
        if (_uncontainedEffects.Contains((process, owner)))
            return KernelResult.Fail(KernelError.ExternalEffectUncontained,
                "A CXL creation effect has ambiguous acceptance and reclaim remains quarantined.");
        var fabrics = _trackedFabric.Values.Where(item => item.Process == process && item.Owner == owner).ToArray();
        foreach (var coherent in _trackedCoherent.Values.Where(item => fabrics.Any(f => f.Binding.BindingId == item.FabricBinding.BindingId)).ToArray())
        {
            var closed = ReleaseCoherent(coherent);
            if (!closed.IsSuccess) return closed;
        }
        foreach (var memory in _trackedMemory.Values.Where(item => fabrics.Any(f => f.Binding.BindingId == item.FabricBinding.BindingId)).ToArray())
        {
            var closed = ReleaseMemory(memory);
            if (!closed.IsSuccess) return closed;
        }
        foreach (var fabric in fabrics)
        {
            var closed = ReleaseFabric(fabric.Binding);
            if (!closed.IsSuccess) return closed;
            if (fabric.BackingLease is { } backingLease)
            {
                var releasedBacking = _regions.ReleaseBacking(backingLease, owner);
                if (!releasedBacking.IsSuccess && releasedBacking.Error != KernelError.PlatformBindingNotFound)
                    return releasedBacking;
            }
        }
        return KernelResult.Ok();
    }

    private KernelResult ValidateAuthority(RegionOwner principal, RegionUseHandle regionUse, PlatformDomainIdentity subject,
        PlatformDeviceLease deviceLease, CxlEndpointId endpointId, CxlDeviceGeneration generation)
    {
        var use = _regions.ValidateUse(regionUse, principal);
        if (!use.IsSuccess) return KernelResult.Fail(use.Error, use.Message!);
        var lease = _platform.ValidateDeviceLease(deviceLease, subject);
        if (!lease.IsSuccess) return lease;
        var endpoint = ValidateEndpoint(new(endpointId, generation, CxlEndpointFeatures.Io, true), CxlEndpointFeatures.Io);
        if (!endpoint.IsSuccess) return KernelResult.Fail(endpoint.Error, endpoint.Message!);
        var io = FromProvider(_io.ResolveDevice(endpointId, generation));
        if (!io.IsSuccess) return KernelResult.Fail(io.Error, io.Message!);
        return io.Value == deviceLease.Device
            ? KernelResult.Ok()
            : KernelResult.Fail(KernelError.InsufficientRights, "The device lease does not authorize this CXL endpoint.");
    }

    private KernelResult<CxlEndpointSnapshot> ValidateEndpoint(CxlEndpointSnapshot expected, CxlEndpointFeatures required)
    {
        var current = FromProvider(_discovery.QueryEndpoint(expected.EndpointId));
        if (!current.IsSuccess) return current;
        if (!current.Value!.Available) return KernelResult<CxlEndpointSnapshot>.Fail(KernelError.PlatformUnavailable, "CXL endpoint is unavailable.");
        if (current.Value.DeviceGeneration != expected.DeviceGeneration)
            return KernelResult<CxlEndpointSnapshot>.Fail(KernelError.StaleGeneration, "CXL device generation changed.");
        if ((current.Value.Features & required) != required)
            return KernelResult<CxlEndpointSnapshot>.Fail(KernelError.PlatformUnsupported, "CXL endpoint no longer supports the required semantic feature.");
        return current;
    }

    private KernelResult ValidateFabric(CxlFabricBinding expected)
    {
        var current = FromProvider(_fabric.Query(expected.BindingId));
        if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
        return current.Value == expected
            ? KernelResult.Ok()
            : KernelResult.Fail(KernelError.StaleGeneration, "CXL fabric binding generation changed.");
    }

    private KernelResult<CxlMemoryBinding> ValidateMemoryResult(KernelResult<CxlMemoryBinding> result, CxlFabricBinding fabric, RegionBackingLeaseHandle backingLease)
    {
        if (!result.IsSuccess) return result;
        var binding = result.Value!;
        var current = FromProvider(_memory.QueryMemory(binding.BindingId));
        if (binding.BindingId.Value != 0 && binding.Generation.Value != 0 && binding.FabricBinding == fabric &&
            binding.BackingLease == backingLease && current.IsSuccess && current.Value == binding)
            return result;
        var compensation = _memory.ReleaseMemory(binding);
        if (compensation.IsSuccess)
        {
            _trackedMemory.Remove(binding.BindingId);
            return KernelResult<CxlMemoryBinding>.Fail(KernelError.PlatformFaulted, "CXL memory provider returned a malformed binding and was closed.");
        }
        return KernelResult<CxlMemoryBinding>.Fail(KernelError.ExternalEffectUncontained, "Malformed CXL memory binding could not be closed and remains quarantined.");
    }

    private static KernelResult<T> FromProvider<T>(PlatformAuthorityResult<T> result) => result.IsSuccess
        ? KernelResult<T>.Ok(result.Value!)
        : KernelResult<T>.Fail(Map(result.Status), result.Message ?? "CXL provider rejected the request.");

    private KernelResult<T> InvokeCreation<T>(Func<PlatformAuthorityResult<T>> effect,
        ProcessHandle process, RegionOwner owner, string operation)
    {
        PlatformAuthorityResult<T> result;
        try
        {
            result = effect();
        }
        catch (Exception exception)
        {
            _uncontainedEffects.Add((process, owner));
            return KernelResult<T>.Fail(KernelError.ExternalEffectUncontained,
                $"{operation} threw without closure evidence and remains quarantined: {exception.Message}");
        }
        if (result.IsSuccess) return KernelResult<T>.Ok(result.Value!);
        if (result.Status == PlatformAuthorityStatus.NotAccepted)
            return KernelResult<T>.Fail(Map(result.Status), result.Message ?? $"{operation} was rejected before effect.");
        _uncontainedEffects.Add((process, owner));
        return KernelResult<T>.Fail(KernelError.ExternalEffectUncontained,
            result.Message ?? $"{operation} acceptance is ambiguous and remains quarantined.");
    }

    private static KernelError Map(PlatformAuthorityStatus status) => status switch
    {
        PlatformAuthorityStatus.NotAccepted => KernelError.PlatformUnavailable,
        PlatformAuthorityStatus.Unavailable => KernelError.PlatformUnavailable,
        PlatformAuthorityStatus.Unsupported => KernelError.PlatformUnsupported,
        PlatformAuthorityStatus.Stale => KernelError.StaleGeneration,
        PlatformAuthorityStatus.Revoked => KernelError.PlatformBindingRevoked,
        PlatformAuthorityStatus.WrongDomain => KernelError.WrongPlatformDomain,
        PlatformAuthorityStatus.Denied => KernelError.PlatformDenied,
        _ => KernelError.PlatformFaulted
    };
}
