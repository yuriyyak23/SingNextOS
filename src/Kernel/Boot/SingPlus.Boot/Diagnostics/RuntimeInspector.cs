using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Boot.Diagnostics;

/// <summary>
/// Read-only projections of the emulated runtime, rendered as console tables.
/// Every view re-queries the authority graph so the shell always shows live state.
/// </summary>
public static class RuntimeInspector
{
    public static void WriteStatus(EmulatorSession session)
    {
        var runtime = session.Runtime;
        if (runtime is null)
        {
            EmulatorConsole.Error("[emulator] runtime authority graph is unavailable\r\n");
            return;
        }

        var processes = runtime.Processes.Snapshot();
        var rows = new List<IReadOnlyList<string>>
        {
            new[] { "platform profile", session.Options.PlatformProfile },
            new[] { "uptime", session.Uptime.Elapsed.ToString(@"hh\:mm\:ss\.fff") },
            new[] { "processes (live)", processes.Count.ToString() },
            new[] { "domains", runtime.Domains.Snapshot().Count.ToString() },
            new[] { "sip components", session.Sips.Count.ToString() },
            new[] { "regions", runtime.Regions.Snapshot().Count.ToString() },
            new[] { "channels (tracked)", session.Channels.Count.ToString() },
            new[] { "borrows (delivered, valid)", session.Borrows.Values.Count(static record => record.Lease.IsValid).ToString() },
            new[] { "trace sessions (tracked)", session.TraceSessions.Count.ToString() },
            new[] { "completed trace snapshots", session.CompletedTraces.Count.ToString() },
            new[] { "budget", session.IsBudgetConfigured ? "configured" : "not configured (boot with --budget)" },
        };
        EmulatorConsole.Table(["property", "value"], rows);
    }

    public static void WriteEnvironment(EmulatorSession session)
    {
        if (session.EnvironmentFacts.Count == 0)
        {
            EmulatorConsole.Info("[emulator] environment facts were not captured\r\n");
            return;
        }
        foreach (var fact in session.EnvironmentFacts)
            EmulatorConsole.Info($"  {fact}\r\n");
    }

    public static void WriteProcesses(EmulatorSession session)
    {
        var runtime = session.Runtime;
        if (runtime is null) { EmulatorConsole.Error("[emulator] runtime is unavailable\r\n"); return; }

        var processes = runtime.Processes.Snapshot();
        if (processes.Count == 0)
        {
            EmulatorConsole.Info("[emulator] no live processes\r\n");
            return;
        }

        var rows = processes.Select(static process => (IReadOnlyList<string>)new[]
        {
            process.ProcessId.Value.ToString(),
            process.Generation.ToString(),
            process.DomainId.Value.ToString(),
            process.ExecutionRole.ToString(),
            process.MemoryProfile.ToString(),
            process.State.ToString(),
            process.Channels.Count.ToString(),
            process.Regions.Count.ToString(),
            process.Capabilities.Items.Count.ToString(),
            process.Manifest.EntryIdentity,
        }).ToArray();
        EmulatorConsole.Table(
            ["pid", "gen", "domain", "role", "memory", "state", "chan", "regions", "caps", "identity"], rows);
    }

    public static void WriteProcessDetail(EmulatorSession session, ulong processIdValue)
    {
        var runtime = session.Runtime;
        if (runtime is null) { EmulatorConsole.Error("[emulator] runtime is unavailable\r\n"); return; }

        var handle = session.ResolvePid(processIdValue);
        if (!handle.IsSuccess)
        {
            EmulatorConsole.Error($"[emulator] {handle.Message}\r\n");
            return;
        }

        var process = runtime.Processes.Resolve(handle.Value).Value!;
        var manifest = process.Manifest;
        EmulatorConsole.Info($"process {process.ProcessId.Value} (generation {process.Generation})\r\n");
        EmulatorConsole.Table(["property", "value"],
        [
            new[] { "identity", manifest.EntryIdentity },
            new[] { "domain", process.DomainId.Value.ToString() },
            new[] { "role", process.ExecutionRole.ToString() },
            new[] { "memory profile", process.MemoryProfile.ToString() },
            new[] { "state", process.State.ToString() },
            new[] { "schema", $"{manifest.SchemaId} v{manifest.SchemaVersion}" },
            new[] { "contract digest", manifest.ContractDigest },
            new[] { "memory limit", $"{manifest.ResourceLimits.MaxMemoryBytes} bytes" },
            new[] { "region limit", manifest.ResourceLimits.MaxRegions.ToString() },
            new[] { "channel limit", manifest.ResourceLimits.MaxChannels.ToString() },
            new[] { "pending message limit", manifest.ResourceLimits.MaxPendingMessages.ToString() },
        ]);

        if (process.Channels.Count > 0)
        {
            EmulatorConsole.Info("channels:\r\n");
            foreach (var channel in process.Channels)
                EmulatorConsole.Detail($"  channel {channel.ChannelId.Value} endpoint {channel.EndpointId.Value} gen {channel.Generation}\r\n");
        }
        if (process.Regions.Count > 0)
        {
            EmulatorConsole.Info("regions:\r\n");
            foreach (var region in process.Regions)
                EmulatorConsole.Detail($"  region {region.RegionId.Value} gen {region.Generation}\r\n");
        }
        if (process.Capabilities.Items.Count > 0)
        {
            EmulatorConsole.Info("capabilities:\r\n");
            foreach (var capability in process.Capabilities.Items)
                EmulatorConsole.Detail($"  capability {capability.Value}\r\n");
        }

        var teardown = runtime.QueryProcessTeardown(handle.Value);
        if (teardown.IsSuccess)
            EmulatorConsole.Info($"teardown: phase={teardown.Value!.Phase} complete={teardown.Value.IsComplete}\r\n");
    }

    public static void WriteDomains(EmulatorSession session)
    {
        var runtime = session.Runtime;
        if (runtime is null) { EmulatorConsole.Error("[emulator] runtime is unavailable\r\n"); return; }

        var domains = runtime.Domains.Snapshot();
        var rows = domains.Select(static domain => (IReadOnlyList<string>)new[] { domain.Value.ToString() }).ToArray();
        EmulatorConsole.Table(["domain"], rows);
    }

    public static void WriteComponents(EmulatorSession session)
    {
        var runtime = session.Runtime;
        if (runtime is null) { EmulatorConsole.Error("[emulator] runtime is unavailable\r\n"); return; }
        if (session.Sips.Count == 0)
        {
            EmulatorConsole.Info("[emulator] no SIP components admitted (use --sip <path>)\r\n");
            return;
        }

        var rows = new List<IReadOnlyList<string>>();
        foreach (var sip in session.Sips)
        {
            var fresh = runtime.QueryComponent(sip.Identity);
            rows.Add(fresh.IsSuccess
                ?
                [
                    sip.Identity.Name,
                    sip.AssemblyVersion,
                    sip.StaticVerificationProof is null ? "not verified" : "ManagedCap static pass",
                    fresh.Value!.State.ToString(),
                    fresh.Value.Process.ProcessId.Value.ToString(),
                    fresh.Value.Domain.Value.ToString(),
                    $"{sip.ImageDigest[..16]}…",
                    sip.ImagePath,
                ]
                :
                [
                    sip.Identity.Name,
                    sip.AssemblyVersion,
                    sip.StaticVerificationProof is null ? "not verified" : "ManagedCap static pass",
                    $"query failed: {fresh.Error}",
                    "-", "-", "-",
                    sip.ImagePath,
                ]);
        }
        EmulatorConsole.Table(["component", "version", "static verification", "state", "pid", "domain", "sha256", "image"], rows);
    }

    public static void WriteRegions(EmulatorSession session)
    {
        var runtime = session.Runtime;
        if (runtime is null) { EmulatorConsole.Error("[emulator] runtime is unavailable\r\n"); return; }

        var regions = runtime.Regions.Snapshot();
        if (regions.Count == 0)
        {
            EmulatorConsole.Info("[emulator] no live memory regions\r\n");
            return;
        }

        var rows = regions.Select(static region => (IReadOnlyList<string>)new[]
        {
            region.Handle.RegionId.Value.ToString(),
            region.Handle.Generation.Value.ToString(),
            region.Owner.DomainId.Value.ToString(),
            region.Owner.ProcessGeneration.ToString(),
            region.ByteLength.ToString(),
            region.ElementType,
            region.State.ToString(),
        }).ToArray();
        EmulatorConsole.Table(["region", "gen", "owner domain", "owner gen", "bytes", "element", "state"], rows);
    }

    public static void WriteChannels(EmulatorSession session)
    {
        var runtime = session.Runtime;
        if (runtime is null) { EmulatorConsole.Error("[emulator] runtime is unavailable\r\n"); return; }
        if (session.Channels.Count == 0)
        {
            EmulatorConsole.Info("[emulator] no channels tracked (create one with 'channel <leftPid> <rightPid>')\r\n");
            return;
        }

        var rows = new List<IReadOnlyList<string>>();
        foreach (var record in session.Channels)
        {
            var endpoint = runtime.Channels.GetEndpoint(record.LeftEndpoint);
            rows.Add(endpoint.IsSuccess
                ?
                [
                    record.ChannelId.ToString(),
                    record.Left.ProcessId.Value.ToString(),
                    record.Right.ProcessId.Value.ToString(),
                    endpoint.Value!.ProtocolState,
                    endpoint.Value.Sequence.ToString(),
                    record.Capacity.ToString(),
                ]
                :
                [
                    record.ChannelId.ToString(),
                    record.Left.ProcessId.Value.ToString(),
                    record.Right.ProcessId.Value.ToString(),
                    $"closed ({endpoint.Error})",
                    "-",
                    record.Capacity.ToString(),
                ]);
        }
        EmulatorConsole.Table(["channel", "left pid", "right pid", "protocol state", "sequence", "capacity"], rows);
    }

    public static void WriteCapabilities(EmulatorSession session, ulong processIdValue)
    {
        var runtime = session.Runtime;
        if (runtime is null) { EmulatorConsole.Error("[emulator] runtime is unavailable\r\n"); return; }

        var handle = session.ResolvePid(processIdValue);
        if (!handle.IsSuccess)
        {
            EmulatorConsole.Error($"[emulator] {handle.Message}\r\n");
            return;
        }

        var process = runtime.Processes.Resolve(handle.Value).Value!;
        var descriptors = runtime.CapabilityAuthority.SnapshotForDomain(process.DomainId);
        if (descriptors.Count == 0)
        {
            EmulatorConsole.Info($"[emulator] domain {process.DomainId.Value} holds no capabilities\r\n");
            return;
        }

        var rows = descriptors.Select(static capability => (IReadOnlyList<string>)new[]
        {
            capability.CapabilityId.Value.ToString(),
            capability.ResourceKind.ToString(),
            capability.ResourceId,
            capability.Rights.ToString(),
            capability.Generation.ToString(),
            capability.RevocationEpoch.ToString(),
        }).ToArray();
        EmulatorConsole.Table(["capability", "resource kind", "resource id", "rights", "gen", "revocation epoch"], rows);
    }

    public static void WriteBudgets(EmulatorSession session)
    {
        var runtime = session.Runtime;
        if (runtime is null) { EmulatorConsole.Error("[emulator] runtime is unavailable\r\n"); return; }
        if (!session.IsBudgetConfigured)
        {
            EmulatorConsole.Info("[emulator] budgets are not configured; boot with --budget\r\n");
            return;
        }

        WriteBudgetAccount(runtime, session.SystemBudgetAccount!.Value, "system");
        foreach (var (pid, admission) in session.BudgetAccounts.OrderBy(static pair => pair.Key))
        {
            WriteBudgetAccount(runtime, admission.ProcessBudget, $"process {pid}");
            WriteBudgetAccount(runtime, admission.ServiceBudget, $"service correlation '{admission.ServiceBudget.AccountId.Value}'");
        }
    }

    private static void WriteBudgetAccount(RuntimeKernel runtime, BudgetAccountHandle account, string label)
    {
        var snapshot = runtime.QueryBudget(account);
        if (!snapshot.IsSuccess)
        {
            EmulatorConsole.Error($"[emulator] budget account {label}: {snapshot.Message}\r\n");
            return;
        }

        var value = snapshot.Value!;
        EmulatorConsole.Info(
            $"budget account {value.Account.AccountId.Value} ({label}, level {value.Level}, pressure {value.Pressure})\r\n");
        if (value.Usage.Count == 0)
        {
            EmulatorConsole.Detail("  (no dimension limits)\r\n");
            return;
        }

        EmulatorConsole.Table(["dimension", "used", "limit"],
            value.Usage.Select(static usage => (IReadOnlyList<string>)new[]
            {
                usage.Dimension.ToString(),
                usage.Used.ToString(),
                usage.Limit.ToString(),
            }).ToArray());
    }

    public static void WriteTraceSessions(EmulatorSession session)
    {
        if (session.TraceSessions.Count == 0 && session.CompletedTraces.Count == 0)
        {
            EmulatorConsole.Info("[emulator] no tracked trace sessions (use 'trace-start <pid>' or boot with --trace)\r\n");
            return;
        }

        EmulatorConsole.Table(["pid", "session", "producer", "state", "capacity"],
            session.TraceSessions
                .Concat(session.CompletedTraces.Select(static pair =>
                    new KeyValuePair<ulong, TraceSessionAdmission>(pair.Key, pair.Value.Session)))
                .OrderBy(static pair => pair.Key).Select(static pair =>
            {
                var admission = pair.Value;
                return (IReadOnlyList<string>)new[]
                {
                    pair.Key.ToString(),
                    admission.Session.SessionId.Value.ToString(),
                    admission.Producer.ProducerId.Value.ToString(),
                    admission.State.ToString(),
                    admission.ProducerCapacity.ToString(),
                };
            }).ToArray());
    }

    public static bool WriteTraceEvents(EmulatorSession session, ulong processIdValue, int count)
    {
        TraceSnapshot value;
        if (session.CompletedTraces.TryGetValue(processIdValue, out var completed))
        {
            value = completed;
        }
        else
        {
            var runtime = session.Runtime;
            if (runtime is null)
            {
                EmulatorConsole.Error("[emulator] runtime is unavailable\r\n");
                return false;
            }
            if (!session.TraceSessions.TryGetValue(processIdValue, out var admission))
            {
                EmulatorConsole.Error($"[emulator] no tracked trace session for process {processIdValue}\r\n");
                return false;
            }

            var snapshot = runtime.InspectTrace(admission.Owner, admission.Session);
            if (!snapshot.IsSuccess)
            {
                EmulatorConsole.Error($"[kernel] trace inspection failed ({snapshot.Error}): {snapshot.Message}\r\n");
                return false;
            }
            value = snapshot.Value!;
        }

        var events = value.Events;
        EmulatorConsole.Info(
            $"trace session {value.Session.Session.SessionId.Value} ({value.Session.State}): {events.Count} events, " +
            $"{value.DroppedEventCount} dropped, complete={value.Complete}\r\n");
        if (events.Count == 0) return true;

        var skip = Math.Max(0, events.Count - Math.Max(1, count));
        var rows = events.Skip(skip).Select(static traceEvent => (IReadOnlyList<string>)new[]
        {
            traceEvent.Sequence.Value.ToString(),
            traceEvent.Kind.ToString(),
            traceEvent.Data.ResourceClass,
            traceEvent.Data.ResourceCorrelation,
            traceEvent.Data.State,
            traceEvent.Data.Outcome,
            traceEvent.Correlation.Value,
        }).ToArray();
        EmulatorConsole.Table(["seq", "kind", "resource", "correlation-id", "state", "outcome", "causality"], rows);
        if (skip > 0) EmulatorConsole.Detail($"  ({skip} earlier events omitted; pass a larger count)\r\n");
        return true;
    }

    public static void WritePlatform(EmulatorSession session)
    {
        var runtime = session.Runtime;
        if (runtime is null) { EmulatorConsole.Error("[emulator] runtime is unavailable\r\n"); return; }

        if (session.Options.HostModelPlatform)
            EmulatorConsole.Info("[platform] Host model provider: availability is the provider contract classification, not physical hardware evidence\r\n");
        if (session.Options.HybridCpuChildPlatform)
            EmulatorConsole.Info("[platform] HybridCPU model parent plus existing executable child adapter; advertised features do not prove guest execution\r\n");
        if (session.Options.HybridCpuModelPlatform)
            EmulatorConsole.Info("[platform] Existing HybridCPU neutral model adapter; RuntimeAdmission features do not qualify physical execution\r\n");
        var manifest = runtime.QueryPlatformFeatures();
        if (manifest.Features.Count == 0)
        {
            EmulatorConsole.Info("[emulator] no platform backend installed; the feature manifest is empty\r\n");
            EmulatorConsole.Detail("  CXL discovery, HybridCPU execution and firmware evidence remain unavailable in this profile.\r\n");
            return;
        }

        EmulatorConsole.Table(["family", "contract", "availability"],
            manifest.Features.Select(static feature => (IReadOnlyList<string>)new[]
            {
                feature.Family.ToString(),
                feature.ContractVersion.ToString(),
                feature.Availability.ToString(),
            }).ToArray());
    }
}
