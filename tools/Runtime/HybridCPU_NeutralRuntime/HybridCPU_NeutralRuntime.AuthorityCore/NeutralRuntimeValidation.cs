namespace YAKSys_Hybrid_CPU.Core;

internal static class NeutralRuntimeValidation
{
    public static bool IsValid(NeutralOwnedRegionSlice slice) =>
        slice.Offset >= 0 && slice.Length > 0 &&
        slice.Offset <= long.MaxValue - slice.Length &&
        slice.Access != NeutralMemoryAccess.None &&
        IsDefinedFlags(slice.Access, NeutralMemoryAccess.Read | NeutralMemoryAccess.Write);

    public static bool IsValid(NeutralDmaRange range) =>
        range.Offset >= 0 && range.Length > 0;

    public static bool IsValid(NeutralMmioRange range, NeutralMmioRegionIdentity region) =>
        region.ByteLength > 0 && range.Offset >= 0 && range.Length > 0 &&
        range.Offset < region.ByteLength && range.Length <= region.ByteLength - range.Offset;

    public static bool Has(NeutralDeviceRights available, NeutralDeviceRights requested) =>
        requested != NeutralDeviceRights.None && (available & requested) == requested;

    public static bool Has(NeutralMemoryAccess available, NeutralMemoryAccess requested) =>
        requested != NeutralMemoryAccess.None && (available & requested) == requested;

    public static bool Has(NeutralMmioAccess available, NeutralMmioAccess requested) =>
        requested != NeutralMmioAccess.None && (available & requested) == requested;

    public static bool IsDefinedFlags<T>(T value, T all) where T : struct, Enum =>
        (Convert.ToUInt64(value) & ~Convert.ToUInt64(all)) == 0;
}
