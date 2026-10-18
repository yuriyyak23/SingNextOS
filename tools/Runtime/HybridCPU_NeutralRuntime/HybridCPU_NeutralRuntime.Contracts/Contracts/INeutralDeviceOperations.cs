namespace YAKSys_Hybrid_CPU.Core;

public interface INeutralDeviceOperations
{
    NeutralDeviceBindResult BindDevice(NeutralDomainBindingLease domain, NeutralDeviceIdentity identity, NeutralDeviceRights rights);
    NeutralDeviceCloseResult CloseDevice(NeutralDeviceLease lease);
}
