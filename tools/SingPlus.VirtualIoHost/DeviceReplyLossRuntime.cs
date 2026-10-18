using System.Reflection;
using YAKSys_Hybrid_CPU.Core;

public interface IDeviceReplyLossRuntime : INeutralDomainRuntime,INeutralOriginalDeviceProvider { }
public class DeviceReplyLossRuntime : DispatchProxy
{
    public NeutralDomainRuntimeFacade Inner=null!;
    public bool Lose;
    public int DeviceBinds;
    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        if(Lose && method!.Name=="ResolveDeviceBind")throw new IOException("Original device Bind query lost");
        var result=method!.Invoke(Inner,args);
        if(method.Name=="BindDevice"){DeviceBinds++;if(Lose)throw new IOException("Original device Bind reply lost after native outcome");}
        return result;
    }
}
