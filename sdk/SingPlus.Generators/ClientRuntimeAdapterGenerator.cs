using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace SingPlus.Generators;

[Generator]
public sealed class ClientRuntimeAdapterGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var contracts = context.SyntaxProvider.ForAttributeWithMetadataName(
            "SingPlus.Sip.Sdk.SipContractAttribute",
            static (node, _) => node is Microsoft.CodeAnalysis.CSharp.Syntax.InterfaceDeclarationSyntax,
            static (ctx, _) => (INamedTypeSymbol)ctx.TargetSymbol).Collect();

        context.RegisterSourceOutput(contracts, static (output, symbols) =>
        {
            var unique = symbols
                .GroupBy(static symbol => symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), StringComparer.Ordinal)
                .Select(static group => group.First())
                .OrderBy(static symbol => symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), StringComparer.Ordinal);

            foreach (var contract in unique)
                Emit(output, contract);
        });
    }

    private static void Emit(SourceProductionContext output, INamedTypeSymbol contract)
    {
        var methods = contract.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(static method => method.MethodKind == MethodKind.Ordinary)
            .OrderBy(static method => method.Name, StringComparer.Ordinal)
            .ThenBy(static method => method.Parameters.Length)
            .ToArray();

        if (methods.Any(static method =>
                method.Parameters.Length > 1 ||
                method.Parameters.Any(static parameter => parameter.RefKind != RefKind.None)))
        {
            return;
        }

        var source = Generate(contract, methods);
        output.AddSource(
            Sanitize(contract.ToDisplayString()) + ".ClientRuntimeAdapter.g.cs",
            SourceText.From(source, Encoding.UTF8));
    }

    private static string Generate(
        INamedTypeSymbol contract,
        IReadOnlyList<IMethodSymbol> methods)
    {
        var builder = new StringBuilder("#nullable enable\n");
        if (!contract.ContainingNamespace.IsGlobalNamespace)
            builder.Append("namespace ").Append(contract.ContainingNamespace.ToDisplayString()).AppendLine(";\n");

        var contractName = contract.Name;
        var transportName = contractName + "RuntimeClientTransport";
        var generatedTransportInterface = "I" + contractName + "ClientTransport";
        var generatedClient = contractName + "Client";
        var factoryName = contractName + "RuntimeClient";

        builder.Append("internal sealed class ").Append(transportName)
            .Append(" : ").Append(generatedTransportInterface).AppendLine();
        builder.AppendLine("{");
        builder.AppendLine("    private readonly global::SingPlus.Contracts.ISipClientRuntimeTransport _runtime;");
        builder.AppendLine();
        builder.Append("    public ").Append(transportName)
            .AppendLine("(global::SingPlus.Contracts.ISipClientRuntimeTransport runtime)");
        builder.AppendLine("    {");
        builder.AppendLine("        _runtime = runtime ?? throw new global::System.ArgumentNullException(nameof(runtime));");
        builder.AppendLine("    }");

        foreach (var method in methods)
        {
            builder.AppendLine();
            AppendMethod(builder, method);
        }

        builder.AppendLine();
        AppendHelpers(builder);
        builder.AppendLine("}");
        builder.AppendLine();

        builder.Append("internal static class ").Append(factoryName).AppendLine();
        builder.AppendLine("{");
        builder.Append("    public static ")
            .Append(contract.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
            .Append(" Create(global::SingPlus.Contracts.ISipClientRuntimeTransport runtime) => new ")
            .Append(generatedClient).Append("(new ").Append(transportName).AppendLine("(runtime));");
        builder.AppendLine("}");

        return builder.ToString();
    }

    private static void AppendMethod(StringBuilder builder, IMethodSymbol method)
    {
        var returnType = method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        builder.Append("    public ").Append(returnType).Append(" Send_").Append(method.Name)
            .Append("(uint messageId");
        if (method.Parameters.Length != 0)
            builder.Append(", ").Append(Parameter(method.Parameters[0]));
        builder.Append(')');

        var payload = method.Parameters.Length == 0 ? "null" : "@" + method.Parameters[0].Name;
        if (method.ReturnsVoid)
        {
            builder.Append(" => EnsureVoid(_runtime.Invoke(messageId, ").Append(payload).AppendLine("), messageId);");
            return;
        }

        if (TryAsync(method.ReturnType, out var asyncKind, out var responseType))
        {
            switch (asyncKind)
            {
                case AsyncKind.Task:
                    builder.Append(" => AwaitTask(_runtime.InvokeAsync(messageId, ").Append(payload).AppendLine("), messageId);");
                    return;
                case AsyncKind.TaskOfT:
                    builder.Append(" => AwaitTask<").Append(Display(responseType!)).Append(">(_runtime.InvokeAsync(messageId, ")
                        .Append(payload).AppendLine("), messageId);");
                    return;
                case AsyncKind.ValueTask:
                    builder.Append(" => AwaitValueTask(_runtime.InvokeAsync(messageId, ").Append(payload).AppendLine("), messageId);");
                    return;
                case AsyncKind.ValueTaskOfT:
                    builder.Append(" => AwaitValueTask<").Append(Display(responseType!)).Append(">(_runtime.InvokeAsync(messageId, ")
                        .Append(payload).AppendLine("), messageId);");
                    return;
            }
        }

        builder.Append(" => Decode<").Append(returnType).Append(">(_runtime.Invoke(messageId, ")
            .Append(payload).AppendLine("), messageId);");
    }

    private static void AppendHelpers(StringBuilder builder)
    {
        builder.AppendLine("    private static object? EnsurePublished(global::SingPlus.Contracts.ResponseEnvelope response, uint messageId)");
        builder.AppendLine("    {");
        builder.AppendLine("        if (response.MessageId != messageId)");
        builder.AppendLine("            throw new global::System.InvalidOperationException($\"Response message {response.MessageId} does not match request message {messageId}.\");");
        builder.AppendLine("        if (response.Status != global::SingPlus.Contracts.ResponsePublicationStatus.Published)");
        builder.AppendLine("            throw new global::System.OperationCanceledException($\"SIP request {response.RequestSequence} was cancelled before response publication.\");");
        builder.AppendLine("        return response.Payload;");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    private static void EnsureVoid(global::SingPlus.Contracts.ResponseEnvelope response, uint messageId)");
        builder.AppendLine("    {");
        builder.AppendLine("        if (EnsurePublished(response, messageId) is not null)");
        builder.AppendLine("            throw new global::System.InvalidOperationException(\"Void SIP response unexpectedly carried a payload.\");");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    private static T Decode<T>(global::SingPlus.Contracts.ResponseEnvelope response, uint messageId)");
        builder.AppendLine("    {");
        builder.AppendLine("        var payload = EnsurePublished(response, messageId);");
        builder.AppendLine("        if (payload is T typed) return typed;");
        builder.AppendLine("        throw new global::System.InvalidOperationException($\"Published SIP response payload cannot be decoded as {typeof(T).FullName}.\");");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    private static async global::System.Threading.Tasks.Task AwaitTask(global::System.Threading.Tasks.ValueTask<global::SingPlus.Contracts.ResponseEnvelope> response, uint messageId) =>");
        builder.AppendLine("        EnsureVoid(await response.ConfigureAwait(false), messageId);");
        builder.AppendLine();
        builder.AppendLine("    private static async global::System.Threading.Tasks.Task<T> AwaitTask<T>(global::System.Threading.Tasks.ValueTask<global::SingPlus.Contracts.ResponseEnvelope> response, uint messageId) =>");
        builder.AppendLine("        Decode<T>(await response.ConfigureAwait(false), messageId);");
        builder.AppendLine();
        builder.AppendLine("    private static async global::System.Threading.Tasks.ValueTask AwaitValueTask(global::System.Threading.Tasks.ValueTask<global::SingPlus.Contracts.ResponseEnvelope> response, uint messageId) =>");
        builder.AppendLine("        EnsureVoid(await response.ConfigureAwait(false), messageId);");
        builder.AppendLine();
        builder.AppendLine("    private static async global::System.Threading.Tasks.ValueTask<T> AwaitValueTask<T>(global::System.Threading.Tasks.ValueTask<global::SingPlus.Contracts.ResponseEnvelope> response, uint messageId) =>");
        builder.AppendLine("        Decode<T>(await response.ConfigureAwait(false), messageId);");
    }

    private static bool TryAsync(
        ITypeSymbol type,
        out AsyncKind kind,
        out ITypeSymbol? responseType)
    {
        kind = AsyncKind.None;
        responseType = null;
        if (type is not INamedTypeSymbol named ||
            named.ContainingNamespace.ToDisplayString() != "System.Threading.Tasks")
        {
            return false;
        }

        if (named.Name == "Task")
        {
            kind = named.TypeArguments.Length == 0 ? AsyncKind.Task : AsyncKind.TaskOfT;
            responseType = named.TypeArguments.Length == 0 ? null : named.TypeArguments[0];
            return true;
        }

        if (named.Name == "ValueTask")
        {
            kind = named.TypeArguments.Length == 0 ? AsyncKind.ValueTask : AsyncKind.ValueTaskOfT;
            responseType = named.TypeArguments.Length == 0 ? null : named.TypeArguments[0];
            return true;
        }

        return false;
    }

    private static string Parameter(IParameterSymbol parameter) =>
        Display(parameter.Type) + " @" + parameter.Name;

    private static string Display(ITypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    private static string Sanitize(string value) =>
        new(value.Select(static c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    private enum AsyncKind
    {
        None = 0,
        Task,
        TaskOfT,
        ValueTask,
        ValueTaskOfT
    }
}
