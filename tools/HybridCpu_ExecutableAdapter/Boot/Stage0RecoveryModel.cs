using System.Security.Cryptography;
using YAKSys_Hybrid_CPU.Boot.Contracts;

namespace YAKSys_Hybrid_CPU.ExecutableAdapter.Boot;

internal enum Stage0State { Entry, ScratchReady, ProfileValidated, RecoverySelected, Copying, Verified, ReadyToEnter, Failed }
internal enum Stage0Failure { None, RomTooLarge, ForbiddenDependency, InvalidDescriptor, NoUsableRam, CopyFault, HashMismatch, InvalidEntry, OperationBudgetExceeded }
internal readonly record struct SemanticFault(string Operation, int Ordinal, Stage0Failure Failure);

internal sealed record RomImageModel(ReadOnlyMemory<byte> Bytes, IReadOnlyList<string> Dependencies);
internal sealed record LocalRecoveryImage(ReadOnlyMemory<byte> Payload, ReadOnlyMemory<byte> ExpectedSha384, ulong LoadAddress, ulong EntryAddress);
internal sealed record Stage0Result(Stage0State State, Stage0Failure Failure, ulong EntryAddress, ReadOnlyMemory<byte> LoadedBytes, IReadOnlyList<string> Trace);

internal sealed class Stage0RecoveryModel
{
    private static readonly HashSet<string> AllowedDependencies = new(StringComparer.Ordinal)
    {
        "Boot.Contracts", "Boot.Crypto", "Boot.Platform", "System.Memory"
    };
    private static readonly HashSet<string> FaultableOperations = new(StringComparer.Ordinal)
    {
        "Entry", "InitScratch", "ValidateProfile", "SelectLocalRecovery", "CopyChunk", "HashDestination", "ValidateEntry"
    };

    public const int MaxRomBytes = 256 * 1024;
    public const int MaxExecutableAndReadonlyBytes = 128 * 1024;
    public const int MaxRecoveryBytes = 8 * 1024 * 1024;
    public const int MaxOperations = 32;

    public Stage0Result Run(RomImageModel rom, LocalRecoveryImage recovery, ulong ramBase, ulong ramBytes, SemanticFault? fault = null)
    {
        if (fault is { } configured && (configured.Ordinal <= 0 || configured.Failure == Stage0Failure.None ||
            !FaultableOperations.Contains(configured.Operation)))
            throw new ArgumentException("Fault plan must name a faultable semantic operation, a positive ordinal, and a non-None failure.", nameof(fault));
        var trace = new List<string>(); var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
        Stage0Result Fail(Stage0Failure failure) => new(Stage0State.Failed, failure, 0, ReadOnlyMemory<byte>.Empty, trace);
        bool Step(string operation, out Stage0Failure failure)
        {
            if (trace.Count >= MaxOperations) { failure = Stage0Failure.OperationBudgetExceeded; return false; }
            var ordinal = ordinals.TryGetValue(operation, out var current) ? current + 1 : 1; ordinals[operation] = ordinal; trace.Add($"{operation}#{ordinal}");
            if (fault is { } f && f.Operation == operation && f.Ordinal == ordinal) { failure = f.Failure; return false; }
            failure = Stage0Failure.None; return true;
        }

        if (!Step("Entry", out var failure)) return Fail(failure);
        if (rom.Bytes.Length == 0 || rom.Bytes.Length > MaxRomBytes || rom.Bytes.Length > MaxExecutableAndReadonlyBytes) return Fail(Stage0Failure.RomTooLarge);
        if (rom.Dependencies.Count > 16 || rom.Dependencies.Any(x => !AllowedDependencies.Contains(x))) return Fail(Stage0Failure.ForbiddenDependency);
        if (!Step("InitScratch", out failure) || !Step("ValidateProfile", out failure) || !Step("SelectLocalRecovery", out failure)) return Fail(failure);
        if (recovery.Payload.Length == 0 || recovery.Payload.Length > MaxRecoveryBytes || recovery.ExpectedSha384.Length != 48 ||
            !BootWire.IsAligned(recovery.LoadAddress, ResetAbiProfileV1.BundleAlignment) || recovery.EntryAddress < recovery.LoadAddress)
            return Fail(Stage0Failure.InvalidDescriptor);
        if (!BootWire.TryRange(recovery.LoadAddress - Math.Min(recovery.LoadAddress, ramBase), (ulong)recovery.Payload.Length, ramBytes, out _, out _) ||
            recovery.LoadAddress < ramBase || (ulong)recovery.Payload.Length > ramBytes - (recovery.LoadAddress - ramBase)) return Fail(Stage0Failure.NoUsableRam);
        if (recovery.EntryAddress - recovery.LoadAddress >= (ulong)recovery.Payload.Length || !BootWire.IsAligned(recovery.EntryAddress, ResetAbiProfileV1.BundleAlignment)) return Fail(Stage0Failure.InvalidEntry);
        var destination = new byte[recovery.Payload.Length];
        for (var offset = 0; offset < recovery.Payload.Length; offset += 4096)
        {
            if (!Step("CopyChunk", out failure)) { Array.Clear(destination); return Fail(failure == Stage0Failure.None ? Stage0Failure.CopyFault : failure); }
            var length = Math.Min(4096, recovery.Payload.Length - offset); recovery.Payload.Span.Slice(offset, length).CopyTo(destination.AsSpan(offset));
        }
        if (!Step("HashDestination", out failure)) { Array.Clear(destination); return Fail(failure); }
        if (!CryptographicOperations.FixedTimeEquals(SHA384.HashData(destination), recovery.ExpectedSha384.Span)) { Array.Clear(destination); return Fail(Stage0Failure.HashMismatch); }
        if (!Step("ValidateEntry", out failure)) { Array.Clear(destination); return Fail(failure); }
        trace.Add("ReadyToEnter#1");
        return new(Stage0State.ReadyToEnter, Stage0Failure.None, recovery.EntryAddress, destination, trace);
    }
}
