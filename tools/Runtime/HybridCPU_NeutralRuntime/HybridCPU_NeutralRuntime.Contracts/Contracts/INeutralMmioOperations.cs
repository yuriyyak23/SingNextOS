namespace YAKSys_Hybrid_CPU.Core;

public interface INeutralMmioOperations
{
    NeutralMmioMapResult MapMmio(NeutralDeviceLease device, NeutralMmioRegionIdentity region, NeutralMmioRange range, NeutralMmioAccess access);
    NeutralMmioCloseResult CloseMmio(NeutralMmioLease lease);
}
