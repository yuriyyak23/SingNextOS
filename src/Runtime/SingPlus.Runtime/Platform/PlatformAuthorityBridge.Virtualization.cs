using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class PlatformAuthorityBridge
{
    internal readonly record struct VirtualDomainBindingId(ulong Value);
    internal readonly record struct VirtualDomainBindingGeneration(ulong Value);
    internal readonly record struct VirtualDomainBinding(
        VirtualDomainBindingId BindingId,
        VirtualDomainBindingGeneration Generation,
        ProcessHandle Owner);

    private sealed record VirtualDomainRecord(
        VirtualDomainBinding Binding,
        PlatformProviderVirtualDomainLease ProviderLease);

    private readonly Dictionary<VirtualDomainBindingId, VirtualDomainRecord> _virtualDomainBindings = [];
    private ulong _nextVirtualDomainBindingId = 1;

    internal KernelResult<VirtualDomainBinding> CreateVirtualDomain(PlatformDomainIdentity owner, PlatformVirtualDomainProfile profile)
    {
        if (_provider is not IPlatformVirtualizationProvider provider || !_featureManifest.Supports(PlatformFeatureFamily.VirtualizationDomains, PlatformVirtualizationContract.ContractVersion, PlatformFeatureAvailability.ModelOnly))
            return KernelResult<VirtualDomainBinding>.Fail(KernelError.PlatformUnsupported, "Platform virtualization ModelOnly contract is unavailable.");
        var result = provider.CreateVirtualDomain(owner, profile);
        if (!result.IsSuccess)
            return FromProviderFailure<VirtualDomainBinding>(result.Status, result.Message);
        var binding = new VirtualDomainBinding(
            new VirtualDomainBindingId(_nextVirtualDomainBindingId++),
            new VirtualDomainBindingGeneration(1),
            owner.Process);
        _virtualDomainBindings.Add(binding.BindingId, new VirtualDomainRecord(binding, result.Value!));
        return KernelResult<VirtualDomainBinding>.Ok(binding);
    }

    internal KernelResult TransitionVirtualDomain(VirtualDomainBinding binding, PlatformVirtualDomainTransition transition)
    {
        if (_provider is not IPlatformVirtualizationProvider provider) return KernelResult.Fail(KernelError.PlatformUnsupported, "Platform virtualization provider is unavailable.");
        var record = ResolveVirtualDomain(binding);
        if (!record.IsSuccess) return KernelResult.Fail(record.Error, record.Message!);
        var result = provider.TransitionVirtualDomain(record.Value!.ProviderLease, transition);
        return result.IsSuccess ? KernelResult.Ok() : FromProviderFailure(result.Status, result.Message);
    }

    internal KernelResult RevokeVirtualDomain(VirtualDomainBinding binding)
    {
        if (_provider is not IPlatformVirtualizationProvider provider) return KernelResult.Fail(KernelError.PlatformUnsupported, "Platform virtualization provider is unavailable.");
        var record = ResolveVirtualDomain(binding);
        if (!record.IsSuccess) return KernelResult.Fail(record.Error, record.Message!);
        var result = provider.RevokeVirtualDomain(record.Value!.ProviderLease);
        if (!result.IsSuccess) return FromProviderFailure(result.Status, result.Message);
        _virtualDomainBindings.Remove(binding.BindingId);
        return KernelResult.Ok();
    }

    private KernelResult<VirtualDomainRecord> ResolveVirtualDomain(VirtualDomainBinding binding)
    {
        if (!_virtualDomainBindings.TryGetValue(binding.BindingId, out var record))
            return KernelResult<VirtualDomainRecord>.Fail(KernelError.PlatformBindingNotFound, "Virtual-domain platform binding was not found.");
        if (record.Binding.Generation != binding.Generation)
            return KernelResult<VirtualDomainRecord>.Fail(KernelError.StaleGeneration, "Virtual-domain platform binding generation is stale.");
        if (record.Binding != binding)
            return KernelResult<VirtualDomainRecord>.Fail(KernelError.WrongPlatformDomain, "Virtual-domain platform binding belongs to another local owner.");
        return KernelResult<VirtualDomainRecord>.Ok(record);
    }
}
