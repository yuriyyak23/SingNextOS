using YAKSys_Hybrid_CPU.Core;
namespace SingPlus.Platform.HybridCpu;

public sealed partial class HybridCpuPlatformAuthorityProvider
{
    public PlatformAuthorityResult<PlatformOriginalIrqBindResolution> ResolveIrqBind(PlatformOriginalIrqBindRequest request)
    {
        DeviceLeaseRecord device;
        IrqBindFailure? fact;
        IrqBindingRecord? binding;
        lock (_childCreateCommitGate)
        {
            if (!_originalDeviceLeases.TryGetValue(request.Device.LeaseId,out device!) ||
                !_deviceLeases.TryGetValue(request.Device.LeaseId,out var routed) || !ReferenceEquals(routed,device) || device.IrqBindInFlight || device.Lease != request.Device)
                return Refuse("Original IRQ device owner missing/foreign/in flight.");
            binding = _originalIrqBindings.Values.SingleOrDefault(old => ReferenceEquals(old.OriginalBindRequest,request));
            fact = device.FailedIrqBind;
            if (binding is null && (fact is null || !ReferenceEquals(fact.OriginalRequest,request))) return Refuse("Original IRQ invocation unavailable.");
        }
        var producing = _runtime as INeutralOriginalIrqProvider;
        var nativeRequest = binding?.NativeBind ?? fact?.NativeRequest;
        if (producing is null || nativeRequest is not { } expected) return Refuse("Native original IRQ lookup unavailable.");
        var raw = fact?.NativeObservation ?? producing.ResolveIrqBind(expected);
        lock (_childCreateCommitGate) { if (fact is not null) fact.NativeObservation = raw; }
        if (!ValidateOriginalNativeMappingSource().IsSuccess) return Refuse("Original native IRQ realm unavailable.");
        lock (_childCreateCommitGate)
        {
            if (!_deviceLeases.TryGetValue(request.Device.LeaseId,out var routed) || !ReferenceEquals(routed,device) || device.IrqBindInFlight ||
                !_domains.TryGetValue(request.Device.DomainLease.LeaseId,out var parent) || !ReferenceEquals(parent,device.BindParent) ||
                raw is not { } value || value.Request != expected || value.Observation is null || !Enum.IsDefined(value.Observation.Decision) || !value.SourceFenceMatched && !value.NoEffectGuaranteed)
                return Refuse("Original IRQ lookup/raw source lost or malformed.");
            if (value.NoEffectGuaranteed && !value.Observation.IsBound && value.Observation.Decision!=NeutralInterruptBindDecision.Bound && value.Observation.Lease == default && binding is null)
            {
                if (!ReferenceEquals(device.FailedIrqBind,fact)) return Refuse("Original IRQ no-effect owner changed.");
                device.FailedIrqBind = null;
                return PlatformAuthorityResult<PlatformOriginalIrqBindResolution>.Ok(new(request,true,null));
            }
            if (value.NoEffectGuaranteed || value.Observation is not { IsBound: true,Decision: NeutralInterruptBindDecision.Bound } ||
                value.Observation.Lease.Handle.Value == 0 || value.Observation.Lease.Epoch.Value == 0 || value.Observation.Lease.DeviceLease != device.HybridCpuLease ||
                value.Observation.Lease.Source != expected.Source) return Refuse("Original IRQ admitted receipt malformed.");
            if (binding is null)
            {
                if (fact is null || !ReferenceEquals(device.FailedIrqBind,fact) || !ReferenceEquals(fact.Parent,parent)) return Refuse("Original IRQ intent changed.");
                var lease = new PlatformProviderIrqBinding(fact.InvocationId,new(1),request.Device,request.Source);
                binding = new(lease,value.Observation.Lease) { BindSource = new(device,parent),NativeBind = expected,OriginalBindRequest = request,CleanupOnly = true };
                _originalIrqBindings.Add(lease.BindingId,binding); _irqBindings.Add(lease.BindingId,binding);
                device.FailedIrqBind = null;
            }
            if (!_originalIrqBindings.TryGetValue(binding.Binding.BindingId,out var owner) || !ReferenceEquals(owner,binding) ||
                !_irqBindings.TryGetValue(binding.Binding.BindingId,out var route) || !ReferenceEquals(route,binding) || !ReferenceEquals(binding.BindSource?.Device,device) || !ReferenceEquals(binding.BindSource?.Parent,parent))
                return Refuse("Original IRQ cleanup route foreign.");
            return PlatformAuthorityResult<PlatformOriginalIrqBindResolution>.Ok(new(request,false,binding.Binding));
        }
        PlatformAuthorityResult<PlatformOriginalIrqBindResolution> Refuse(string message) => PlatformAuthorityResult<PlatformOriginalIrqBindResolution>.Fail(PlatformAuthorityStatus.Faulted,message);
    }
}
