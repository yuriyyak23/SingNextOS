using System.Reflection;
using SingPlus.Runtime;

namespace SingPlus.Tests.SipJobs;

public sealed class Phase140ArchitecturalFreezeTests
{
    [Fact]
    public void EveryDeclaredSipJobGateIsOffByDefault()
    {
        Assert.Equal(Enum.GetValues<SipJobFeatureGate>().Length, SipJobFeatureGates.Names.Count);
        Assert.All(SipJobFeatureGates.Names, name => Assert.False(SipJobFeatureGates.IsEnabled(name)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("FG-UNKNOWN")]
    [InlineData("fg-job-linear")]
    [InlineData("FG-JOB-LINEAR ")]
    public void UnknownOrNonCanonicalGateFailsClosed(string? name)
    {
        Assert.False(SipJobFeatureGates.TryResolve(name, out var resolved));
        Assert.False(Enum.IsDefined(resolved));
        Assert.NotEqual(SipJobFeatureGate.Linear, resolved);
        Assert.False(SipJobFeatureGates.IsEnabled(name));
    }

    [Fact]
    public void QualificationTraceCarriesNoAuthorityOrRuntimeReferences()
    {
        var eventType = typeof(SipJobSemanticTraceEvent);
        var fieldTypes = eventType
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.PropertyType)
            .ToArray();

        Assert.Equal(
            [typeof(SipJobSemanticEventKind), typeof(string), typeof(ulong), typeof(SipJobSemanticOutcome)],
            fieldTypes);

        var trace = new SipJobSemanticTrace();
        trace.RecordCompleted(
            SipJobSemanticEventKind.SessionPinned,
            "session:opaque-1",
            7,
            SipJobSemanticOutcome.Succeeded);

        Assert.Single(trace.Completed);
        Assert.Equal(7UL, trace.Completed[0].Generation);
    }
}
