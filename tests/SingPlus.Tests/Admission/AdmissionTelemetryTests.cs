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
        string directory = Path.Combine(AppContext.BaseDirectory, "telemetry-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string assembly = Path.Combine(directory, "Corpus.dll");
            string source = rejected ? "public static class Fixture { public static object Root() => new object(); }"
                : "public static class Fixture { public static int Root() => 1; }";
            CSharpCompilation compilation = CSharpCompilation.Create("Corpus", [CSharpSyntaxTree.ParseText(source)],
                AnalyzerTests.PlatformReferences(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            Assert.True(compilation.Emit(assembly).Success);
            (int ExitCode, string Error) baseline = await Invoke("baseline.json", false);
            (int ExitCode, string Error) failed = await Invoke("failed.json", true);
            Assert.Equal(rejected ? 2 : 0, baseline.ExitCode);
            Assert.Equal(baseline.ExitCode, failed.ExitCode);
            Assert.Equal(File.ReadAllBytes(Path.Combine(directory, "baseline.json")), File.ReadAllBytes(Path.Combine(directory, "failed.json")));
            Assert.Contains("Telemetry output failed", failed.Error, StringComparison.Ordinal);
            if (rejected) Assert.Contains(baseline.Error, failed.Error, StringComparison.Ordinal);

            byte[] inputBytes = File.ReadAllBytes(assembly);
            byte[] canonicalBytes = File.ReadAllBytes(Path.Combine(directory, "baseline.json"));
            foreach (string? destination in new[] { assembly, Path.Combine(directory, "baseline.json") })
            {
                (int ExitCode, string Error) collision = await Invoke("baseline.json", true, destination);
                Assert.Equal(baseline.ExitCode, collision.ExitCode);
                Assert.Contains("destinations are disabled", collision.Error, StringComparison.Ordinal);
                Assert.Equal(inputBytes, File.ReadAllBytes(assembly));
                Assert.Equal(canonicalBytes, File.ReadAllBytes(Path.Combine(directory, "baseline.json")));
            }
            (int ExitCode, string Error) archiveCollision = await Invoke("baseline.json", true, Path.Combine(directory, "trace.ndjson"), assembly);
            Assert.Equal(baseline.ExitCode, archiveCollision.ExitCode);
            Assert.Equal(inputBytes, File.ReadAllBytes(assembly));
            Assert.False(File.Exists(Path.Combine(directory, "trace.ndjson")));

            string existingTrace = Path.Combine(directory, "existing.ndjson");
            string existingArchive = Path.Combine(directory, "existing.zstd");
            byte[] sentinel = [1, 2, 3, 4];
            File.WriteAllBytes(existingTrace, sentinel);
            File.WriteAllBytes(existingArchive, sentinel);
            (int ExitCode, string Error) traceRefusal = await Invoke("baseline.json", true, existingTrace);
            Assert.Equal(baseline.ExitCode, traceRefusal.ExitCode);
            Assert.Contains("Telemetry output failed", traceRefusal.Error, StringComparison.Ordinal);
            Assert.Equal(sentinel, File.ReadAllBytes(existingTrace));
            (int ExitCode, string Error) archiveRefusal = await Invoke("baseline.json", true, Path.Combine(directory, "new.ndjson"), existingArchive);
            Assert.Equal(baseline.ExitCode, archiveRefusal.ExitCode);
            Assert.Contains("Telemetry output failed", archiveRefusal.Error, StringComparison.Ordinal);
            Assert.Equal(sentinel, File.ReadAllBytes(existingArchive));
            Assert.Equal(canonicalBytes, File.ReadAllBytes(Path.Combine(directory, "baseline.json")));
            string freshTrace = Path.Combine(directory, "fresh.ndjson");
            string freshArchive = Path.Combine(directory, "fresh.zstd");
            (int ExitCode, string Error) success = await Invoke("baseline.json", true, freshTrace, freshArchive);
            Assert.Equal(baseline.ExitCode, success.ExitCode);
            Assert.DoesNotContain("Telemetry output failed", success.Error, StringComparison.Ordinal);
            Assert.Equal(File.ReadAllBytes(freshTrace),
                TelemetryZstdStorage.Unpack(TelemetryZstdStorage.Deserialize(File.ReadAllBytes(freshArchive))));
            Assert.Equal(canonicalBytes, File.ReadAllBytes(Path.Combine(directory, "baseline.json")));

            async Task<(int ExitCode, string Error)> Invoke(string proof, bool telemetry, string? destination = null, string? archive = null)
            {
                ProcessStartInfo start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                foreach (string? argument in new[] { "exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "SingPlus.Tests.runtimeconfig.json"),
                    typeof(AdmissionTelemetry).Assembly.Location, "verify", "--assembly", assembly, "--root", "Fixture::Root",
                    "--profile", "KernelNoHeap", "--proof", Path.Combine(directory, proof) }) start.ArgumentList.Add(argument);
                if (telemetry) { start.ArgumentList.Add("--telemetry"); start.ArgumentList.Add(destination ?? directory); }
                if (archive is not null) { start.ArgumentList.Add("--telemetry-zstd"); start.ArgumentList.Add(archive); }
                using Process process = Process.Start(start)!;
                Task<string> output = process.StandardOutput.ReadToEndAsync();
                Task<string> error = process.StandardError.ReadToEndAsync();
                using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await process.WaitForExitAsync(timeout.Token); }
                finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                await output;
                return (process.ExitCode, await error);
            }
        }
        finally
        {
            string basePath = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(directory).StartsWith(basePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Telemetry fixture cleanup escaped test output.");
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task NdjsonIsOrderedAndCannotChangeSemanticProof()
    {
        AdmissionVerificationResult result = Fixture();
        byte[] canonicalBefore = result.Proof.SerializeCanonical(result.Violations);
        IReadOnlyList<AdmissionTelemetryEvent> events = AdmissionTelemetry.Observe(result);
        await using MemoryStream first = new MemoryStream();
        await using MemoryStream second = new MemoryStream();
        await AdmissionTelemetry.WriteNdjsonAsync(first, events);
        await AdmissionTelemetry.WriteNdjsonAsync(second, events);
        Assert.Equal(first.ToArray(), second.ToArray());
        Assert.Equal(canonicalBefore, result.Proof.SerializeCanonical(result.Violations));
        string[] lines = Encoding.UTF8.GetString(first.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        using JsonDocument record = JsonDocument.Parse(lines[1]);
        Assert.Equal("A", record.RootElement.GetProperty("Method").GetString());
        Assert.Equal(1, record.RootElement.GetProperty("Sequence").GetInt32());
    }

    [Fact]
    public void ActivityTraceCorrelatesOnlyObservation()
    {
        AdmissionVerificationResult result = Fixture();
        byte[] canonical = result.Proof.SerializeCanonical(result.Violations);
        using Activity activity = new Activity("qualification").Start();
        Assert.Equal(activity.TraceId.ToString(), AdmissionTelemetry.Observe(result)[0].TraceId);
        Assert.Equal(canonical, result.Proof.SerializeCanonical(result.Violations));
    }

    [Fact]
    public void OptInObservationHasTraceEvenWithoutAnActivitySourceListener()
    {
        AdmissionVerificationResult result = Fixture();
        byte[] canonical = result.Proof.SerializeCanonical(result.Violations);
        using (Activity activity = AdmissionTelemetry.StartObservation())
        {
            Assert.NotEqual(default, activity.TraceId);
            Assert.All(AdmissionTelemetry.Observe(result), item => Assert.Equal(activity.TraceId.ToString(), item.TraceId));
        }
        Assert.Equal(canonical, result.Proof.SerializeCanonical(result.Violations));
    }

    [Fact]
    public async Task NdjsonStreamingHasFixedFieldOrderAndKeepsOutputOpen()
    {
        await using MemoryStream output = new MemoryStream();
        AdmissionTelemetryEvent[] events = new[]
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
        byte[] raw = Encoding.UTF8.GetBytes("{\"Kind\":\"VerificationCompleted\"}\n");
        TelemetryZstdArchive archive = TelemetryZstdStorage.Pack(raw);
        byte[] stored = TelemetryZstdStorage.Serialize(archive);
        TelemetryZstdArchive reloaded = TelemetryZstdStorage.Deserialize(stored);
        Assert.Equal(Convert.ToHexString(global::System.Security.Cryptography.SHA256.HashData(raw)).ToLowerInvariant(), archive.RawDigest);
        Assert.NotEqual(Convert.ToHexString(global::System.Security.Cryptography.SHA256.HashData(archive.CompressedBytes)).ToLowerInvariant(), archive.RawDigest);
        Assert.Equal("SINGZST1", Encoding.ASCII.GetString(stored, 0, 8));
        Assert.Equal(stored, TelemetryZstdStorage.Serialize(TelemetryZstdStorage.Pack(raw)));
        Assert.Equal(raw, TelemetryZstdStorage.Unpack(reloaded));
        Assert.Throws<InvalidDataException>(() => TelemetryZstdStorage.Unpack(archive with { RawDigest = new string('0', 64) }));
        byte[] badMagic = (byte[])stored.Clone();
        badMagic[0] ^= 1;
        Assert.Throws<InvalidDataException>(() => TelemetryZstdStorage.Deserialize(badMagic));
        byte[] badDigest = (byte[])stored.Clone();
        badDigest[12] ^= 1;
        Assert.Throws<InvalidDataException>(() => TelemetryZstdStorage.Unpack(TelemetryZstdStorage.Deserialize(badDigest)));
        Assert.Throws<InvalidDataException>(() => TelemetryZstdStorage.Unpack(reloaded with { RawLength = 1 }));
        TelemetryZstdArchive trailing = reloaded with { CompressedBytes = [.. reloaded.CompressedBytes, 0xff, 0x01] };
        Assert.Throws<InvalidDataException>(() => TelemetryZstdStorage.Unpack(trailing));
        TelemetryZstdArchive secondFrame = TelemetryZstdStorage.Pack(Encoding.UTF8.GetBytes("hidden\n"));
        TelemetryZstdArchive concatenated = reloaded with { CompressedBytes = [.. reloaded.CompressedBytes, .. secondFrame.CompressedBytes] };
        Assert.Throws<InvalidDataException>(() => TelemetryZstdStorage.Unpack(concatenated));
    }

    [Fact]
    public void CompressionLevelCannotChangeRawTraceDigest()
    {
        byte[] raw = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("{\"Event\":\"Observed\"}\n", 1000)));
        TelemetryZstdArchive fastest = TelemetryZstdStorage.Pack(raw, global::System.IO.Compression.CompressionLevel.Fastest);
        TelemetryZstdArchive optimal = TelemetryZstdStorage.Pack(raw, global::System.IO.Compression.CompressionLevel.Optimal);
        Assert.Equal(fastest.RawDigest, optimal.RawDigest);
        Assert.Equal(raw, TelemetryZstdStorage.Unpack(TelemetryZstdStorage.Deserialize(TelemetryZstdStorage.Serialize(fastest))));
        Assert.Equal(raw, TelemetryZstdStorage.Unpack(TelemetryZstdStorage.Deserialize(TelemetryZstdStorage.Serialize(optimal))));
    }

    [Fact]
    public void EmptyTraceHasAValidFrameAndRawDigest()
    {
        TelemetryZstdArchive archive = TelemetryZstdStorage.Pack([]);
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
