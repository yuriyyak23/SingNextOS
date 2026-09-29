using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class V6ManagedSafePointProviderTests
{
    [Fact]
    public void ExactManagedHookWithinBoundProducesValidNonAuthorityLifecycle()
    {
        var time = new TestTimeProvider();
        var provider = Provider(time);

        var result = provider.RequestSafePoint(Correlation, 3, 7, 11, () =>
        {
            time.AdvanceNanoseconds(500);
            return KernelResult.Ok();
        });

        Assert.True(result.IsSuccess, result.Message);
        var receipt = result.Value!;
        Assert.Equal(500UL, receipt.ObservedLatencyNanoseconds);
        Assert.Equal(PreemptionClassV1.SafePoint, receipt.Guarantee.Class);
        Assert.Equal(PreemptionEffectSemanticsV1.ContainedAtSafePoint,
            receipt.Guarantee.EffectSemantics);
        Assert.True(PreemptionLifecycleValidatorV1.IsValidSafePoint(receipt.Lifecycle));
        Assert.False(receipt.AuthorizesPreemption);
        Assert.False(receipt.AuthorizesExecution);
        Assert.False(receipt.ProvesPhysicalLatency);
    }

    [Fact]
    public void HookAfterAdvertisedBoundFailsClosedWithoutContainmentReceipt()
    {
        var time = new TestTimeProvider();
        var provider = Provider(time);

        var result = provider.RequestSafePoint(Correlation, 3, 7, 11, () =>
        {
            time.AdvanceNanoseconds(1_100);
            return KernelResult.Ok();
        });

        Assert.Equal(KernelError.DeadlineExpired, result.Error);
        Assert.Null(result.Value);
        Assert.Equal(KernelError.InvalidTransition,
            provider.RequestSafePoint(Correlation, 3, 7, 11, KernelResult.Ok).Error);
    }

    [Fact]
    public void ProviderFailureAndExceptionProduceNoSafePointEvidence()
    {
        var provider = Provider(new());

        var denied = provider.RequestSafePoint("managed-safe-point:denied", 3, 7, 11,
            () => KernelResult.Fail(KernelError.PlatformUnavailable, "unavailable"));
        var threw = provider.RequestSafePoint("managed-safe-point:threw", 3, 7, 11,
            () => throw new InvalidOperationException("lost"));

        Assert.Equal(KernelError.PlatformUnavailable, denied.Error);
        Assert.Equal(KernelError.PlatformUnavailable, threw.Error);
        Assert.Null(denied.Value);
        Assert.Null(threw.Value);
    }

    [Fact]
    public void ClockFailureBeforeHookQuarantinesAttemptWithoutInvokingProvider()
    {
        var time = new TestTimeProvider { ThrowOnRead = 1 };
        var provider = Provider(time);
        var calls = 0;

        var result = provider.RequestSafePoint(Correlation, 3, 7, 11, () =>
        {
            calls++;
            return KernelResult.Ok();
        });

        Assert.Equal(KernelError.PlatformFaulted, result.Error);
        Assert.Equal(0, calls);
        Assert.Equal(KernelError.InvalidTransition,
            provider.RequestSafePoint(Correlation, 3, 7, 11, KernelResult.Ok).Error);
    }

    [Fact]
    public void ClockFailureAfterHookQuarantinesPossibleEffectWithoutReceipt()
    {
        var time = new TestTimeProvider { ThrowOnRead = 2 };
        var provider = Provider(time);
        var calls = 0;

        var result = provider.RequestSafePoint(Correlation, 3, 7, 11, () =>
        {
            calls++;
            return KernelResult.Ok();
        });

        Assert.Equal(KernelError.PlatformFaulted, result.Error);
        Assert.Equal(1, calls);
        Assert.Null(result.Value);
        Assert.Equal(KernelError.InvalidTransition,
            provider.RequestSafePoint(Correlation, 3, 7, 11, KernelResult.Ok).Error);
    }

    [Fact]
    public void ProviderResetDuringHookRunsOutsideLockAndRejectsStaleCompletion()
    {
        var provider = Provider(new());

        var result = provider.RequestSafePoint(Correlation, 3, 7, 11, () =>
        {
            provider.ResetProvider();
            return KernelResult.Ok();
        });

        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.Equal(8UL, provider.ProviderGeneration);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TerminalGenerationResetQuarantinesInFlightSafePoint(bool providerReset)
    {
        var provider = new V6ManagedSafePointProvider("managed-safe-point-model-v1", 1_000,
            providerReset ? ulong.MaxValue : 7,
            providerReset ? 11 : ulong.MaxValue, new TestTimeProvider());
        var expectedProvider = provider.ProviderGeneration;
        var expectedRuntime = provider.RuntimeGeneration;

        var result = provider.RequestSafePoint(Correlation, 3, expectedProvider,
            expectedRuntime, () =>
            {
                if (providerReset) provider.ResetProvider();
                else provider.ResetRuntime();
                return KernelResult.Ok();
            });

        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.Null(result.Value);
        Assert.Equal(KernelError.StaleGeneration,
            provider.RequestSafePoint("managed-safe-point:new", 4, expectedProvider,
                expectedRuntime, KernelResult.Ok).Error);
        Assert.Equal(KernelError.StaleGeneration,
            provider.ValidateLoweringSafePointMap([], "point", 1, new string('a', 64),
                expectedProvider, expectedRuntime).Error);
    }

    [Fact]
    public void ExhaustedRequestGenerationDeniesBeforeProviderHook()
    {
        var provider = Provider(new TestTimeProvider());
        typeof(V6ManagedSafePointProvider).GetField("_nextRequestGeneration",
            global::System.Reflection.BindingFlags.Instance |
            global::System.Reflection.BindingFlags.NonPublic)!.SetValue(provider, ulong.MaxValue);
        var callbacks = 0;

        var result = provider.RequestSafePoint(Correlation, 3, 7, 11, () =>
        {
            callbacks++;
            return KernelResult.Ok();
        });

        Assert.Equal(KernelError.CapacityExhausted, result.Error);
        Assert.Equal(0, callbacks);
    }

    [Fact]
    public async Task ConcurrentRequestForExactOperationHasOneInFlightWinner()
    {
        var provider = Provider(new());
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var first = Task.Run(() => provider.RequestSafePoint(Correlation, 3, 7, 11, () =>
        {
            entered.Set();
            release.Wait();
            return KernelResult.Ok();
        }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        var duplicate = provider.RequestSafePoint(Correlation, 3, 7, 11, KernelResult.Ok);
        release.Set();
        var winner = await first;

        Assert.Equal(KernelError.InvalidTransition, duplicate.Error);
        Assert.True(winner.IsSuccess, winner.Message);
    }

    [Fact]
    public void StaleGenerationRejectsBeforeProviderHook()
    {
        var provider = Provider(new());
        var calls = 0;

        var result = provider.RequestSafePoint(Correlation, 3, 6, 11, () =>
        {
            calls++;
            return KernelResult.Ok();
        });

        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.Equal(0, calls);
    }

    private static V6ManagedSafePointProvider Provider(TestTimeProvider time) =>
        new("managed-safe-point-model-v1", 1_000, 7, 11, time);

    private sealed class TestTimeProvider : TimeProvider
    {
        private long _timestamp;
        private int _reads;
        internal int ThrowOnRead { get; init; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp()
        {
            if (Interlocked.Increment(ref _reads) == ThrowOnRead)
                throw new InvalidOperationException("Injected clock failure.");
            return Interlocked.Read(ref _timestamp);
        }

        internal void AdvanceNanoseconds(long nanoseconds)
        {
            if (nanoseconds < 0 || nanoseconds % 100 != 0)
                throw new ArgumentOutOfRangeException(nameof(nanoseconds));
            Interlocked.Add(ref _timestamp, nanoseconds / 100);
        }
    }

    private const string Correlation = "managed-safe-point:operation:1";
}
