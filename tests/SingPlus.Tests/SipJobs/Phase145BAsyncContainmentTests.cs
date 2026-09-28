using System.Reflection;
using SingPlus.Runtime;

namespace SingPlus.Tests.SipJobs;

public sealed class Phase145BAsyncContainmentTests
{
    [Fact]
    public void SuspendedStateIsClosedNonAuthoritativeMetadataAndGateRemainsOff()
    {
        var result = SipJobAsyncSuspensionVerifier.Verify(Valid());
        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { "input", "invocation", "cancel", "region" }, result.SlotIds.ToArray());
        Assert.False(SipJobFeatureGates.IsEnabled(SipJobAsyncSuspensionVerifier.AsyncGate));

        var forbidden = new[]
        {
            typeof(object), typeof(Task), typeof(ValueTask), typeof(Delegate), typeof(IServiceProvider),
            typeof(Type), typeof(global::System.Runtime.CompilerServices.INotifyCompletion)
        };
        var surface = typeof(SipJobAsyncSuspensionDescriptor).GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(static property => property.Name != "EqualityContract")
            .Select(static property => property.PropertyType)
            .Concat(typeof(SipJobSuspendedValueDescriptor).GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(static property => property.Name != "EqualityContract")
                .Select(static property => property.PropertyType));
        Assert.DoesNotContain(surface, type => forbidden.Contains(type));
        Assert.DoesNotContain(typeof(SipJobAsyncSuspensionResult).GetProperties(), property =>
            property.Name.Contains("Authoriz", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("Allowed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UnknownVersionGateKindAndOwnerFailClosed()
    {
        var valid = Valid();
        Assert.Equal(SipJobAsyncSuspensionError.UnknownVersion,
            SipJobAsyncSuspensionVerifier.Verify(new(2, valid.StageId, valid.Values, valid.DeclaredGateSet)).Error);
        Assert.Equal(SipJobAsyncSuspensionError.UnsupportedGate,
            SipJobAsyncSuspensionVerifier.Verify(new(1, valid.StageId, valid.Values, ["FG-JOB-LINEAR"])).Error);
        Assert.Equal(SipJobAsyncSuspensionError.UnsupportedState,
            SipJobAsyncSuspensionVerifier.Verify(new(1, valid.StageId,
                [valid.Values[0] with { Kind = (SipJobSuspendedValueKind)999 }], valid.DeclaredGateSet)).Error);
        Assert.Equal(SipJobAsyncSuspensionError.UnsupportedState,
            SipJobAsyncSuspensionVerifier.Verify(new(1, valid.StageId,
                [valid.Values[1] with { SchemaOrOwnerId = "Task" }], valid.DeclaredGateSet)).Error);
    }

    [Fact]
    public void DuplicateOrNonCanonicalSlotsFailBeforeRuntimeWork()
    {
        var valid = Valid();
        Assert.Equal(SipJobAsyncSuspensionError.Malformed,
            SipJobAsyncSuspensionVerifier.Verify(new(1, valid.StageId,
                [valid.Values[0], valid.Values[0]], valid.DeclaredGateSet)).Error);
        Assert.Equal(SipJobAsyncSuspensionError.Malformed,
            SipJobAsyncSuspensionVerifier.Verify(new(1, " stage ", valid.Values, valid.DeclaredGateSet)).Error);
    }

    [Fact]
    public void NullSuspendedValueFailsClosedBeforeVersionOrKindEvaluation()
    {
        var valid = Valid();
        var descriptor = new SipJobAsyncSuspensionDescriptor(
            1, valid.StageId, [null!], valid.DeclaredGateSet);

        Assert.Equal(SipJobAsyncSuspensionError.Malformed,
            SipJobAsyncSuspensionVerifier.Verify(descriptor).Error);
    }

    [Theory]
    [InlineData("schema:")]
    [InlineData("object")]
    [InlineData("region:value")]
    public void ClosedValueRequiresAnExactNonemptySchemaIdentity(string schemaOrOwnerId)
    {
        var valid = Valid();
        var descriptor = new SipJobAsyncSuspensionDescriptor(
            1,
            valid.StageId,
            [valid.Values[0] with { SchemaOrOwnerId = schemaOrOwnerId }],
            valid.DeclaredGateSet);

        Assert.Equal(SipJobAsyncSuspensionError.UnsupportedState,
            SipJobAsyncSuspensionVerifier.Verify(descriptor).Error);
    }

    private static SipJobAsyncSuspensionDescriptor Valid() => new(
        1,
        "stage-async-1",
        [
            new(1, "input", SipJobSuspendedValueKind.ClosedValue, "schema:closed-v1"),
            new(1, "invocation", SipJobSuspendedValueKind.InvocationCorrelation, "EndpointSessionInvocationRegistry"),
            new(1, "cancel", SipJobSuspendedValueKind.CancellationCorrelation, "CancellationScopeAuthority"),
            new(1, "region", SipJobSuspendedValueKind.RegionUseIdentity, "RegionAuthority"),
        ],
        [SipJobAsyncSuspensionVerifier.LinearGate, SipJobAsyncSuspensionVerifier.AsyncGate]);
}
