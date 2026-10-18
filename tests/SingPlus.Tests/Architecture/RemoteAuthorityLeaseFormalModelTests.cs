namespace SingPlus.Tests.Architecture;

public sealed class RemoteAuthorityLeaseFormalModelTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void FormalProtocolKeepsOneOwnerAndUsesLogicalSequenceInsteadOfWallClock()
    {
        var model = Read("RemoteAuthorityLease.tla");

        Assert.Contains("original logical owner", model, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never a distributed", model, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NotAfterOwnerSequence", model, StringComparison.Ordinal);
        Assert.DoesNotContain("DateTime", model, StringComparison.Ordinal);
        Assert.DoesNotContain("GlobalCapability", model, StringComparison.Ordinal);
    }

    [Fact]
    public void FormalProtocolNamesPartitionRenewRevokeRebootClosureAndReclaimSafety()
    {
        var model = Read("RemoteAuthorityLease.tla");
        string[] actions =
        ["RemoteSubmit", "Partition", "Renew", "Revoke", "OwnerReboot",
         "RemoteReboot", "Fence", "ObserveEffectClosure", "Reclaim"];
        string[] invariants =
        ["AtMostOneRemoteSubmit", "NoSubmitAcrossPartition", "ReclaimRequiresFenceAndClosure",
         "RebootNeverProvesClosure", "NoExpiryBasedReclaim"];

        Assert.All(actions, action => Assert.Contains($"{action} ==", model, StringComparison.Ordinal));
        var config = Read("RemoteAuthorityLease.cfg");
        Assert.All(invariants, invariant => Assert.Contains($"INVARIANT {invariant}", config, StringComparison.Ordinal));
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(
        RepositoryRoot, "formal", "v6", file));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SingNextOS.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("SingNextOS repository root was not found.");
    }
}
