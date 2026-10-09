using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace SingPlus.Tests.Architecture;

public sealed class V6DevEvidencePolicyTests
{
    [Fact]
    public void OrdinaryInvocationRecordsAttributionWithoutExecutingCommandsOrTouchingInputs()
    {
        using var run = new Invocation();
        var before = run.Snapshot();
        // An executable sentinel command must remain inert, even when its supplied result is passed.
        run.Input["commands"]![0]!["command"] = $"dotnet; Freeze-V6Baseline.ps1; New-Item '{run.Root}/executed'";
        var result = run.Execute(digest: true);
        Assert.Equal(0, result.Exit);
        Assert.Equal(before, run.Snapshot());
        Assert.False(File.Exists(Path.Combine(run.Root, "executed")));
        var audit = run.Audit();
        Assert.Equal("dirty input retained by caller", audit["sourceAttribution"]!["value"]!.GetValue<string>());
        Assert.Equal("supplied fixture", audit["sourceAttribution"]!["provenance"]!.GetValue<string>());
        Assert.Equal("DevOnly", audit["kind"]!.GetValue<string>());
        Assert.Equal("OFF", audit["gates"]!.GetValue<string>());
        Assert.Equal("FutureGated", audit["physical"]!.GetValue<string>());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(run.Source))).ToLowerInvariant(),
            audit["sourceDigests"]![0]!["sha256"]!.GetValue<string>());
        Assert.Contains("Dev-only", File.ReadAllText(Path.Combine(run.Output, "report.md")));
    }

    [Theory]
    [InlineData("failed", 7, 1, "failed")]
    [InlineData("failed", null, 1, "failed")]
    [InlineData("pending", null, 3, "pending")]
    [InlineData("unavailable", null, 3, "pending")]
    public void FailedOrMissingResultsCannotBecomeGreen(string result, int? exit, int expected, string status)
    {
        using var run = new Invocation();
        run.Input["commands"]![0]!["result"] = result;
        run.Input["commands"]![0]!["exitCode"] = exit;
        Assert.Equal(expected, run.Execute().Exit);
        Assert.Equal(status, run.Audit()["status"]!.GetValue<string>());
        Assert.Null(run.Audit()["commands"]![0]!["counts"]);
        if (exit is null)
            Assert.Contains("exit=unavailable", File.ReadAllText(Path.Combine(run.Output, "report.md")));
    }

    [Theory]
    [InlineData("ProductionQualified")]
    [InlineData("Physical")]
    [InlineData("GuaranteedReservation")]
    [InlineData("RuntimeEnforced")]
    public void PromotedClaimRefusedBeforePublication(string claim)
    {
        using var run = new Invocation();
        run.Input["claimCeiling"] = claim;
        AssertRefused(run);
    }

    [Theory]
    [InlineData("missing-exit")]
    [InlineData("unknown-input")]
    [InlineData("unknown-command")]
    [InlineData("null-green-exit")]
    [InlineData("failed-counts")]
    [InlineData("unobserved-counts")]
    [InlineData("missing-attribution")]
    public void InvalidMandatorySemanticsFailClosed(string fault)
    {
        using var run = new Invocation();
        var command = run.Input["commands"]![0]!.AsObject();
        switch (fault)
        {
            case "missing-exit": command.Remove("exitCode"); break;
            case "unknown-input": run.Input["mandatoryAuthority"] = "unknown"; break;
            case "unknown-command": command["permission"] = "unknown"; break;
            case "null-green-exit": command["exitCode"] = null; break;
            case "failed-counts": command["counts"] = JsonNode.Parse("{\"passed\":0,\"failed\":1,\"skipped\":0,\"total\":1}"); break;
            case "unobserved-counts":
                command["result"] = "unavailable";
                command["exitCode"] = null;
                command["counts"] = JsonNode.Parse("{\"passed\":0,\"failed\":0,\"skipped\":0,\"total\":0}");
                break;
            case "missing-attribution": run.Input.Remove("sourceAttribution"); break;
        }
        AssertRefused(run);
    }

    [Fact]
    public void ExistingFailedArtifactAndUserFilesAreNeverOverwritten()
    {
        using var run = new Invocation();
        run.Input["commands"]![0]!["result"] = "failed";
        run.Input["commands"]![0]!["exitCode"] = 9;
        Assert.Equal(1, run.Execute().Exit);
        var audit = File.ReadAllBytes(Path.Combine(run.Output, "audit.json"));
        File.WriteAllText(Path.Combine(run.Output, "user.txt"), "history");
        run.Input["commands"]![0]!["result"] = "passed";
        run.Input["commands"]![0]!["exitCode"] = 0;
        Assert.Equal(2, run.Execute().Exit);
        Assert.Equal(audit, File.ReadAllBytes(Path.Combine(run.Output, "audit.json")));
        Assert.Equal("history", File.ReadAllText(Path.Combine(run.Output, "user.txt")));
    }

    [Theory]
    [InlineData("src/new-output")]
    [InlineData("artifacts/Boot/new-output")]
    [InlineData("artifacts/../escape")]
    public void SourceBootAndEscapingOutputAreRefused(string path)
    {
        using var run = new Invocation();
        run.Output = Path.Combine(run.Root, path);
        AssertRefused(run);
    }

    [Fact]
    public void DocumentationDigestOptInIsRefusedWithoutMutation()
    {
        using var run = new Invocation();
        Assert.Equal(2, run.Execute(digestPath: "docs/policy.md").Exit);
        Assert.False(Directory.Exists(run.Output));
    }

    [Fact]
    public async Task ConcurrentPublicationKeepsExactlyOneCompleteArtifact()
    {
        using var run = new Invocation();
        run.WriteInput();
        var results = await Task.WhenAll(Task.Run(() => run.Invoke()), Task.Run(() => run.Invoke()));
        Assert.Equal(new[] { 0, 2 }, results.Select(r => r.Exit).Order().ToArray());
        Assert.Equal("passed", run.Audit()["status"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(run.Output, "report.md")));
    }

    [Theory]
    [InlineData("claimCeiling")]
    [InlineData("result")]
    public void DuplicateJsonKeysCannotHideEarlierClaimsOrResults(string key)
    {
        using var run = new Invocation();
        var json = run.Input.ToJsonString();
        json = key == "claimCeiling"
            ? json.Replace("\"claimCeiling\":\"DevOnly\"", "\"claimCeiling\":\"ProductionQualified\",\"claimCeiling\":\"DevOnly\"")
            : json.Replace("\"result\":\"passed\"", "\"result\":\"failed\",\"result\":\"passed\"");
        Assert.NotEqual(run.Input.ToJsonString(), json);
        File.WriteAllText(Path.Combine(run.Root, "input.json"), json);
        var result = run.Invoke();
        Assert.Equal(2, result.Exit);
        Assert.Contains("Duplicate JSON", result.Text);
        Assert.False(Directory.Exists(run.Output));
    }

    [Theory]
    [InlineData("pending", "failed", 1, "failed")]
    [InlineData("failed", "pending", 1, "failed")]
    [InlineData("passed", "unavailable", 3, "pending")]
    [InlineData("unavailable", "passed", 3, "pending")]
    public void AggregateResultNeverHidesFailureOrUnavailability(string first, string second, int expected, string status)
    {
        using var run = new Invocation();
        var commands = run.Input["commands"]!.AsArray();
        var copy = commands[0]!.DeepClone();
        copy["id"] = "second";
        commands.Add(copy);
        foreach (var (command, result) in new[] { (commands[0]!, first), (commands[1]!, second) })
        {
            command["result"] = result;
            command["exitCode"] = result == "passed" ? 0 : result == "failed" ? 7 : (int?)null;
        }
        Assert.Equal(expected, run.Execute().Exit);
        Assert.Equal(status, run.Audit()["status"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("../outside.cs")]
    [InlineData("src/../docs/policy.md")]
    public void SourceAttributionPathsCannotEscapeScope(string path)
    {
        using var run = new Invocation();
        run.Input["affectedFiles"] = new JsonArray(path);
        AssertRefused(run);
    }

    private static void AssertRefused(Invocation run)
    {
        var result = run.Execute();
        Assert.Equal(2, result.Exit);
        Assert.Contains("refused", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(run.Output));
    }

    private sealed class Invocation : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "v6-dev-evidence-" + Guid.NewGuid().ToString("N"));
        public string Output { get; set; }
        public string Source => Path.Combine(Root, "src", "fixture.cs");
        public JsonObject Input { get; } = JsonNode.Parse("""
            {"slice":"W1-B","contour":"controlled dev fixture","claimCeiling":"DevOnly",
             "affectedFiles":["src/fixture.cs"],
             "commands":[{"id":"fixture","command":"supplied only","result":"passed","exitCode":0,"provenance":"supplied fixture"}],
             "sourceAttribution":{"value":"dirty input retained by caller","provenance":"supplied fixture"},
             "tuple":{"value":"unavailable","provenance":"not measured in fixture"}}
            """)!.AsObject();

        public Invocation()
        {
            Output = Path.Combine(Root, "artifacts", "run");
            foreach (var path in new[] { "src/fixture.cs", "docs/policy.md", "packages.lock.json", "MANIFEST.md", "eng/v6/Freeze-V6Baseline.ps1" })
            {
                var full = Path.Combine(Root, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, "controlled input: " + path);
            }
        }

        public string Snapshot() => string.Join("\n", new[] { "src/fixture.cs", "docs/policy.md", "packages.lock.json", "MANIFEST.md", "eng/v6/Freeze-V6Baseline.ps1" }
            .Select(path => path + ":" + File.ReadAllText(Path.Combine(Root, path))));
        public void WriteInput() => File.WriteAllText(Path.Combine(Root, "input.json"), Input.ToJsonString());
        public (int Exit, string Text) Execute(bool digest = false, string? digestPath = null)
        {
            WriteInput();
            return Invoke(digest ? "src/fixture.cs" : digestPath);
        }

        private static string ResolvePowerShellHost()
        {
            var executable = OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh";
            var searchDirectories = (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(path => path.Trim('"')).ToArray();
            string? Find(string candidate)
            {
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                if (Path.IsPathRooted(candidate) || candidate.Contains(Path.DirectorySeparatorChar) ||
                    candidate.Contains(Path.AltDirectorySeparatorChar)) return null;
                return searchDirectories.Select(path => Path.Combine(path, candidate))
                    .FirstOrDefault(File.Exists);
            }

            var configured = Environment.GetEnvironmentVariable("V6_DEV_EVIDENCE_PWSH");
            if (!string.IsNullOrWhiteSpace(configured))
                return Find(configured) ?? throw new InvalidOperationException(
                    $"V6_DEV_EVIDENCE_PWSH does not resolve to an existing PowerShell 7 host: {configured}");

            var onPath = Find(executable);
            if (onPath is not null) return onPath;
            var candidates = new List<string>();
            if (OperatingSystem.IsWindows())
            {
                var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                candidates.Add(Path.Combine(programFiles, "PowerShell", "7", executable));
                candidates.Add(Path.Combine(programFiles, "PowerShell", "7-preview", executable));
                candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "PowerShell", "7", executable));
            }
            else
            {
                candidates.Add("/usr/bin/pwsh");
                candidates.Add("/usr/local/bin/pwsh");
            }
            // The desktop runtime may provide PowerShell without modifying the IDE's PATH.
            // This optional installed host is used only by the test helper; no installation is performed.
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cache", "codex-runtimes", "codex-primary-runtime", "dependencies", "native", "powershell", executable));
            return candidates.FirstOrDefault(File.Exists) ?? throw new InvalidOperationException(
                "PowerShell 7 host was not found. Set V6_DEV_EVIDENCE_PWSH to its executable path. " +
                "Windows PowerShell 5 is not compatible with this collector.");
        }

        public (int Exit, string Text) Invoke(string? digestPath = null)
        {
            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "eng", "v6", "Record-V6DevEvidence.ps1")))
                repository = repository.Parent;
            Assert.NotNull(repository);
            var start = new ProcessStartInfo(ResolvePowerShellHost())
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-File", Path.Combine(repository!.FullName, "eng", "v6", "Record-V6DevEvidence.ps1"),
                         "-RepositoryRoot", Root, "-EvidencePath", Path.Combine(Root, "input.json"), "-OutputDirectory", Output })
                start.ArgumentList.Add(arg);
            if (digestPath is not null) { start.ArgumentList.Add("-SourceDigestFiles"); start.ArgumentList.Add(digestPath); }
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                // This helper is owned by this invocation; never terminate another session's process.
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                Assert.Fail("Controlled PowerShell invocation timed out.");
            }
            return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
        }

        public JsonObject Audit() => JsonNode.Parse(File.ReadAllText(Path.Combine(Output, "audit.json")))!.AsObject();
        public void Dispose()
        {
            var absolute = Path.GetFullPath(Root);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Assert.True(absolute.StartsWith(temp, StringComparison.OrdinalIgnoreCase));
            Assert.StartsWith("v6-dev-evidence-", Path.GetFileName(absolute));
            Directory.Delete(absolute, recursive: true);
        }
    }
}
