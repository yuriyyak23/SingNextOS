using YAKSys_Hybrid_CPU.Core;

namespace HybridCPU_NeutralRuntime.Tests;

public sealed class NeutralAssemblySeparationTests
{
    [Fact]
    public void ContractsAuthorityAndModelArePhysicallyDistinct()
    {
        Assert.Equal("HybridCPU_NeutralRuntime.Contracts", typeof(INeutralDomainRuntime).Assembly.GetName().Name);
        Assert.Equal("HybridCPU_NeutralRuntime.AuthorityCore", typeof(NeutralDependencyRegistry).Assembly.GetName().Name);
        Assert.Equal("HybridCPU_NeutralRuntime.Model", typeof(NeutralDomainRuntimeFacade).Assembly.GetName().Name);
        Assert.NotEqual(typeof(NeutralDependencyRegistry).Assembly, typeof(NeutralDomainRuntimeFacade).Assembly);
    }

    [Fact]
    public void AuthorityCoreDoesNotReferenceModelOrAdapter()
    {
        var references = typeof(NeutralDependencyRegistry).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(references, reference => reference.Name?.Contains("Model", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(references, reference => reference.Name?.Contains("ExecutableAdapter", StringComparison.OrdinalIgnoreCase) == true);
    }
}
