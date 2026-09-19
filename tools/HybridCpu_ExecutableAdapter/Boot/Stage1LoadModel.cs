using System.Security.Cryptography;
using YAKSys_Hybrid_CPU.Boot.Contracts;

namespace YAKSys_Hybrid_CPU.ExecutableAdapter.Boot;

internal enum Stage1LoadFailure
{
    None, EmptyComponent, ComponentLimit, InvalidAlignment, RangeOverflow,
    RangeOutsideRam, RangeOverlap, HashMismatch, InvalidEntry, CopyFault
}

internal readonly record struct Stage1ComponentDescriptor(
    string Name,
    ReadOnlyMemory<byte> Source,
    ReadOnlyMemory<byte> ExpectedSha384,
    ulong LoadAddress,
    ulong? EntryAddress,
    ulong Alignment);

internal readonly record struct LoadedComponent(
    string Name, ulong LoadAddress, ulong Length, ulong? EntryAddress, Memory<byte> Bytes);
internal readonly record struct Stage1SemanticFault(string Operation, int Ordinal, Stage1LoadFailure Failure);

internal sealed record Stage1LoadResult(
    Stage1LoadFailure Failure,
    IReadOnlyList<LoadedComponent> Components,
    IReadOnlyList<string> Trace)
{
    public bool IsSuccess => Failure == Stage1LoadFailure.None;
}

internal readonly record struct KernelEntryAbiV1(
    uint AbiVersion,
    ulong EntryAddress,
    ulong BootInfoAddress,
    uint BootInfoLength,
    uint Reserved);

internal sealed class Stage1LoadModel
{
    public const int MaxComponents = 64;
    public const int MaxComponentBytes = 64 * 1024 * 1024;

    public Stage1LoadResult Load(
        IReadOnlyList<Stage1ComponentDescriptor> descriptors,
        ulong ramBase,
        ulong ramBytes,
        Stage1SemanticFault? fault = null)
    {
        var trace = new List<string>();
        var loaded = new List<LoadedComponent>();
        if (descriptors.Count is 0 or > MaxComponents)
            return Fail(Stage1LoadFailure.ComponentLimit, loaded, trace);
        if (ramBytes == 0 || ramBase > ulong.MaxValue - ramBytes)
            return Fail(Stage1LoadFailure.RangeOverflow, loaded, trace);
        if (fault is { } configured && (configured.Operation != "CopyComponent" || configured.Ordinal <= 0 ||
            configured.Ordinal > descriptors.Count || configured.Failure != Stage1LoadFailure.CopyFault))
            throw new ArgumentException("Stage-1 fault plan must name CopyComponent, a present positive ordinal, and CopyFault.", nameof(fault));

        for (var index = 0; index < descriptors.Count; index++)
        {
            var item = descriptors[index];
            if (item.Source.Length is 0 or > MaxComponentBytes || item.ExpectedSha384.Length != 48)
                return FailAndClear(Stage1LoadFailure.EmptyComponent, loaded, trace);
            if (item.Alignment == 0 || (item.Alignment & (item.Alignment - 1)) != 0 || !BootWire.IsAligned(item.LoadAddress, item.Alignment))
                return FailAndClear(Stage1LoadFailure.InvalidAlignment, loaded, trace);
            if (item.LoadAddress < ramBase || !BootWire.TryRange(item.LoadAddress - ramBase, (ulong)item.Source.Length, ramBytes, out _, out _))
                return FailAndClear(item.LoadAddress < ramBase ? Stage1LoadFailure.RangeOutsideRam : Stage1LoadFailure.RangeOverflow, loaded, trace);
            if (item.LoadAddress > ulong.MaxValue - (ulong)item.Source.Length)
                return FailAndClear(Stage1LoadFailure.RangeOverflow, loaded, trace);
            var absoluteEnd = item.LoadAddress + (ulong)item.Source.Length;
            if (loaded.Any(x => item.LoadAddress < x.LoadAddress + x.Length && x.LoadAddress < absoluteEnd))
                return FailAndClear(Stage1LoadFailure.RangeOverlap, loaded, trace);
            if (item.EntryAddress is { } entry && (entry < item.LoadAddress || entry >= absoluteEnd || !BootWire.IsAligned(entry, ResetAbiProfileV1.BundleAlignment)))
                return FailAndClear(Stage1LoadFailure.InvalidEntry, loaded, trace);

            trace.Add($"CopyComponent#{index + 1}");
            if (fault is { Operation: "CopyComponent" } f && f.Ordinal == index + 1)
                return FailAndClear(Stage1LoadFailure.CopyFault, loaded, trace);
            var bytes = item.Source.ToArray();
            trace.Add($"HashDestination#{index + 1}");
            if (!CryptographicOperations.FixedTimeEquals(SHA384.HashData(bytes), item.ExpectedSha384.Span))
            {
                Array.Clear(bytes);
                return FailAndClear(Stage1LoadFailure.HashMismatch, loaded, trace);
            }
            loaded.Add(new(item.Name, item.LoadAddress, (ulong)bytes.Length, item.EntryAddress, bytes));
        }
        return new(Stage1LoadFailure.None, loaded, trace);
    }

    public static bool ValidateEntryAbi(KernelEntryAbiV1 entry, ulong ramBase, ulong ramBytes)
    {
        if (entry.AbiVersion != 1 || entry.Reserved != 0 || entry.BootInfoLength < HybridBootInfoCodec.HeaderSize)
            return false;
        if (entry.EntryAddress < ramBase || entry.BootInfoAddress < ramBase)
            return false;
        return BootWire.TryRange(entry.EntryAddress - ramBase, 1, ramBytes, out _, out _) &&
               BootWire.TryRange(entry.BootInfoAddress - ramBase, entry.BootInfoLength, ramBytes, out _, out _);
    }

    private static Stage1LoadResult Fail(Stage1LoadFailure failure, List<LoadedComponent> loaded, List<string> trace) =>
        new(failure, loaded, trace);

    private static Stage1LoadResult FailAndClear(Stage1LoadFailure failure, List<LoadedComponent> loaded, List<string> trace)
    {
        foreach (var item in loaded)
            item.Bytes.Span.Clear();
        loaded.Clear();
        return Fail(failure, loaded, trace);
    }
}
