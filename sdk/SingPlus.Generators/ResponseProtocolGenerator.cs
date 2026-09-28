using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace SingPlus.Generators;

[Generator]
public sealed class ResponseProtocolGenerator : IIncrementalGenerator
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

            foreach (var contract in unique) Emit(output, contract);
        });
    }

    private static void Emit(SourceProductionContext output, INamedTypeSymbol contract)
    {
        var responses = new List<ResponseModel>();
        foreach (var method in contract.GetMembers().OfType<IMethodSymbol>()
                     .Where(static method => method.MethodKind == MethodKind.Ordinary)
                     .OrderBy(MessageId)
                     .ThenBy(static method => method.Name, StringComparer.Ordinal))
        {
            if (!TryCreateResponse(method, out var response)) return;
            responses.Add(response);
        }

        var canonical = string.Join("\n", responses.Select(static response => response.Canonical));
        var digest = Sha256(canonical);
        output.AddSource(
            Sanitize(contract.ToDisplayString()) + ".ResponseProtocol.g.cs",
            SourceText.From(Generate(contract, responses, digest), Encoding.UTF8));
    }

    private static bool TryCreateResponse(IMethodSymbol method, out ResponseModel response)
    {
        var type = UnwrapAsync(method.ReturnType, out var noValueAsync);
        if (method.ReturnsVoid || noValueAsync)
        {
            response = new ResponseModel(MessageId(method), method.Name, 0, string.Empty, 0, 0);
            return true;
        }

        var ownershipKind = OwnershipKind(type);
        if (ownershipKind != 0)
        {
            response = new ResponseModel(
                MessageId(method),
                method.Name,
                4,
                ownershipKind == 1 ? "SingPlus.Sip.OwnedBuffer" : "SingPlus.Sip.OwnedRegion",
                0,
                ownershipKind);
            return true;
        }

        var boundedAttribute = BoundedPayloadAttribute(type);
        var implementsBounded = ImplementsBoundedPayload(type);
        if (boundedAttribute is not null || implementsBounded)
        {
            if (boundedAttribute is null || !implementsBounded || type is not INamedTypeSymbol boundedType || !HasStableRuntimeTypeName(boundedType))
            {
                response = default;
                return false;
            }

            var maxBytes = BoundedPayloadMaxBytes(boundedAttribute);
            if (maxBytes <= 0)
            {
                response = default;
                return false;
            }

            response = new ResponseModel(MessageId(method), method.Name, 3, RuntimeTypeName(boundedType), maxBytes, 0);
            return true;
        }

        if (IsPrimitivePayload(type))
        {
            response = new ResponseModel(MessageId(method), method.Name, 1, RuntimeTypeName((INamedTypeSymbol)type), 0, 0);
            return true;
        }

        if (type.TypeKind == TypeKind.Enum && type is INamedTypeSymbol enumType && HasStableRuntimeTypeName(enumType))
        {
            response = new ResponseModel(MessageId(method), method.Name, 2, RuntimeTypeName(enumType), 0, 0);
            return true;
        }

        response = default;
        return false;
    }

    private static ITypeSymbol UnwrapAsync(ITypeSymbol type, out bool noValueAsync)
    {
        noValueAsync = false;
        if (type is not INamedTypeSymbol named ||
            named.ContainingNamespace.ToDisplayString() != "System.Threading.Tasks" ||
            (named.Name != "Task" && named.Name != "ValueTask"))
        {
            return type;
        }

        if (named.TypeArguments.Length == 0)
        {
            noValueAsync = true;
            return type;
        }

        return named.TypeArguments[0];
    }

    private static uint MessageId(IMethodSymbol method)
    {
        var attribute = method.GetAttributes().FirstOrDefault(static attribute => attribute.AttributeClass?.Name == "MessageAttribute");
        return attribute is null ? 0u : Convert.ToUInt32(attribute.ConstructorArguments[0].Value, CultureInfo.InvariantCulture);
    }

    private static int OwnershipKind(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named ||
            named.TypeArguments.Length != 1 ||
            named.ContainingNamespace.ToDisplayString() != "SingPlus.Sip")
        {
            return 0;
        }

        if (named.Name == "OwnedBuffer") return 1;
        if (named.Name == "OwnedRegion") return 2;
        return 0;
    }

    private static bool IsPrimitivePayload(ITypeSymbol type) => type.SpecialType is
        SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_Int16 or SpecialType.System_UInt16 or
        SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64 or
        SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Boolean or SpecialType.System_Char or
        SpecialType.System_Decimal;

    private static AttributeData? BoundedPayloadAttribute(ITypeSymbol type) =>
        type.GetAttributes().FirstOrDefault(static attribute =>
            attribute.AttributeClass?.Name == "BoundedPayloadAttribute" &&
            attribute.AttributeClass.ContainingNamespace.ToDisplayString() == "SingPlus.Sip.Sdk");

    private static int BoundedPayloadMaxBytes(AttributeData attribute) =>
        attribute.ConstructorArguments.Length == 0 || attribute.ConstructorArguments[0].Value is null
            ? 0
            : Convert.ToInt32(attribute.ConstructorArguments[0].Value, CultureInfo.InvariantCulture);

    private static bool ImplementsBoundedPayload(ITypeSymbol type) =>
        type.AllInterfaces.Any(static iface =>
            iface.Name == "IBoundedPayload" &&
            iface.ContainingNamespace.ToDisplayString() == "SingPlus.Contracts");

    private static bool HasStableRuntimeTypeName(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            if (current.Arity != 0) return false;
        }
        return true;
    }

    private static string RuntimeTypeName(INamedTypeSymbol type)
    {
        if (type.SpecialType != SpecialType.None)
            return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty);

        var names = new Stack<string>();
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
            names.Push(current.MetadataName);
        var namespaceName = type.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : type.ContainingNamespace.ToDisplayString();
        return (string.IsNullOrEmpty(namespaceName) ? string.Empty : namespaceName + ".") + string.Join("+", names);
    }

    private static string Generate(
        INamedTypeSymbol contract,
        IReadOnlyList<ResponseModel> responses,
        string digest)
    {
        var builder = new StringBuilder("#nullable enable\n");
        if (!contract.ContainingNamespace.IsGlobalNamespace)
            builder.Append("namespace ").Append(contract.ContainingNamespace.ToDisplayString()).AppendLine(";\n");

        builder.Append("internal static class ").Append(contract.Name).AppendLine("ResponseProtocol");
        builder.AppendLine("{");
        builder.AppendLine("    public static readonly global::SingPlus.Contracts.ResponseProtocolDefinitionV1 Definition =");
        builder.AppendLine("        new global::SingPlus.Contracts.ResponseProtocolDefinitionV1(");
        builder.Append("            ").Append(Literal(contract.ToDisplayString())).AppendLine(",");
        builder.Append("            ").Append(Literal(digest)).AppendLine(",");
        builder.AppendLine("            new global::SingPlus.Contracts.ResponseMessageDescriptorV1[]");
        builder.AppendLine("            {");
        foreach (var response in responses)
        {
            builder.Append("                new global::SingPlus.Contracts.ResponseMessageDescriptorV1(")
                .Append(response.MessageId.ToString(CultureInfo.InvariantCulture)).Append("u, ")
                .Append(Literal(response.Name)).Append(", ");
            AppendPayload(builder, response);
            builder.AppendLine("),");
        }
        builder.AppendLine("            });");
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static void AppendPayload(StringBuilder builder, ResponseModel response)
    {
        if (response.Kind == 0)
        {
            builder.Append("global::SingPlus.Contracts.ResponsePayloadDescriptorV1.None");
            return;
        }

        builder.Append("new global::SingPlus.Contracts.ResponsePayloadDescriptorV1(")
            .Append("(global::SingPlus.Contracts.ResponsePayloadKind)")
            .Append(response.Kind.ToString(CultureInfo.InvariantCulture)).Append(", ")
            .Append(Literal(response.TypeName)).Append(", ")
            .Append(response.MaxBytes.ToString(CultureInfo.InvariantCulture)).Append(", ")
            .Append("(global::SingPlus.Contracts.OwnershipPayloadKind)")
            .Append(response.OwnershipKind.ToString(CultureInfo.InvariantCulture)).Append(')');
    }

    private static string Literal(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";

    private static string Sanitize(string value) =>
        new string(value.Select(static c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    private static string Sha256(string text)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
        var builder = new StringBuilder(hash.Length * 2);
        foreach (var value in hash)
            builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    private readonly record struct ResponseModel(
        uint MessageId,
        string Name,
        int Kind,
        string TypeName,
        int MaxBytes,
        int OwnershipKind)
    {
        public string Canonical => $"response={MessageId}|{Name}|{Kind}|{TypeName}|{MaxBytes}|{OwnershipKind}";
    }
}
