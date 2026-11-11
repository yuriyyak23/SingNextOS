using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class V6ManagedSafePointProviderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidUnicodeCannotRegisterRequestOrAliasSafePointEvidence(bool lowSurrogate)
    {
        string invalid = "point:" + (lowSurrogate ? '\uDC00' : '\uD800');
        Assert.ThrowsAny<ArgumentException>(() => new V6ManagedSafePointProvider(invalid, 1_000));
        V6ManagedSafePointProvider provider = Provider(new());
        int callbacks = 0;
        KernelResult<V6ManagedSafePointReceipt> denied = provider.RequestSafePoint(invalid, 3, 7, 11, () =>
        {
            callbacks++;
            return KernelResult.Ok();
        });
        Assert.Equal(KernelError.InvalidMessage, denied.Error);
        Assert.Equal(0, callbacks);
        Assert.Equal(KernelError.InvalidMessage,
            provider.ValidateLoweringSafePointMap([], invalid, 1, new string('a', 64), 7, 11).Error);

        KernelResult<V6ManagedSafePointReceipt> accepted = provider.RequestSafePoint("point:😀", 3, 7, 11, KernelResult.Ok);
        Assert.True(accepted.IsSuccess, accepted.Message);
        Assert.Equal(1UL, accepted.Value!.RequestGeneration);
        Assert.False(accepted.Value.AuthorizesExecution);
    }

    [Theory]
    [InlineData(1_000_000_000L, 99L, true, 99UL)]
    [InlineData(1_000_000_000L, 100L, true, 100UL)]
    [InlineData(1_000_000_000L, 101L, false, 0UL)]
    [InlineData(1_000_000_000L, 199L, false, 0UL)]
    [InlineData(3_000_000_000L, 300L, true, 100UL)]
    [InlineData(3_000_000_000L, 301L, false, 0UL)]
    [InlineData(3_000_000_000L, 1L, true, 1UL)]
    public void ExactClockRatioCannotRoundOverBoundIntoContainment(long frequency, long ticks, bool accepted, ulong observed)
    {
        RatioClock clock = new RatioClock(frequency, 0, ticks);
        V6ManagedSafePointProvider provider = new V6ManagedSafePointProvider("ratio-clock", 100, 7, 11, clock);
        KernelResult<V6ManagedSafePointReceipt> result = provider.RequestSafePoint(Correlation, 3, 7, 11, KernelResult.Ok);
        if (accepted)
        {
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(observed, result.Value!.ObservedLatencyNanoseconds);
        }
        else
        {
            Assert.Equal(KernelError.DeadlineExpired, result.Error);
            Assert.Null(result.Value);
        }
    }

    [Theory]
    [InlineData(1L, false)]
    [InlineData(1_000_000_000L, true)]
    public void FullSignedTimestampRangeUsesCheckedWideArithmetic(long frequency, bool accepted)
    {
        V6ManagedSafePointProvider provider = new V6ManagedSafePointProvider("wide-clock", ulong.MaxValue, 7, 11,
            new RatioClock(frequency, long.MinValue, long.MaxValue));
        KernelResult<V6ManagedSafePointReceipt> result = provider.RequestSafePoint(Correlation, 3, 7, 11, KernelResult.Ok);
        if (accepted)
        {
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(ulong.MaxValue, result.Value!.ObservedLatencyNanoseconds);
        }
        else Assert.Equal(KernelError.PlatformFaulted, result.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidOrChangedClockFrequencyCannotProduceReceipt(bool changed)
    {
        RatioClock clock = new RatioClock(changed ? 1_000_000_000 : 0, 0, 100);
        V6ManagedSafePointProvider provider = new V6ManagedSafePointProvider("frequency-clock", 100, 7, 11, clock);
        int calls = 0;
        KernelResult<V6ManagedSafePointReceipt> result = provider.RequestSafePoint(Correlation, 3, 7, 11, () =>
        {
            calls++;
            clock.Frequency = 2_000_000_000;
            return KernelResult.Ok();
        });
        Assert.Equal(KernelError.PlatformFaulted, result.Error);
        Assert.Null(result.Value);
        Assert.Equal(changed ? 1 : 0, calls);
    }

    private sealed class RatioClock(long frequency, long start, long end) : TimeProvider
    {
        private int _reads;
        internal long Frequency { get; set; } = frequency;
        public override long TimestampFrequency => Frequency;
        public override long GetTimestamp() => ++_reads == 1 ? start : end;
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-99)]
    [InlineData(0)]
    public void SubTickBackwardTimestampCannotBecomeContainmentReceipt(long delta)
    {
        SubTickClock clock = new SubTickClock(delta);
        V6ManagedSafePointProvider provider = new V6ManagedSafePointProvider("subtick-clock", 100, 7, 11, clock);
        KernelResult<V6ManagedSafePointReceipt> result = provider.RequestSafePoint(Correlation, 3, 7, 11, KernelResult.Ok);
        if (delta < 0)
        {
            Assert.Equal(KernelError.PlatformFaulted, result.Error);
            Assert.Null(result.Value);
            Assert.Equal(KernelError.InvalidTransition,
                provider.RequestSafePoint(Correlation, 3, 7, 11, KernelResult.Ok).Error);
        }
        else Assert.True(result.IsSuccess, result.Message);
    }

    private sealed class SubTickClock(long delta) : TimeProvider
    {
        private int _reads;
        public override long TimestampFrequency => 1_000_000_000;
        public override long GetTimestamp() => ++_reads == 1 ? 1_000 : 1_000 + delta;
    }

    [Fact]
    public void ExactManagedHookWithinBoundProducesValidNonAuthorityLifecycle()
    {
        TestTimeProvider time = new TestTimeProvider();
        V6ManagedSafePointProvider provider = Provider(time);

        KernelResult<V6ManagedSafePointReceipt> result = provider.RequestSafePoint(Correlation, 3, 7, 11, () =>
        {
            time.AdvanceNanoseconds(500);
            return KernelResult.Ok();
        });

        Assert.True(result.IsSuccess, result.Message);
        V6ManagedSafePointReceipt receipt = result.Value!;
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
        TestTimeProvider time = new TestTimeProvider();
        V6ManagedSafePointProvider provider = Provider(time);

        KernelResult<V6ManagedSafePointReceipt> result = provider.RequestSafePoint(Correlation, 3, 7, 11, () =>
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
        V6ManagedSafePointProvider provider = Provider(new());

        KernelResult<V6ManagedSafePointReceipt> denied = provider.RequestSafePoint("managed-safe-point:denied", 3, 7, 11,
            () => KernelResult.Fail(KernelError.PlatformUnavailable, "unavailable"));
        KernelResult<V6ManagedSafePointReceipt> threw = provider.RequestSafePoint("managed-safe-point:threw", 3, 7, 11,
            () => throw new InvalidOperationException("lost"));

        Assert.Equal(KernelError.PlatformUnavailable, denied.Error);
        Assert.Equal(KernelError.PlatformUnavailable, threw.Error);
        Assert.Null(denied.Value);
        Assert.Null(threw.Value);
    }

    [Fact]
    public void ClockFailureBeforeHookQuarantinesAttemptWithoutInvokingProvider()
    {
        TestTimeProvider time = new TestTimeProvider { ThrowOnRead = 1 };
        V6ManagedSafePointProvider provider = Provider(time);
        int calls = 0;

        KernelResult<V6ManagedSafePointReceipt> result = provider.RequestSafePoint(Correlation, 3, 7, 11, () =>
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
        TestTimeProvider time = new TestTimeProvider { ThrowOnRead = 2 };
        V6ManagedSafePointProvider provider = Provider(time);
        int calls = 0;

        KernelResult<V6ManagedSafePointReceipt> result = provider.RequestSafePoint(Correlation, 3, 7, 11, () =>
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
        V6ManagedSafePointProvider provider = Provider(new());

        KernelResult<V6ManagedSafePointReceipt> result = provider.RequestSafePoint(Correlation, 3, 7, 11, () =>
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
        V6ManagedSafePointProvider provider = new V6ManagedSafePointProvider("managed-safe-point-model-v1", 1_000,
            providerReset ? ulong.MaxValue : 7,
            providerReset ? 11 : ulong.MaxValue, new TestTimeProvider());
        ulong expectedProvider = provider.ProviderGeneration;
        ulong expectedRuntime = provider.RuntimeGeneration;

        KernelResult<V6ManagedSafePointReceipt> result = provider.RequestSafePoint(Correlation, 3, expectedProvider,
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
        V6ManagedSafePointProvider provider = Provider(new TestTimeProvider());
        typeof(V6ManagedSafePointProvider).GetField("_nextRequestGeneration",
            global::System.Reflection.BindingFlags.Instance |
            global::System.Reflection.BindingFlags.NonPublic)!.SetValue(provider, ulong.MaxValue);
        int callbacks = 0;

        KernelResult<V6ManagedSafePointReceipt> result = provider.RequestSafePoint(Correlation, 3, 7, 11, () =>
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
        V6ManagedSafePointProvider provider = Provider(new());
        using ManualResetEventSlim entered = new ManualResetEventSlim(false);
        using ManualResetEventSlim release = new ManualResetEventSlim(false);
        Task<KernelResult<V6ManagedSafePointReceipt>> first = Task.Run(() => provider.RequestSafePoint(Correlation, 3, 7, 11, () =>
        {
            entered.Set();
            release.Wait();
            return KernelResult.Ok();
        }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        KernelResult<V6ManagedSafePointReceipt> duplicate = provider.RequestSafePoint(Correlation, 3, 7, 11, KernelResult.Ok);
        release.Set();
        KernelResult<V6ManagedSafePointReceipt> winner = await first;

        Assert.Equal(KernelError.InvalidTransition, duplicate.Error);
        Assert.True(winner.IsSuccess, winner.Message);
    }

    [Fact]
    public void StaleGenerationRejectsBeforeProviderHook()
    {
        V6ManagedSafePointProvider provider = Provider(new());
        int calls = 0;

        KernelResult<V6ManagedSafePointReceipt> result = provider.RequestSafePoint(Correlation, 3, 6, 11, () =>
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
