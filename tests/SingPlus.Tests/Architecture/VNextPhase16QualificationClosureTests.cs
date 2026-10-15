using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SingPlus.Runtime;

namespace SingPlus.Tests.Architecture;

public sealed class VNextPhase16QualificationClosureTests
{
    [Theory]
    [InlineData("hash")]
    [InlineData("bytes")]
    [InlineData("duplicate")]
    [InlineData("empty")]
    public void SourceTupleRejectsChangedBytesAndDuplicateOrEmptyArtifactSets(string mutation)
    {
        var bytes = new byte[] { 1, 2, 3 };
        var array = new JsonArray(new JsonObject
        {
            ["path"] = "src/fixture.cs", ["bytes"] = bytes.Length,
            ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
        });
        switch (mutation)
        {
            case "hash": array[0]!["sha256"] = new string('0', 64); break;
            case "bytes": array[0]!["bytes"] = 4; break;
            case "duplicate": array.Add(array[0]!.DeepClone()); break;
            case "empty": array.Clear(); break;
        }
        using var document = JsonDocument.Parse(array.ToJsonString());
        Assert.NotEmpty(VNextTraceabilityPolicy.ValidateArtifacts(document.RootElement, _ => bytes));
    }
}
