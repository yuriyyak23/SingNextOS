using SingPlus.Contracts;
namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    public KernelResult<PlatformMmio32Invocation> CreateComponentDriverMmio32Invocation(ComponentIdentity identity,
        string resource,long offset,bool write,uint value=0)
    {
        lock(_componentAdmissionGate)
        {
            if(!_components.TryGetValue(identity,out ComponentAdmissionRecord? record) || record.State!=ComponentLifecycleState.Running || record.DeviceResources is not { } resources)
                return KernelResult<PlatformMmio32Invocation>.Fail(KernelError.ComponentNotFound,"Running driver register resource owner absent.");
            MmioResourceGrant? grant =resources.Mmio.SingleOrDefault(g=>g.CapabilityResourceId==resource);
            return grant?.BackingRegion is not null
                ?KernelResult<PlatformMmio32Invocation>.Ok(new(grant.Lease,offset,write,value,this,record))
                :KernelResult<PlatformMmio32Invocation>.Fail(KernelError.PlatformUnsupported,"Original component software register backing absent.");
        }
    }
    public KernelResult<uint> AccessComponentDriverMmio32(ComponentIdentity identity,PlatformMmio32Invocation invocation)
    {
        ComponentAdmissionRecord expected;DeviceResourceSet resources;MmioResourceGrant grant;
        lock(_componentAdmissionGate)
        {
            if(!_components.TryGetValue(identity,out expected!) || expected.State!=ComponentLifecycleState.Running || expected.DeviceResources is not { } found)
                return KernelResult<uint>.Fail(KernelError.ComponentNotFound,"Running driver register owner absent.");
            resources=found;
            if(!ReferenceEquals(invocation.Original,invocation) || !ReferenceEquals(invocation.ComponentKernel,this) ||
                !ReferenceEquals(invocation.ComponentOwner,expected))
                return KernelResult<uint>.Fail(KernelError.StaleGeneration,"Original component invocation owner changed.");
            grant=resources.Mmio.SingleOrDefault(g=>g.Lease==invocation.Mapping)!;
            if(grant?.BackingRegion is null)return KernelResult<uint>.Fail(KernelError.PlatformDenied,"Register invocation is not this component's original MMIO grant.");
        }
        KernelResult Commit(Func<KernelResult> commit)
        {
            lock(_componentAdmissionGate)
            lock(expected.SnapshotGate)
            {
                if(!_components.TryGetValue(identity,out ComponentAdmissionRecord? current) || !ReferenceEquals(current,expected) || expected.State!=ComponentLifecycleState.Running ||
                    !ReferenceEquals(expected.DeviceResources,resources) || !resources.Mmio.Any(g=>ReferenceEquals(g,grant)))
                    return KernelResult.Fail(KernelError.StaleGeneration,"Component register owner/source changed before effect/publication.");
                return commit(); // Trusted local capability/owner commit only; callbacks stay outside.
            }
        }
        return AccessPlatformMmio32Core(expected.Process,invocation,Commit);
    }
    public KernelResult<uint> ResolveComponentDriverMmio32(ComponentIdentity identity,PlatformMmio32Invocation invocation)
    {
        if(!ReferenceEquals(invocation.Original,invocation) || !ReferenceEquals(invocation.ComponentKernel,this) ||
            invocation.ComponentOwner is not { } owner || owner.Manifest.Identity!=identity)
            return KernelResult<uint>.Fail(KernelError.StaleGeneration,"Original component invocation absent/copied/foreign.");
        // Original caller correlation survives routing retirement. The existing
        // bridge fact still proves exact invocation/map/source/history ownership;
        // this lookup neither admits an effect nor reacquires Region bytes.
        return PlatformAuthority.ResolveMmio32(invocation,invocation.Mapping.DeviceLease.DomainBinding.Subject);
    }
}
