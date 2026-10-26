using SingPlus.Contracts;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    /// <summary>
    /// Snapshot a bounded aligned subrange of an original mapped OS buffer under its runtime read reservation,
    /// then load the snapshot. This is not a coherent live guest-memory load.
    /// </summary>
    public KernelResult<SoftwareSecureInitializedLoadResult> ExecuteSoftwareSecureRegionSnapshotLoad(ProcessHandle owner,
        VirtualDomainHandle domain, CapabilityId configureCapability, CapabilityId executeCapability,
        GuestRegionMappingHandle mapping, OwnedBuffer<byte> buffer, int byteLength, int byteOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (byteLength is < 8 or > 4096 || byteOffset < 0 || (byteOffset & 7) != 0)
            return KernelResult<SoftwareSecureInitializedLoadResult>.Fail(KernelError.PlatformUnsupported, "One bounded Region snapshot required.");
        RuntimeBufferLease<byte>? lease = null;
        bool adopted = false;
        try
        {
            byte[] snapshot;
            Func<KernelResult> current;
            lock (_platformMemoryUseGate)
            {
                var process = Processes.Resolve(owner);
                if (!process.IsSuccess) return KernelResult<SoftwareSecureInitializedLoadResult>.Fail(process.Error, process.Message!);
                var active = EnsureProcessAcceptsNewEffects(process.Value!);
                if (!active.IsSuccess) return KernelResult<SoftwareSecureInitializedLoadResult>.Fail(active.Error, active.Message!);
                var resolved = _virtualDomains.ResolveAdmission(owner, domain);
                if (!resolved.IsSuccess) return KernelResult<SoftwareSecureInitializedLoadResult>.Fail(resolved.Error, resolved.Message!);
                var local = _virtualDomains.ResolveMapping(resolved.Value!, mapping);
                var caps = _virtualDomains.ResolveMappingSource(resolved.Value!, mapping);
                var principal = new RegionOwner(process.Value!.DomainId, owner.Generation);
                var payload = Regions.CaptureTransferPayload(buffer.Handle, principal);
                if (!local.IsSuccess || !caps.IsSuccess || !payload.IsSuccess || !ReferenceEquals(payload.Value, buffer) ||
                    local.Value.Region != buffer.Handle || local.Value.Access != GuestMemoryAccess.Read ||
                    local.Value.GuestRange.ByteLength < byteLength || byteOffset > local.Value.GuestRange.ByteLength - byteLength ||
                    buffer.Length < byteLength || byteOffset > buffer.Length - byteLength ||
                    local.Value.GuestRange.GuestAddress > ulong.MaxValue - (ulong)byteOffset ||
                    local.Value.GuestRange.GuestAddress + (ulong)byteOffset > ulong.MaxValue - (ulong)byteLength)
                    return KernelResult<SoftwareSecureInitializedLoadResult>.Fail(KernelError.StaleGeneration, "Original read-only mapped Region payload required.");
                var permission = CapabilityAuthority.CommitVirtualDomainEffect(configureCapability, principal.DomainId, owner.Generation,
                    CapabilityRights.Configure, VirtualizationResourceIds.Domain(domain.DomainId), () =>
                    CapabilityAuthority.CommitSecureDomainEffect(executeCapability, principal.DomainId, owner.Generation,
                    CapabilityRights.Execute, SoftwareSecureInitializedLoadResource(domain), () =>
                    CapabilityAuthority.CommitVirtualDomainEffect(caps.Value.Memory, principal.DomainId, owner.Generation,
                    CapabilityRights.Map, VirtualizationResourceIds.Memory(domain.DomainId), () =>
                    CapabilityAuthority.CommitGuestRegionSource(caps.Value.Region, principal.DomainId, owner.Generation,
                    CapabilityRights.Map | CapabilityRights.Read, CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), KernelResult.Ok))));
                if (!permission.IsSuccess) return KernelResult<SoftwareSecureInitializedLoadResult>.Fail(permission.Error, permission.Message!);
                lease = buffer.ReserveForRuntime(RuntimeBufferAccess.Read);
                var originalLease = lease;
                var originalRegion = buffer.Handle;
                current = () =>
                {
                    var original = Regions.CaptureTransferPayload(originalRegion, principal);
                    return original.IsSuccess && ReferenceEquals(original.Value, buffer) && buffer.Handle == originalRegion && originalLease.IsActive
                        ? KernelResult.Ok() : KernelResult.Fail(KernelError.StaleGeneration, "Original Region snapshot backing/reservation changed.");
                };
                snapshot = originalLease.ReadOnlySpan.Slice(byteOffset, byteLength).ToArray();
            }
            return ExecuteSoftwareSecureInitializedLoadCore(owner, domain, configureCapability, executeCapability,
                mapping, snapshot, lease, current, () => adopted = true, byteOffset);
        }
        catch (InvalidOperationException error)
        {
            return KernelResult<SoftwareSecureInitializedLoadResult>.Fail(KernelError.PlatformBindingActive, error.Message);
        }
        finally
        {
            // Once adopted, only the original operation's known closure may release this reservation.
            if (!adopted) lease?.Dispose();
        }
    }
}
