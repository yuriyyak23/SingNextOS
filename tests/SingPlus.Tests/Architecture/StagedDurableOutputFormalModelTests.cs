namespace SingPlus.Tests.Architecture;

public sealed class StagedDurableOutputFormalModelTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void CrashModelNamesEveryBarrierAndKeepsPublicationAfterDurability()
    {
        var model = Read("StagedDurableOutput.tla");
        string[] actions = ["WriteData", "PersistData", "WriteMetadata", "PersistMetadata", "CommitDurable", "Publish", "Crash", "Recover"];
        Assert.All(actions, action => Assert.Contains($"{action} ==", model, StringComparison.Ordinal));
        Assert.Contains("PublishedOnlyAfterDurable", Read("StagedDurableOutput.cfg"), StringComparison.Ordinal);
    }

    [Fact]
    public void CrashModelTreatsRecoveryAsCorrelationNotAuthority()
    {
        var model = Read("StagedDurableOutput.tla");
        Assert.Contains("freshAdmission", model, StringComparison.Ordinal);
        Assert.Contains("RecoveryNeverRestoresAuthority", Read("StagedDurableOutput.cfg"), StringComparison.Ordinal);
        Assert.DoesNotContain("DurableAuthority", model, StringComparison.Ordinal);
        Assert.DoesNotContain("RegionAuthority", model, StringComparison.Ordinal);
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(RepositoryRoot, "formal", "v6", file));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SingNextOS.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("SingNextOS repository root was not found.");
    }
}
