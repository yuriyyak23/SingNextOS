using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SingPlus.Admission;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SingPlus.Tests.Analyzers;

namespace SingPlus.Tests.Admission;

public sealed class AdmissionTelemetryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnwritableTelemetryPreservesCliAdmissionResult(bool rejected)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "telemetry-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var assembly = Path.Combine(directory, "Corpus.dll");
            var source = rejected ? "public static class Fixture { public static object Root() => new object(); }"
                : "public static class Fixture { public static int Root() => 1; }";
            var compilation = CSharpCompilation.Create("Corpus", [CSharpSyntaxTree.ParseText(source)],
                AnalyzerTests.PlatformReferences(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            Assert.True(compilation.Emit(assembly).Success);
            var baseline = await Invoke("baseline.json", false);
            var failed = await Invoke("failed.json", true);
            Assert.Equal(rejected ? 2 : 0, baseline.ExitCode);
            Assert.Equal(baseline.ExitCode, failed.ExitCode);
            Assert.Equal(File.ReadAllBytes(Path.Combine(directory, "baseline.json")), File.ReadAllBytes(Path.Combine(directory, "failed.json")));
            Assert.Contains("Telemetry output failed", failed.Error, StringComparison.Ordinal);
            if (rejected) Assert.Contains(baseline.Error, failed.Error, StringComparison.Ordinal);

            var inputBytes = File.ReadAllBytes(assembly);
            var canonicalBytes = File.ReadAllBytes(Path.Combine(directory, "baseline.json"));
            foreach (var destination in new[] { assembly, Path.Combine(directory, "baseline.json") })
            {
                var collision = await Invoke("baseline.json", true, destination);
                Assert.Equal(baseline.ExitCode, collision.ExitCode);
                Assert.Contains("destinations are disabled", collision.Error, StringComparison.Ordinal);
                Assert.Equal(inputBytes, File.ReadAllBytes(assembly));
                Assert.Equal(canonicalBytes, File.ReadAllBytes(Path.Combine(directory, "baseline.json")));
            }
            var archiveCollision = await Invoke("baseline.json", true, Path.Combine(directory, "trace.ndjson"), assembly);
            Assert.Equal(baseline.ExitCode, archiveCollision.ExitCode);
            Assert.Equal(inputBytes, File.ReadAllBytes(assembly));
            Assert.False(File.Exists(Path.Combine(directory, "trace.ndjson")));

            var existingTrace = Path.Combine(directory, "existing.ndjson");
            var existingArchive = Path.Combine(directory, "existing.zstd");
            byte[] sentinel = [1, 2, 3, 4];
            File.WriteAllBytes(existingTrace, sentinel);
            File.WriteAllBytes(existingArchive, sentinel);
            var traceRefusal = await Invoke("baseline.json", true, existingTrace);
            Assert.Equal(baseline.ExitCode, traceRefusal.ExitCode);
            Assert.Contains("Telemetry output failed", traceRefusal.Error, StringComparison.Ordinal);
            Assert.Equal(sentinel, File.ReadAllBytes(existingTrace));
            var archiveRefusal = await Invoke("baseline.json", true, Path.Combine(directory, "new.ndjson"), existingArchive);
            Assert.Equal(baseline.ExitCode, archiveRefusal.ExitCode);
            Assert.Contains("Telemetry output failed", archiveRefusal.Error, StringComparison.Ordinal);
            Assert.Equal(sentinel, File.ReadAllBytes(existingArchive));
            Assert.Equal(canonicalBytes, File.ReadAllBytes(Path.Combine(directory, "baseline.json")));
            var freshTrace = Path.Combine(directory, "fresh.ndjson");
            var freshArchive = Path.Combine(directory, "fresh.zstd");
            var success = await Invoke("baseline.json", true, freshTrace, freshArchive);
            Assert.Equal(baseline.ExitCode, success.ExitCode);
            Assert.DoesNotContain("Telemetry output failed", success.Error, StringComparison.Ordinal);
            Assert.Equal(File.ReadAllBytes(freshTrace),
                TelemetryZstdStorage.Unpack(TelemetryZstdStorage.Deserialize(File.ReadAllBytes(freshArchive))));
            Assert.Equal(canonicalBytes, File.ReadAllBytes(Path.Combine(directory, "baseline.json")));

            async Task<(int ExitCode, string Error)> Invoke(string proof, bool telemetry, string? destination = null, string? archive = null)
            {
                var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                foreach (var argument in new[] { "exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "SingPlus.Tests.runtimeconfig.json"),
                    typeof(AdmissionTelemetry).Assembly.Location, "verify", "--assembly", assembly, "--root", "Fixture::Root",
                    "--profile", "KernelNoHeap", "--proof", Path.Combine(directory, proof) }) start.ArgumentList.Add(argument);
                if (telemetry) { start.ArgumentList.Add("--telemetry"); start.ArgumentList.Add(destination ?? directory); }
                if (archive is not null) { start.ArgumentList.Add("--telemetry-zstd"); start.ArgumentList.Add(archive); }
                using var process = Process.Start(start)!;
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await process.WaitForExitAsync(timeout.Token); }
                finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                await output;
                return (process.ExitCode, await error);
            }
        }
        finally
        {
            var basePath = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(directory).StartsWith(basePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Telemetry fixture cleanup escaped test output.");
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task NdjsonIsOrderedAndCannotChangeSemanticProof()
    {
        var result = Fixture();
        var canonicalBefore = result.Proof.SerializeCanonical(result.Violations);
        var events = AdmissionTelemetry.Observe(result);
        await using var first = new MemoryStream();
        await using var second = new MemoryStream();
        await AdmissionTelemetry.WriteNdjsonAsync(first, events);
        await AdmissionTelemetry.WriteNdjsonAsync(second, events);
        Assert.Equal(first.ToArray(), second.ToArray());
        Assert.Equal(canonicalBefore, result.Proof.SerializeCanonical(result.Violations));
        var lines = Encoding.UTF8.GetString(first.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        using var record = JsonDocument.Parse(lines[1]);
        Assert.Equal("A", record.RootElement.GetProperty("Method").GetString());
        Assert.Equal(1, record.RootElement.GetProperty("Sequence").GetInt32());
    }

    [Fact]
    public void ActivityTraceCorrelatesOnlyObservation()
    {
        var result = Fixture();
        var canonical = result.Proof.SerializeCanonical(result.Violations);
        using var activity = new Activity("qualification").Start();
        Assert.Equal(activity.TraceId.ToString(), AdmissionTelemetry.Observe(result)[0].TraceId);
        Assert.Equal(canonical, result.Proof.SerializeCanonical(result.Violations));
    }

    [Fact]
    public void OptInObservationHasTraceEvenWithoutAnActivitySourceListener()
    {
        var result = Fixture();
        var canonical = result.Proof.SerializeCanonical(result.Violations);
        using (var activity = AdmissionTelemetry.StartObservation())
        {
            Assert.NotEqual(default, activity.TraceId);
            Assert.All(AdmissionTelemetry.Observe(result), item => Assert.Equal(activity.TraceId.ToString(), item.TraceId));
        }
        Assert.Equal(canonical, result.Proof.SerializeCanonical(result.Violations));
    }

    [Fact]
    public async Task NdjsonStreamingHasFixedFieldOrderAndKeepsOutputOpen()
    {
        await using var output = new MemoryStream();
        var events = new[]
        {
            new AdmissionTelemetryEvent(0, AdmissionTelemetryKind.VerificationCompleted, "Fixture::Root", "Admitted", null),
            new AdmissionTelemetryEvent(1, AdmissionTelemetryKind.ViolationObserved, "A", "read:x", "0123456789abcdef0123456789abcdef")
        };
        await AdmissionTelemetry.WriteNdjsonAsync(output, events);
        Assert.True(output.CanWrite);
        Assert.Equal(
            "{\"Sequence\":0,\"Kind\":\"VerificationCompleted\",\"Method\":\"Fixture::Root\",\"Detail\":\"Admitted\",\"TraceId\":null}\n" +
            "{\"Sequence\":1,\"Kind\":\"ViolationObserved\",\"Method\":\"A\",\"Detail\":\"read:x\",\"TraceId\":\"0123456789abcdef0123456789abcdef\"}\n",
            Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public void ZstdStorageRoundTripPreservesRawIdentity()
    {
        var raw = Encoding.UTF8.GetBytes("{\"Kind\":\"VerificationCompleted\"}\n");
        var archive = TelemetryZstdStorage.Pack(raw);
        var stored = TelemetryZstdStorage.Serialize(archive);
        var reloaded = TelemetryZstdStorage.Deserialize(stored);
        Assert.Equal(Convert.ToHexString(global::System.Security.Cryptography.SHA256.HashData(raw)).ToLowerInvariant(), archive.RawDigest);
        Assert.NotEqual(Convert.ToHexString(global::System.Security.Cryptography.SHA256.HashData(archive.CompressedBytes)).ToLowerInvariant(), archive.RawDigest);
        Assert.Equal("SINGZST1", Encoding.ASCII.GetString(stored, 0, 8));
        Assert.Equal(stored, TelemetryZstdStorage.Serialize(TelemetryZstdStorage.Pack(raw)));
        Assert.Equal(raw, TelemetryZstdStorage.Unpack(reloaded));
        Assert.Throws<InvalidDataException>(() => TelemetryZstdStorage.Unpack(archive with { RawDigest = new string('0', 64) }));
        var badMagic = (byte[])stored.Clone();
        badMagic[0] ^= 1;
        Assert.Throws<InvalidDataException>(() => TelemetryZstdStorage.Deserialize(badMagic));
        var badDigest = (byte[])stored.Clone();
        badDigest[12] ^= 1;
        Assert.Throws<InvalidDataException>(() => TelemetryZstdStorage.Unpack(TelemetryZstdStorage.Deserialize(badDigest)));
        Assert.Throws<InvalidDataException>(() => TelemetryZstdStorage.Unpack(reloaded with { RawLength = 1 }));
        var trailing = reloaded with { CompressedBytes = [.. reloaded.CompressedBytes, 0xff, 0x01] };
        Assert.Throws<InvalidDataException>(() => TelemetryZstdStorage.Unpack(trailing));
        var secondFrame = TelemetryZstdStorage.Pack(Encoding.UTF8.GetBytes("hidden\n"));
        var concatenated = reloaded with { CompressedBytes = [.. reloaded.CompressedBytes, .. secondFrame.CompressedBytes] };
        Assert.Throws<InvalidDataException>(() => TelemetryZstdStorage.Unpack(concatenated));
    }

    [Fact]
    public void CompressionLevelCannotChangeRawTraceDigest()
    {
        var raw = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("{\"Event\":\"Observed\"}\n", 1000)));
        var fastest = TelemetryZstdStorage.Pack(raw, global::System.IO.Compression.CompressionLevel.Fastest);
        var optimal = TelemetryZstdStorage.Pack(raw, global::System.IO.Compression.CompressionLevel.Optimal);
        Assert.Equal(fastest.RawDigest, optimal.RawDigest);
        Assert.Equal(raw, TelemetryZstdStorage.Unpack(TelemetryZstdStorage.Deserialize(TelemetryZstdStorage.Serialize(fastest))));
        Assert.Equal(raw, TelemetryZstdStorage.Unpack(TelemetryZstdStorage.Deserialize(TelemetryZstdStorage.Serialize(optimal))));
    }

    [Fact]
    public void EmptyTraceHasAValidFrameAndRawDigest()
    {
        var archive = TelemetryZstdStorage.Pack([]);
        Assert.Equal(0, archive.RawLength);
        Assert.NotEmpty(archive.CompressedBytes);
        Assert.Empty(TelemetryZstdStorage.Unpack(TelemetryZstdStorage.Deserialize(TelemetryZstdStorage.Serialize(archive))));
    }

    private static AdmissionVerificationResult Fixture() => new(new SingPlusAdmissionProofV1
    {
        Root = "Fixture::Root", Profile = "KernelNoHeap", AssemblyDigest = "a", ReachableMethodCount = 1,
        ForbiddenOperationCount = 2, DependencyDigest = "b", RulesetDigest = "c", ProofDigest = "d"
    }, [new AdmissionViolation("B", "write", "x"), new AdmissionViolation("A", "read", "y")]);
}
