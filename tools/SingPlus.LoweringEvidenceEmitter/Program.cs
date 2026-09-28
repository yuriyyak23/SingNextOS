using System.Text.Json;
using SingPlus.LoweringEvidence;

if (args.Length != 6)
{
    Console.Error.WriteLine(
        "usage: SingPlus.LoweringEvidenceEmitter <facts.json> <input.ir> <output> <toolchain> <producer> <metadata>");
    return 2;
}

try
{
    var result = LoweringEvidenceEmitter.EmitFiles(
        args[0], args[1], args[2], args[3], args[4], args[5]);
    Console.WriteLine(JsonSerializer.Serialize(result));
    return 0;
}
catch (Exception exception) when (exception is ArgumentException or IOException or
    UnauthorizedAccessException or JsonException or NotSupportedException or OverflowException)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
