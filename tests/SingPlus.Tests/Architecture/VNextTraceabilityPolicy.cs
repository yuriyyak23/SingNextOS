using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SingPlus.Tests.Architecture;

// Qualification metadata validation only. Passing this policy is not phase closure.
internal static class VNextTraceabilityPolicy
{
    internal const string Baseline = "6227ea7cf258ef6ffce52001d4d2ffee07355b35";
    internal const string Roadmap = "docs/SingNextOS-vNext-refactoring-roadmap-new/";
    internal const string Lane = "eng/qualify-vnext.ps1";
    private static readonly string[] Fields =
    [
        "id", "phase", "owner", "implementation", "tests", "ciLane", "evidence", "evidenceSha256",
        "tuple", "tupleSha256", "evidenceKind", "contour", "claim", "exclusions",
    ];

    internal static IReadOnlyList<string> Validate(string json, string markdown,
        IReadOnlySet<string> executableTests, Func<string, byte[]> read)
    {
        var errors = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            ExactProperties(root, ["schemaVersion", "normativeBaseline", "status", "rows"], errors);
            if (root.GetProperty("schemaVersion").GetInt32() != 2) errors.Add("Unknown schema version.");
            if (root.GetProperty("normativeBaseline").GetString() != Baseline) errors.Add("Baseline mismatch.");
            if (root.GetProperty("status").GetString() != "PartialCoverage") errors.Add("Coverage index cannot claim closure.");
            var rows = root.GetProperty("rows").EnumerateArray().ToArray();
            if (rows.Length != 28) errors.Add("Exactly 28 invariants are required.");
            for (var index = 0; index < rows.Length; index++)
            {
                var row = rows[index];
                ExactProperties(row, Fields, errors);
                var id = Text(row, "id");
                if (id != $"VNX-{index + 1:000}") errors.Add($"Unordered, missing or duplicate invariant: {id}.");
                foreach (var field in Fields)
                    if (string.IsNullOrWhiteSpace(Text(row, field))) errors.Add($"{id}: empty {field}.");
                var phases = Text(row, "phase").Split('/');
                if (phases.Any(phase => !Enumerable.Range(0, 18).Select(value => $"P{value:00}").Contains(phase)))
                    errors.Add($"{id}: unknown phase.");
                foreach (var path in Split(row, "implementation"))
                    _ = Read(path, read); // Require exact, repository-relative production paths.
                foreach (var test in Split(row, "tests"))
                    if (!executableTests.Contains(test)) errors.Add($"{id}: missing executable test '{test}'.");
                if (Text(row, "ciLane") != Lane) errors.Add($"{id}: unknown CI lane.");
                _ = Read(Text(row, "ciLane"), read);
                var evidence = Read(Roadmap + Text(row, "evidence"), read);
                var tupleBytes = Read(Roadmap + Text(row, "tuple"), read);
                CheckHash(evidence, Text(row, "evidenceSha256"), id + ": evidence hash", errors);
                CheckHash(tupleBytes, Text(row, "tupleSha256"), id + ": tuple hash", errors);
                using var tuple = JsonDocument.Parse(tupleBytes);
                if (tuple.RootElement.GetProperty("normativeBaseline").GetString() != Baseline)
                    errors.Add($"{id}: tuple baseline mismatch.");
                if (!phases.Contains(tuple.RootElement.GetProperty("phase").GetString()))
                    errors.Add($"{id}: tuple phase mismatch.");

                var expectedClaim = Text(row, "evidenceKind") switch
                {
                    "ModelTests" => "ModelOnly",
                    "StaticChecks" => "StaticAdmission",
                    "DirectOwnerTests" => "RuntimeEnforced",
                    "HostAdapterTests" => "ExecutableAdapter",
                    _ => null,
                };
                if (expectedClaim is null || Text(row, "claim") != expectedClaim)
                    errors.Add($"{id}: claim is not supported by its evidence kind.");
                if (Text(row, "contour") != "Windows-x64/JIT/host-only; ComputeTime-or-static-boundary; rollout-OFF")
                    errors.Add($"{id}: unqualified contour or proof transfer.");
            }
            if (Normalize(markdown) != Normalize(Render(root))) errors.Add("Markdown and JSON traceability differ.");
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or
                                      IOException or InvalidDataException or ArgumentException or FormatException or OverflowException)
        {
            errors.Add($"Invalid qualification input: {error.Message}");
        }
        return errors;
    }

    internal static string Render(JsonElement root)
    {
        var result = new StringBuilder("# vNext traceability — current coverage index\n\n");
        result.Append("Status: PartialCoverage. These rows identify reviewed test contours; they do not close entire invariants or phases. All rollout gates remain OFF. P16 remains open.\n\n");
        result.Append("Generated projection of `VNEXT_TRACEABILITY.json`; the closure lane compares every field and referenced evidence/tuple hash.\n\n");
        foreach (var row in root.GetProperty("rows").EnumerateArray())
        {
            result.Append("## ").Append(Text(row, "id")).Append("\n\n");
            foreach (var field in Fields.Skip(1))
                result.Append("- ").Append(field).Append(": ").Append(Text(row, field)).Append('\n');
            result.Append('\n');
        }
        return result.ToString();
    }

    internal static IReadOnlyList<string> ValidateArtifacts(JsonElement artifacts, Func<string, byte[]> read)
    {
        var errors = new List<string>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var artifact in artifacts.EnumerateArray())
            {
                ExactProperties(artifact, ["path", "bytes", "sha256"], errors);
                var path = Text(artifact, "path");
                if (!paths.Add(path)) errors.Add($"Duplicate artifact: {path}.");
                var bytes = Read(path, read);
                if (bytes.LongLength != artifact.GetProperty("bytes").GetInt64()) errors.Add($"Artifact size mismatch: {path}.");
                CheckHash(bytes, Text(artifact, "sha256"), path, errors);
            }
            if (paths.Count == 0) errors.Add("Qualification tuple has no artifacts.");
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or
                                      IOException or InvalidDataException or ArgumentException or FormatException or OverflowException)
        {
            errors.Add($"Invalid artifact: {error.Message}");
        }
        return errors;
    }

    private static string Text(JsonElement row, string field) =>
        row.GetProperty(field).GetString() ?? throw new InvalidDataException($"Null {field}.");

    private static string[] Split(JsonElement row, string field)
    {
        var values = Text(row, field).Split("; ", StringSplitOptions.None);
        if (values.Any(string.IsNullOrWhiteSpace) || values.Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new InvalidDataException($"Empty or duplicate {field} reference.");
        return values;
    }

    private static byte[] Read(string path, Func<string, byte[]> read)
    {
        if (path.Contains('\\') || path.Contains(':') || path.StartsWith('/') ||
            path.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException($"Noncanonical artifact path: {path}.");
        return read(path);
    }

    private static void ExactProperties(JsonElement value, string[] expected, List<string> errors)
    {
        var properties = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (!properties.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal)))
            errors.Add("Missing, duplicate or unknown JSON field.");
    }

    private static void CheckHash(byte[] bytes, string expected, string label, List<string> errors)
    {
        var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(expected, actual, StringComparison.Ordinal)) errors.Add(label + " mismatch.");
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
}
