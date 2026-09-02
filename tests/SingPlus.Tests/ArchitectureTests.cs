using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void RuntimeCreatesProcessFromManifest()
    {
        var manifest = new SingProcessManifest(new ProcessId(1), new DomainId(1), MemoryProfile.SipRegion);

        var process = new RuntimeKernel().CreateProcess(manifest);

        Assert.Equal(manifest, process.Manifest);
    }
}
