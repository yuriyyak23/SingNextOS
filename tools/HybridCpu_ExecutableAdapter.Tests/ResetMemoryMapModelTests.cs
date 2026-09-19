using YAKSys_Hybrid_CPU.Boot.Contracts;
using YAKSys_Hybrid_CPU.ExecutableAdapter.Boot;
using Xunit;
using System.Xml.Linq;

namespace HybridCpu_ExecutableAdapter.Tests;

public sealed class ResetMemoryMapModelTests
{
    [Theory]
    [InlineData(ResetReason.ColdPowerOn)]
    [InlineData(ResetReason.WarmSoftware)]
    [InlineData(ResetReason.Watchdog)]
    [InlineData(ResetReason.PlatformRecovery)]
    public void Every_modeled_reset_has_the_v1_boundary_snapshot(ResetReason reason)
    {
        var snapshot = new AdapterResetModel().Assert(reason);
        Assert.Equal(ResetAbiProfileV1.ResetVector, snapshot.ProgramCounter);
        Assert.Equal(32, snapshot.IntegerRegisters.Count); Assert.All(snapshot.IntegerRegisters, value => Assert.Equal(0UL, value));
        Assert.True(snapshot.PhysicalAddressing && snapshot.InterruptsMasked && snapshot.PipelineEmpty && snapshot.ReplayEmpty && snapshot.RetireQueueEmpty && snapshot.ExternalEffectsQuiesced);
        Assert.Equal(0U, snapshot.BootVirtualThread); Assert.True(snapshot.SecondaryContextsParked);
    }

    [Fact]
    public void Reset_generation_invalidates_prior_adapter_snapshot()
    {
        var model = new AdapterResetModel(); var first = model.Assert(ResetReason.ColdPowerOn); var second = model.Assert(ResetReason.WarmSoftware);
        Assert.False(model.IsCurrent(first.ResetGeneration)); Assert.True(model.IsCurrent(second.ResetGeneration));
        Assert.NotEqual(first.ResetSequence, second.ResetSequence);
    }

    [Fact]
    public void Immutable_rom_is_read_execute_and_never_write()
    {
        var map = Map();
        Assert.Equal(PlatformRegionKind.ImmutableRom, map.Resolve(ResetAbiProfileV1.ResetVector, 256, PlatformAccess.Read | PlatformAccess.Execute).Kind);
        Assert.Throws<UnauthorizedAccessException>(() => map.Resolve(ResetAbiProfileV1.ResetVector, 1, PlatformAccess.Write));
        Assert.Throws<InvalidOperationException>(() => map.Resolve(ResetAbiProfileV1.ResetVector - 1, 2, PlatformAccess.Read));
    }

    [Fact]
    public void Physical_map_rejects_overlap_overflow_and_cross_boundary()
    {
        Assert.Throws<ArgumentException>(() => new AdapterPhysicalMap(new[] { new PlatformRegion(0, 100, PlatformRegionKind.SystemRam, PlatformAccess.Read), new PlatformRegion(99, 10, PlatformRegionKind.Mmio, PlatformAccess.Read) }));
        Assert.Throws<OverflowException>(() => new AdapterPhysicalMap(new[] { new PlatformRegion(ulong.MaxValue, 2, PlatformRegionKind.Mmio, PlatformAccess.Read) }));
        Assert.Throws<InvalidOperationException>(() => Map().Resolve(0x1fff, 2, PlatformAccess.Read));
    }

    [Fact]
    public void Architecture_guard_keeps_adapter_free_of_direct_hybridcpu_core_references()
    {
        var project = File.ReadAllText(Path.Combine(RepositoryRoot(), "tools", "HybridCpu_ExecutableAdapter", "HybridCpu_ExecutableAdapter.csproj"));
        Assert.DoesNotContain("HybridCPU_ISE", project, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RejectForbiddenAdapterProjectReferences", project, StringComparison.Ordinal);
        var xml = XDocument.Parse(project);
        Assert.DoesNotContain(xml.Descendants("ProjectReference"), reference =>
            ((string?)reference.Attribute("Include"))?.Contains("HybridCPU v2", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static AdapterPhysicalMap Map() => new(new[]
    {
        new PlatformRegion(0x1000, 0x1000, PlatformRegionKind.SystemRam, PlatformAccess.Read | PlatformAccess.Write | PlatformAccess.Execute),
        new PlatformRegion(ResetAbiProfileV1.RomBase, ResetAbiProfileV1.RomBytes, PlatformRegionKind.ImmutableRom, PlatformAccess.Read | PlatformAccess.Execute)
    });

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "SingNextOS.slnx"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException();
    }
}
