using System.Security.Cryptography;
using YAKSys_Hybrid_CPU.ExecutableAdapter.Boot;
using Xunit;

namespace HybridCpu_ExecutableAdapter.Tests;

public sealed class Stage0RecoveryModelTests
{
    [Fact]
    public void Valid_local_recovery_is_copied_hashed_and_ready_only_from_ram()
    {
        var payload = Enumerable.Range(0, 8192).Select(i => (byte)i).ToArray();
        var result = Model().Run(Rom(), Recovery(payload), 0x100000, 0x100000);
        Assert.Equal(Stage0State.ReadyToEnter, result.State); Assert.Equal(Stage0Failure.None, result.Failure);
        Assert.Equal(payload, result.LoadedBytes.ToArray()); Assert.Equal(0x100000UL, result.EntryAddress);
        Assert.Equal(new[] { "CopyChunk#1", "CopyChunk#2" }, result.Trace.Where(x => x.StartsWith("CopyChunk", StringComparison.Ordinal)));
    }

    [Fact]
    public void Rom_budget_and_dependency_allowlist_fail_closed()
    {
        Assert.Equal(Stage0Failure.RomTooLarge, Model().Run(new(new byte[Stage0RecoveryModel.MaxExecutableAndReadonlyBytes + 1], new[] { "Boot.Contracts" }), Recovery([1]), 0x100000, 0x100000).Failure);
        Assert.Equal(Stage0Failure.ForbiddenDependency, Model().Run(new(new byte[1], new[] { "System.Net.Http" }), Recovery([1]), 0x100000, 0x100000).Failure);
    }

    [Fact]
    public void Bad_hash_invalid_entry_and_insufficient_ram_never_produce_executable_bytes()
    {
        var badHash = Recovery([1, 2, 3]) with { ExpectedSha384 = new byte[48] };
        Assert.Empty(Model().Run(Rom(), badHash, 0x100000, 0x100000).LoadedBytes.ToArray());
        var badEntry = Recovery(new byte[512]) with { EntryAddress = 0x100100 + 512 };
        Assert.Equal(Stage0Failure.InvalidEntry, Model().Run(Rom(), badEntry, 0x100000, 0x100000).Failure);
        Assert.Equal(Stage0Failure.NoUsableRam, Model().Run(Rom(), Recovery(new byte[8192]), 0x100000, 4096).Failure);
    }

    [Fact]
    public void Semantic_fault_ordinal_is_deterministic_and_zeroes_partial_copy()
    {
        var result = Model().Run(Rom(), Recovery(new byte[9000]), 0x100000, 0x100000, new("CopyChunk", 2, Stage0Failure.CopyFault));
        Assert.Equal(Stage0Failure.CopyFault, result.Failure); Assert.Empty(result.LoadedBytes.ToArray()); Assert.Contains("CopyChunk#2", result.Trace);
    }

    [Theory]
    [InlineData("CopyChunk", 0, (int)Stage0Failure.CopyFault)]
    [InlineData("UnknownOperation", 1, (int)Stage0Failure.CopyFault)]
    [InlineData("CopyChunk", 1, (int)Stage0Failure.None)]
    public void Malformed_semantic_fault_plans_are_rejected(string operation, int ordinal, int failure)
    {
        Assert.Throws<ArgumentException>(() =>
            Model().Run(Rom(), Recovery(new byte[512]), 0x100000, 0x100000, new(operation, ordinal, (Stage0Failure)failure)));
    }

    private static Stage0RecoveryModel Model() => new();
    private static RomImageModel Rom() => new(new byte[4096], new[] { "Boot.Contracts", "Boot.Crypto" });
    private static LocalRecoveryImage Recovery(byte[] payload)
    {
        var padded = payload.Length < 256 ? payload.Concat(new byte[256 - payload.Length]).ToArray() : payload;
        return new(padded, SHA384.HashData(padded), 0x100000, 0x100000);
    }
}
