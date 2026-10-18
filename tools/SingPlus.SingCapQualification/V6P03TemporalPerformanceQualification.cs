using System.Diagnostics;
using System.Text.Json;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.SingCapQualification;

public static class V6P03TemporalPerformanceQualification
{
    private const int DefaultIterations = 1_000;
    private const int DefaultRounds = 7;
    private const ulong ReservedNanoseconds = 100;
    private const ulong SettledNanoseconds = 61;
    private static readonly Func<KernelResult> Allow = static () => KernelResult.Ok();

    public static int Run(string[] args)
    {
        try
        {
            Options options = Parse(args);
            Report report = Measure(options.Iterations, options.Rounds);
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(report,
                new JsonSerializerOptions { WriteIndented = true });
            string directory = Path.GetDirectoryName(options.OutputPath)
                ?? throw new ArgumentException("Output path must have a parent directory.");
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory,
                $".{Path.GetFileName(options.OutputPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(temporary, json);
                File.Move(temporary, options.OutputPath, true);
            }
            finally { File.Delete(temporary); }
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    private static Report Measure(int iterations, int rounds)
    {
        Warmup(Math.Min(iterations, 500));
        BudgetScenario ledger = CreateBudgetScenario();
        var provider = new V6ManagedTemporalCapacityProvider(10_000_000);
        BudgetScenario dualBudget = CreateBudgetScenario();
        var dualProvider = new V6ManagedTemporalCapacityProvider(10_000_000);
        var coordinator = new V6TemporalCapacityCoordinator(dualBudget.Budgets, dualProvider);

        var samples = new List<Sample>(checked(rounds * 3));
        for (int round = 0; round < rounds; round++)
        {
            string[] correlations = Enumerable.Range(0, iterations)
                .Select(index => $"p03:{round}:{index}").ToArray();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
            string[] order = (round % 3) switch
            {
                0 => ["budget-ledger-only", "provider-capacity-only", "dual-owner-managed-capacity"],
                1 => ["provider-capacity-only", "dual-owner-managed-capacity", "budget-ledger-only"],
                _ => ["dual-owner-managed-capacity", "budget-ledger-only", "provider-capacity-only"],
            };
            foreach (string arm in order)
            {
                samples.Add(arm switch
                {
                    "budget-ledger-only" => MeasureArm(arm, round, correlations,
                        _ => RunLedger(ledger)),
                    "provider-capacity-only" => MeasureArm(arm, round, correlations,
                        correlation => RunProvider(provider, correlation)),
                    _ => MeasureArm(arm, round, correlations,
                        correlation => RunDualOwner(dualBudget, dualProvider, coordinator, correlation)),
                });
            }
        }

        Summary summary = Summarize(samples);
        return new("singnext.v6.p03-temporal-performance/1", DateTimeOffset.UtcNow,
            new(Environment.Version.ToString(), Environment.OSVersion.ToString(),
                System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                Environment.ProcessorCount, Stopwatch.Frequency, "Release JIT host"),
            new(iterations, rounds, ReservedNanoseconds, SettledNanoseconds,
                "Correlations and budget/provider hierarchies are prepared before timing. Ledger-only executes reserve, bind, begin-consumption, and exact settlement. Provider-only executes canonical capacity reserve, begin-use, and release. Dual-owner executes budget reserve/bind, coordinator admission, three live allow gates, budget/provider revalidation, submit transition, exact settlement, and provider release. Arm order rotates; forced GC occurs only between rounds.",
                "Named managed provider and uncontended single-thread host only. No scheduler policy, competing workload, minimum-service enforcement, completion deadline, wall-clock inference, CPU affinity, isolated-host control, physical provider, or schedulability proof."),
            samples, summary,
            "Software accounting/reservation overhead only. It proves neither utilization benefit nor interference bounds, hard real-time, WCET, minimum service, guaranteed completion, physical capacity, production readiness, or gate promotion.");
    }

    private static void Warmup(int operations)
    {
        BudgetScenario ledger = CreateBudgetScenario();
        var provider = new V6ManagedTemporalCapacityProvider(10_000_000);
        BudgetScenario dualBudget = CreateBudgetScenario();
        var dualProvider = new V6ManagedTemporalCapacityProvider(10_000_000);
        var coordinator = new V6TemporalCapacityCoordinator(dualBudget.Budgets, dualProvider);
        for (int index = 0; index < operations; index++)
        {
            if (!RunLedger(ledger) || !RunProvider(provider, $"warmup:p:{index}") ||
                !RunDualOwner(dualBudget, dualProvider, coordinator, $"warmup:d:{index}"))
                throw new InvalidOperationException("P03 warmup violated its settlement invariant.");
        }
    }

    private static Sample MeasureArm(string arm, int round, IReadOnlyList<string> correlations,
        Func<string, bool> operation)
    {
        var elapsed = new long[correlations.Count];
        long allocated = 0;
        var wall = Stopwatch.StartNew();
        for (int index = 0; index < correlations.Count; index++)
        {
            long allocationStart = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            bool valid = operation(correlations[index]);
            elapsed[index] = Stopwatch.GetTimestamp() - started;
            allocated += GC.GetAllocatedBytesForCurrentThread() - allocationStart;
            if (!valid) throw new InvalidOperationException("P03 measured operation violated its settlement invariant.");
        }
        wall.Stop();
        Array.Sort(elapsed);
        return new(arm, round, correlations.Count, correlations.Count / wall.Elapsed.TotalSeconds,
            ToNanoseconds(elapsed[(int)Math.Ceiling(correlations.Count * 0.50) - 1]),
            ToNanoseconds(elapsed[(int)Math.Ceiling(correlations.Count * 0.95) - 1]),
            ToNanoseconds(elapsed[(int)Math.Ceiling(correlations.Count * 0.99) - 1]),
            ToNanoseconds(elapsed[^1]), (double)allocated / correlations.Count);
    }

    private static bool RunLedger(BudgetScenario scenario)
    {
        BudgetReservationHandle budget = ReserveAndBind(scenario);
        RequireValue(scenario.Budgets.BeginConsumption(scenario.Owner, budget));
        BudgetReservationSnapshot settled = RequireValue(scenario.Budgets.SettleLease(
            scenario.Owner, budget, [Amount(SettledNanoseconds)]));
        return ExactSettlement(settled);
    }

    private static bool RunProvider(V6ManagedTemporalCapacityProvider provider, string correlation)
    {
        V6TemporalProviderReservation reserved = RequireValue(provider.Reserve(correlation,
            Temporal().ComputeEnvelope));
        V6TemporalProviderReservation inUse = RequireValue(provider.BeginUse(reserved.Handle));
        V6TemporalProviderReservation released = RequireValue(provider.Release(inUse.Handle));
        return released.State == V6TemporalProviderReservationState.Released &&
            provider.ReservedNanoseconds == 0 && !released.GuaranteesDeadline;
    }

    private static bool RunDualOwner(BudgetScenario scenario,
        V6ManagedTemporalCapacityProvider provider, V6TemporalCapacityCoordinator coordinator,
        string correlation)
    {
        BudgetReservationHandle budget = ReserveAndBind(scenario);
        V6TemporalCapacityBinding admitted = RequireValue(coordinator.Admit(scenario.Owner,
            budget, correlation, Temporal()));
        V6TemporalCapacityBinding submitted = RequireValue(coordinator.Submit(admitted,
            Allow, Allow, Allow, Allow));
        V6TemporalCapacityBinding settled = RequireValue(coordinator.Settle(submitted,
            SettledNanoseconds));
        return settled.State == V6TemporalCapacityOperationState.Settled &&
            provider.ReservedNanoseconds == 0 && !settled.GuaranteesDeadline &&
            ExactSettlement(RequireValue(scenario.Budgets.Query(budget)));
    }

    private static BudgetReservationHandle ReserveAndBind(BudgetScenario scenario)
    {
        BudgetReservationSnapshot reserved = RequireValue(scenario.Budgets.Reserve(scenario.Owner,
            [Amount(ReservedNanoseconds)], BudgetReservationLifetime.ExternalEffect,
            AdmissionQosHint.BoundedInteractive));
        return RequireValue(scenario.Budgets.BindLease(scenario.Owner, reserved.Reservation)).Reservation;
    }

    private static BudgetScenario CreateBudgetScenario()
    {
        const ulong limit = 10_000_000;
        var budgets = new ResourceBudgetAuthority();
        RequireValue(budgets.ConfigureSystem([Amount(limit)]));
        BudgetAccountHandle service = RequireValue(budgets.CreateChild(budgets.SystemBudget,
            BudgetAccountLevel.Service, "p03-performance-service", [Amount(limit)])).Account;
        BudgetAccountHandle account = RequireValue(budgets.CreateChild(service,
            BudgetAccountLevel.ProcessDomain, "p03-performance-process", [Amount(limit)])).Account;
        var owner = new ProcessHandle(new(903_000), 1);
        Require(budgets.AttachProcess(owner, account));
        return new(budgets, owner);
    }

    private static bool ExactSettlement(BudgetReservationSnapshot settled) =>
        settled.State == BudgetReservationState.Released &&
        settled.ChargedAmounts is { Count: 1 } charged &&
        charged[0] == Amount(SettledNanoseconds);

    private static TemporalSemanticsV1 Temporal() => new(1,
        new ResourceEnvelopeV1(1, ResourceDimensionFamilyV1.Time, ResourceClassV1.ComputeTime,
            ResourceUnitV1.Nanoseconds, ReservedNanoseconds, 0, "managed:protected-capacity"),
        ResourceAssuranceV1.GuaranteedReservation, TemporalDeadlineSemanticsV1.None,
        DeadlineClockClass.MonotonicRuntime, 0);
    private static BudgetAmount Amount(ulong amount) =>
        new(ServiceBudgetDimension.ComputeTimeNanoseconds, amount);

    private static Summary Summarize(IReadOnlyList<Sample> samples)
    {
        double ledger = Median("budget-ledger-only");
        double provider = Median("provider-capacity-only");
        double dual = Median("dual-owner-managed-capacity");
        return new(ledger, provider, dual, PercentDelta(ledger, dual),
            SupportsGatePromotion: false,
            "Ledger and provider-only arms expose component costs; the dual-owner arm includes exact admission, live revalidation, submit, and settlement. No deadline or service guarantee is inferred.");

        double Median(string arm) => Percentile(samples.Where(sample => sample.Arm == arm)
            .Select(static sample => sample.MedianNanoseconds).Order().ToArray(), 0.5);
    }

    private static void Require(KernelResult result)
    {
        if (!result.IsSuccess) throw new InvalidOperationException(result.Message);
    }
    private static T RequireValue<T>(KernelResult<T> result)
    {
        if (!result.IsSuccess || result.Value is null) throw new InvalidOperationException(result.Message);
        return result.Value;
    }
    private static double PercentDelta(double baseline, double candidate) =>
        baseline == 0 ? 0 : (candidate - baseline) / baseline * 100;
    private static double Percentile(double[] ordered, double percentile) =>
        ordered[(int)Math.Ceiling(percentile * ordered.Length) - 1];
    private static double ToNanoseconds(long ticks) => ticks * (1_000_000_000d / Stopwatch.Frequency);

    private sealed record Options(string OutputPath, int Iterations, int Rounds);
    private sealed record BudgetScenario(ResourceBudgetAuthority Budgets, ProcessHandle Owner);
    public sealed record EnvironmentTuple(string Runtime, string OperatingSystem, string RuntimeIdentifier,
        int LogicalProcessorCount, long StopwatchFrequency, string ExecutionMode);
    public sealed record Methodology(int IterationsPerArmPerRound, int Rounds,
        ulong ReservedNanoseconds, ulong SettledNanoseconds, string TimingBoundary, string Limitations);
    public sealed record Sample(string Arm, int Round, int Operations, double ThroughputPerSecond,
        double MedianNanoseconds, double P95Nanoseconds, double P99Nanoseconds,
        double MaximumNanoseconds, double AllocatedBytesPerOperation);
    public sealed record Summary(double BudgetLedgerMedianNanoseconds,
        double ProviderCapacityMedianNanoseconds, double DualOwnerMedianNanoseconds,
        double DualOwnerOverLedgerPercent, bool SupportsGatePromotion, string Interpretation);
    public sealed record Report(string Schema, DateTimeOffset CapturedUtc, EnvironmentTuple Environment,
        Methodology Methodology, IReadOnlyList<Sample> Samples, Summary Summary,
        string ClaimBoundary);

    private static Options Parse(string[] args)
    {
        string? output = null;
        int iterations = DefaultIterations;
        int rounds = DefaultRounds;
        for (int index = 0; index < args.Length; index++)
        {
            string value = index + 1 < args.Length ? args[index + 1] : string.Empty;
            switch (args[index])
            {
                case "--output": output = value; index++; break;
                case "--iterations" when int.TryParse(value, out int parsed): iterations = parsed; index++; break;
                case "--rounds" when int.TryParse(value, out int parsed): rounds = parsed; index++; break;
                default: throw new ArgumentException($"Unknown or malformed option: {args[index]}");
            }
        }
        if (string.IsNullOrWhiteSpace(output)) throw new ArgumentException("--output is required.");
        if (iterations is < 100 or > 20_000) throw new ArgumentOutOfRangeException(nameof(iterations));
        if (rounds is < 5 or > 25) throw new ArgumentOutOfRangeException(nameof(rounds));
        return new(Path.GetFullPath(output), iterations, rounds);
    }
}
