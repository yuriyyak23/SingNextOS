using Microsoft.CodeAnalysis;

namespace SingPlus.Generators;

[Generator]
public sealed class SingPlusGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(static output => output.AddSource(
            "SingPlus.GeneratedAssemblyInfo.g.cs",
            "namespace SingPlus.Generated;\n\ninternal static class AssemblyInfo\n{\n    internal const string Name = \"Sing+\";\n}\n"));
    }
}
