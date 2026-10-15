using System.Reflection;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;

public interface IGuestSourceRefusalProvider : IPlatformOriginalAdmissionResolutionProvider,
    IPlatformFeatureProvider,IPlatformProviderIncarnationSource,IPlatformChildDomainProvider { }

// Faults source observation only; effect methods delegate to the actual HybridCPU provider.
public class GuestSourceRefusalProvider : DispatchProxy
{
    public HybridCpuPlatformAuthorityProvider Inner=null!;
    public Action? BeforeSource;
    public int RootBinds,ChildCreates;
    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        if(method!.Name=="get_CurrentIncarnation"){var action=BeforeSource;BeforeSource=null;action?.Invoke();}
        if(method.Name=="BindDomain")RootBinds++;
        if(method.Name=="CreateChildDomain")ChildCreates++;
        return method.Invoke(Inner,args);
    }
}
