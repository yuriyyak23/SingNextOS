using SingPlus.Contracts;

namespace SingPlus.Runtime;

/// <summary>Kernel-private compound identity for one virtualized compute submission.</summary>
internal readonly record struct VirtualComputeContext(
    VirtualDomainHandle VirtualDomain,
    GuestRegionMappingHandle Input,
    GuestRegionMappingHandle Output,
    VirtualIoBinding VirtualIo,
    SecureExecutionBinding? SecureExecution);

public sealed partial class RuntimeKernel
{
    private readonly object _virtualComputeGate = new();
    private readonly Dictionary<ExternalOperationId, (ProcessHandle Owner, VirtualComputeContext Context)> _virtualComputePins = [];

    internal void PinVirtualComputeContext(ProcessHandle owner, ExternalOperationHandle operation, VirtualComputeContext context)
    {
        lock (_virtualComputeGate) _virtualComputePins.Add(operation.OperationId, (owner, context));
    }

    internal void ReleaseVirtualComputeContext(ExternalOperationHandle operation)
    {
        lock (_virtualComputeGate) _virtualComputePins.Remove(operation.OperationId);
    }

    private bool HasVirtualComputePin(VirtualIoBinding binding)
    {
        lock (_virtualComputeGate) return _virtualComputePins.Values.Any(x => x.Context.VirtualIo == binding);
    }

    private bool HasVirtualComputePin(SecureExecutionBinding binding)
    {
        lock (_virtualComputeGate) return _virtualComputePins.Values.Any(x => x.Context.SecureExecution == binding);
    }

    private bool HasVirtualComputePin(GuestRegionMappingHandle mapping)
    {
        lock (_virtualComputeGate) return _virtualComputePins.Values.Any(x => x.Context.Input == mapping || x.Context.Output == mapping);
    }

    internal KernelResult<VirtualComputeContext> CreateVirtualComputeContext(ProcessHandle owner,
        VirtualDomainHandle domain, GuestRegionMappingHandle input, GuestRegionMappingHandle output,
        VirtualIoBinding virtualIo, SecureExecutionBinding? secureExecution = null)
    {
        VirtualComputeContext context = new VirtualComputeContext(domain, input, output, virtualIo, secureExecution);
        KernelResult validation = RevalidateVirtualComputeContext(owner, context, null);
        return validation.IsSuccess
            ? KernelResult<VirtualComputeContext>.Ok(context)
            : KernelResult<VirtualComputeContext>.Fail(validation.Error, validation.Message!);
    }

    internal sealed record VirtualIoEffectSource(object OriginalOwner, long Revision);
    internal KernelResult<VirtualIoEffectSource> CaptureVirtualComputeIoSource(ProcessHandle owner, VirtualComputeContext context)
    {
        lock (_virtualIoGate)
            return _virtualIo.TryGetValue(context.VirtualIo.Id, out VirtualIoRecord? record) && HasOriginalVirtualIo(record) &&
                record.Owner == owner && record.Domain == context.VirtualDomain && !record.SourceRevisionExhausted && record.State == VirtualIoState.Active
                ? KernelResult<VirtualIoEffectSource>.Ok(new(record, record.SourceRevision))
                : KernelResult<VirtualIoEffectSource>.Fail(KernelError.PlatformFaulted, "Original Virtual-I/O source unavailable.");
    }

    // Prepare external queries first. Returned delegate is kernel-private pure owner commit only.
    internal KernelResult<VirtualDomainAuthority.EffectSource> CaptureVirtualComputeSource(ProcessHandle owner,
        VirtualDomainHandle domain)
    {
        KernelResult<VirtualDomainAuthority.Record> original = _virtualDomains.ResolveAdmission(owner, domain);
        return original.IsSuccess ? _virtualDomains.CaptureEffectSource(original.Value!, original.Value!.State) :
            KernelResult<VirtualDomainAuthority.EffectSource>.Fail(original.Error, original.Message!);
    }

    internal KernelResult<Func<Func<KernelResult>, KernelResult>> PrepareVirtualComputePublication(
        ProcessHandle owner, VirtualComputeContext context, ComputePlan plan,
        VirtualDomainAuthority.EffectSource? originalSource = null, SecureEffectSource? originalSecureSource = null, VirtualIoEffectSource? originalIoSource = null)
    {
        KernelResult<VirtualDomainAuthority.Record> originalDomain = _virtualDomains.ResolveAdmission(owner, context.VirtualDomain);
        if (!originalDomain.IsSuccess) return KernelResult<Func<Func<KernelResult>, KernelResult>>.Fail(originalDomain.Error, originalDomain.Message!);
        KernelResult<VirtualDomainAuthority.EffectSource> effectSource = originalSource is null ? _virtualDomains.CaptureEffectSource(originalDomain.Value!, originalDomain.Value!.State) :
            KernelResult<VirtualDomainAuthority.EffectSource>.Ok(originalSource);
        if (!effectSource.IsSuccess) return KernelResult<Func<Func<KernelResult>, KernelResult>>.Fail(effectSource.Error, effectSource.Message!);
        SecureEffectSource? secureSource = originalSecureSource;
        if (context.SecureExecution is { } secureBinding && secureSource is null)
        {
            KernelResult<SecureEffectSource> captured = CaptureSecureComputeSource(owner, secureBinding);
            if (!captured.IsSuccess) return KernelResult<Func<Func<KernelResult>, KernelResult>>.Fail(captured.Error, captured.Message!);
            secureSource = captured.Value;
        }
        KernelResult<VirtualIoEffectSource> ioSource = originalIoSource is null ? CaptureVirtualComputeIoSource(owner, context) : KernelResult<VirtualIoEffectSource>.Ok(originalIoSource);
        if (!ioSource.IsSuccess) return KernelResult<Func<Func<KernelResult>, KernelResult>>.Fail(ioSource.Error, ioSource.Message!);
        KernelResult valid = RevalidateVirtualComputeContext(owner, context, plan);
        if (!valid.IsSuccess) return KernelResult<Func<Func<KernelResult>, KernelResult>>.Fail(valid.Error, valid.Message!);
        KernelResult<VirtualDomainAuthority.Record> domain = _virtualDomains.ResolveAdmission(owner, context.VirtualDomain);
        if (!domain.IsSuccess) return KernelResult<Func<Func<KernelResult>, KernelResult>>.Fail(domain.Error, domain.Message!);
        VirtualDomainAuthority.Record record = domain.Value!;
        VirtualDomainState expected = record.State;
        KernelResult<GuestRegionMapping> input = _virtualDomains.ResolveMapping(record, context.Input);
        KernelResult<GuestRegionMapping> output = _virtualDomains.ResolveMapping(record, context.Output);
        KernelResult<(CapabilityId Memory, CapabilityId Region)> inputSource = _virtualDomains.ResolveMappingSource(record, context.Input);
        KernelResult<(CapabilityId Memory, CapabilityId Region)> outputSource = _virtualDomains.ResolveMappingSource(record, context.Output);
        if (!input.IsSuccess || !output.IsSuccess || !inputSource.IsSuccess || !outputSource.IsSuccess)
            return KernelResult<Func<Func<KernelResult>, KernelResult>>.Fail(KernelError.StaleGeneration, "Virtual publication mapping/source tuple changed.");
        VirtualIoRecord io;
        lock (_virtualIoGate)
        {
            if (!_virtualIo.TryGetValue(context.VirtualIo.Id, out io!) || !HasOriginalVirtualIo(io) || io.SourceRevisionExhausted ||
                !ReferenceEquals(ioSource.Value!.OriginalOwner, io) || ioSource.Value.Revision != io.SourceRevision || io.Binding != context.VirtualIo ||
                io.Owner != owner || io.Domain != context.VirtualDomain || io.State != VirtualIoState.Active)
                return KernelResult<Func<Func<KernelResult>, KernelResult>>.Fail(KernelError.StaleGeneration, "Virtual publication I/O tuple changed.");
        }
        KernelResult<CapabilityId> deviceSource = PlatformAuthority.DeviceEffectSource(io.Platform.ParentDevice, io.Platform.ParentDevice.DomainBinding.Subject);
        if (!deviceSource.IsSuccess) return KernelResult<Func<Func<KernelResult>, KernelResult>>.Fail(deviceSource.Error, deviceSource.Message!);
        DomainId subject = io.Platform.ParentDevice.DomainBinding.Subject.DomainId;
        KernelResult<Func<Func<KernelResult>, KernelResult>> backings = PrepareSecureGuestBackings(new(subject, owner.Generation),
            [(plan.Intent.Input.Region, plan.Intent.Input.Range), (plan.Intent.Output.Region, plan.Intent.Output.Range)]);
        if (!backings.IsSuccess) return KernelResult<Func<Func<KernelResult>, KernelResult>>.Fail(backings.Error, backings.Message!);
        CapabilityRights RegionRights(GuestRegionMapping mapping) => CapabilityRights.Map | CapabilityRights.Read |
            ((mapping.Access & GuestMemoryAccess.Write) != 0 ? CapabilityRights.Write : CapabilityRights.None) |
            ((mapping.Access & GuestMemoryAccess.Execute) != 0 ? CapabilityRights.Execute : CapabilityRights.None);
        KernelResult Commit(Func<KernelResult> commit) => CommitCxlBackingPublication(owner, new(subject, owner.Generation), () =>
            CapabilityAuthority.CommitDeviceLeaseAdmission(deviceSource.Value, subject, owner.Generation,
                ToDeviceCapabilityRights(io.Rights), io.Platform.ParentDevice.Device.ResourceId, () =>
            CapabilityAuthority.CommitVirtualDomainEffect(inputSource.Value.Memory, subject, owner.Generation, CapabilityRights.Map,
                VirtualizationResourceIds.Memory(context.VirtualDomain.DomainId), () =>
            CapabilityAuthority.CommitVirtualDomainEffect(outputSource.Value.Memory, subject, owner.Generation, CapabilityRights.Map,
                VirtualizationResourceIds.Memory(context.VirtualDomain.DomainId), () =>
            CapabilityAuthority.CommitGuestRegionSource(inputSource.Value.Region, subject, owner.Generation, RegionRights(input.Value!),
                CapabilityResourceIds.MemoryRegion(input.Value!.Region.RegionId), () =>
            CapabilityAuthority.CommitGuestRegionSource(outputSource.Value.Region, subject, owner.Generation, RegionRights(output.Value!),
                CapabilityResourceIds.MemoryRegion(output.Value!.Region.RegionId), () =>
            _virtualDomains.CommitEffectAdmission(record, expected, () =>
            {
                KernelResult<GuestRegionMapping> currentInput = _virtualDomains.ResolveMapping(record, context.Input);
                KernelResult<GuestRegionMapping> currentOutput = _virtualDomains.ResolveMapping(record, context.Output);
                KernelResult<(CapabilityId Memory, CapabilityId Region)> currentInputSource = _virtualDomains.ResolveMappingSource(record, context.Input);
                KernelResult<(CapabilityId Memory, CapabilityId Region)> currentOutputSource = _virtualDomains.ResolveMappingSource(record, context.Output);
                if (!currentInput.IsSuccess || !currentOutput.IsSuccess || currentInput.Value != input.Value || currentOutput.Value != output.Value ||
                    !currentInputSource.IsSuccess || !currentOutputSource.IsSuccess ||
                    currentInputSource.Value != inputSource.Value || currentOutputSource.Value != outputSource.Value ||
                    currentInput.Value!.Region != plan.Intent.Input.Region || currentOutput.Value!.Region != plan.Intent.Output.Region)
                    return KernelResult.Fail(KernelError.StaleGeneration, "Virtual publication exact operand/source tuple changed.");
                KernelResult CommitIo() => CommitVirtualComputeIo(owner, context, commit, ioSource.Value);
                return secureSource is null ? backings.Value!(CommitIo) : CommitSecureEffectSource(secureSource, () => backings.Value!(CommitIo));
            }, effectSource.Value!)))))));
        return KernelResult<Func<Func<KernelResult>, KernelResult>>.Ok(Commit);
    }

    // Called after domain and bridge admission locks, immediately around the pure owner transition.
    internal KernelResult CommitVirtualComputeIo(ProcessHandle owner, VirtualComputeContext context, Func<KernelResult> commit, VirtualIoEffectSource? originalSource = null)
    {
        lock (_virtualIoGate)
            return _virtualIo.TryGetValue(context.VirtualIo.Id, out VirtualIoRecord? io) && HasOriginalVirtualIo(io) && !io.SourceRevisionExhausted &&
                (originalSource is null || ReferenceEquals(originalSource.OriginalOwner, io) && originalSource.Revision == io.SourceRevision) && io.Binding == context.VirtualIo &&
                io.Owner == owner && io.Domain == context.VirtualDomain && io.State == VirtualIoState.Active
                ? commit() : KernelResult.Fail(KernelError.PlatformBindingDraining, "Virtual publication I/O stopped.");
    }

    internal KernelResult RevalidateVirtualComputeContext(ProcessHandle owner, VirtualComputeContext context, ComputePlan? plan)
    {
        KernelResult<VirtualDomainAuthority.Record> domain = _virtualDomains.Resolve(owner, context.VirtualDomain);
        if (!domain.IsSuccess) return KernelResult.Fail(domain.Error, domain.Message!);
        KernelResult<GuestRegionMapping> input = _virtualDomains.ResolveMapping(domain.Value!, context.Input);
        KernelResult<GuestRegionMapping> output = _virtualDomains.ResolveMapping(domain.Value!, context.Output);
        if (!input.IsSuccess) return KernelResult.Fail(input.Error, input.Message!);
        if (!output.IsSuccess) return KernelResult.Fail(output.Error, output.Message!);
        KernelResult io = RevalidateVirtualIo(owner, context.VirtualIo, context.VirtualDomain);
        if (!io.IsSuccess) return io;
        if (plan is not null && (plan.Intent.Input.Region != input.Value!.Region || plan.Intent.Output.Region != output.Value!.Region))
            return KernelResult.Fail(KernelError.WrongRegionOwner, "Compute plan operands do not match the exact guest mappings.");
        if (context.SecureExecution is { } secure)
        {
            KernelResult secureValidation = RevalidateSecureExecution(secure);
            if (!secureValidation.IsSuccess) return secureValidation;
            KernelResult<SecureExecutionRecord> execution = ResolveSecureGuestExecution(owner, secure);
            if (!execution.IsSuccess || execution.Value!.Request.Context.Virtual != context.VirtualDomain)
                return KernelResult.Fail(KernelError.WrongPlatformDomain, "Secure compute context belongs to another virtual domain.");
        }
        else if (plan?.Intent.RequiresSecureEvidence == true)
            return KernelResult.Fail(KernelError.PlatformDenied, "Secure compute plan requires an exact secure execution binding.");
        return KernelResult.Ok();
    }
}
