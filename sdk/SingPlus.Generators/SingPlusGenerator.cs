using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace SingPlus.Generators;

[Generator]
public sealed class SingPlusGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor OwnershipTypeDiagnostic = new(
        "SINGGEN001",
        "Invalid ownership payload type",
        "{0}",
        "SingPlus.Contracts",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor OwnershipAnnotationDiagnostic = new(
        "SINGGEN002",
        "Ownership annotation is required",
        "{0}",
        "SingPlus.Contracts",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor OwnershipCardinalityDiagnostic = new(
        "SINGGEN003",
        "Unsupported ownership payload cardinality",
        "{0}",
        "SingPlus.Contracts",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ReturnsOwnershipDiagnostic = new(
        "SINGGEN004",
        "Invalid returned ownership shape",
        "{0}",
        "SingPlus.Contracts",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var contracts = context.SyntaxProvider.ForAttributeWithMetadataName(
            "SingPlus.Sip.Sdk.SipContractAttribute",
            static (node, _) => node is Microsoft.CodeAnalysis.CSharp.Syntax.InterfaceDeclarationSyntax,
            static (ctx, _) => (INamedTypeSymbol)ctx.TargetSymbol).Collect();

        context.RegisterSourceOutput(contracts, static (output, symbols) =>
        {
            var unique = symbols.GroupBy(static s => s.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), StringComparer.Ordinal)
                .Select(static g => g.First())
                .OrderBy(static s => s.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), StringComparer.Ordinal);
            foreach (var symbol in unique) EmitContract(output, symbol);
        });
    }

    private static void EmitContract(SourceProductionContext output, INamedTypeSymbol contract)
    {
        if (!ValidateOwnershipContract(output, contract)) return;
        var model = ContractModel.Create(contract);
        var hint = Sanitize(contract.ToDisplayString()) + ".";
        output.AddSource(hint + "Protocol.g.cs", SourceText.From(GenerateProtocol(model), Encoding.UTF8));
        output.AddSource(hint + "Dispatcher.g.cs", SourceText.From(GenerateDispatcher(model), Encoding.UTF8));
        output.AddSource(hint + "Manifest.g.cs", SourceText.From(GenerateManifest(model), Encoding.UTF8));
        output.AddSource(hint + "Capabilities.g.cs", SourceText.From(GenerateCapabilities(model), Encoding.UTF8));
    }

    private static bool ValidateOwnershipContract(SourceProductionContext output, INamedTypeSymbol contract)
    {
        var valid = true;
        foreach (var method in contract.GetMembers().OfType<IMethodSymbol>().Where(static m => m.MethodKind == MethodKind.Ordinary))
        {
            var ownershipParameterCount = 0;
            foreach (var parameter in method.Parameters)
            {
                var consumes = HasAttribute(parameter, "ConsumesAttribute");
                var borrows = HasAttribute(parameter, "BorrowsAttribute");
                var kind = OwnershipKind(parameter.Type, unwrapAsync: false);

                if (consumes || borrows)
                {
                    ownershipParameterCount++;
                    if (consumes && borrows)
                    {
                        Report(output, OwnershipCardinalityDiagnostic, parameter, $"Parameter '{parameter.Name}' cannot be both Consumes and Borrows.");
                        valid = false;
                    }
                    if (kind == 0)
                    {
                        Report(output, OwnershipTypeDiagnostic, parameter, $"Ownership annotation on '{parameter.Name}' requires OwnedBuffer<T> or OwnedRegion<T>.");
                        valid = false;
                    }
                    if (parameter.RefKind != RefKind.None)
                    {
                        Report(output, OwnershipTypeDiagnostic, parameter, $"Ownership parameter '{parameter.Name}' cannot use ref, in, or out passing.");
                        valid = false;
                    }
                }
                else if (kind != 0)
                {
                    Report(output, OwnershipAnnotationDiagnostic, parameter, $"Ownership parameter '{parameter.Name}' must declare exactly one of Consumes or Borrows.");
                    valid = false;
                }
            }

            if (ownershipParameterCount > 1)
            {
                Report(output, OwnershipCardinalityDiagnostic, method, $"Message '{method.Name}' has {ownershipParameterCount} ownership-bearing parameters, but the current channel transport supports exactly one payload slot.");
                valid = false;
            }

            var returnsOwnership = HasAttribute(method, "ReturnsOwnershipAttribute");
            var returnKind = OwnershipKind(method.ReturnType, unwrapAsync: true);
            if (returnsOwnership && returnKind == 0)
            {
                Report(output, ReturnsOwnershipDiagnostic, method, $"Message '{method.Name}' declares ReturnsOwnership but does not return OwnedBuffer<T>/OwnedRegion<T> or Task/ValueTask wrapping one.");
                valid = false;
            }
            else if (!returnsOwnership && returnKind != 0)
            {
                Report(output, ReturnsOwnershipDiagnostic, method, $"Message '{method.Name}' returns an ownership-bearing payload and must declare ReturnsOwnership.");
                valid = false;
            }
        }
        return valid;
    }

    private static void Report(SourceProductionContext output, DiagnosticDescriptor descriptor, ISymbol symbol, string message)
    {
        output.ReportDiagnostic(Diagnostic.Create(descriptor, symbol.Locations.FirstOrDefault(), message));
    }

    private static bool HasAttribute(ISymbol symbol, string attributeName) =>
        symbol.GetAttributes().Any(a => a.AttributeClass?.Name == attributeName);

    private static int OwnershipKind(ITypeSymbol type, bool unwrapAsync)
    {
        if (unwrapAsync && type is INamedTypeSymbol asyncType && asyncType.TypeArguments.Length == 1 &&
            asyncType.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks" &&
            (asyncType.Name == "Task" || asyncType.Name == "ValueTask"))
        {
            type = asyncType.TypeArguments[0];
        }

        if (type is not INamedTypeSymbol named || named.TypeArguments.Length != 1 || named.ContainingNamespace.ToDisplayString() != "SingPlus.Sip") return 0;
        if (named.Name == "OwnedBuffer") return 1;
        if (named.Name == "OwnedRegion") return 2;
        return 0;
    }

    private static void AppendOwnershipKind(StringBuilder builder, int kind) =>
        builder.Append("(global::SingPlus.Contracts.OwnershipPayloadKind)").Append(kind.ToString(CultureInfo.InvariantCulture));

    private static string GenerateProtocol(ContractModel model)
    {
        var b = Header(model);
        b.Append("internal static class ").Append(model.TypeName).AppendLine("Protocol");
        b.AppendLine("{");
        b.Append("    public const string ContractName = ").Append(Literal(model.FullName)).AppendLine(";");
        b.Append("    public const string ContractDigest = ").Append(Literal(model.Digest)).AppendLine(";");
        b.Append("    public const string InitialState = ").Append(Literal(model.InitialState)).AppendLine(";");
        foreach (var message in model.Messages) b.Append("    public const uint Message_").Append(message.Name).Append(" = ").Append(message.Id.ToString(CultureInfo.InvariantCulture)).AppendLine("u;");
        b.AppendLine();
        b.AppendLine("    public static global::SingPlus.Contracts.ProtocolDefinitionV1 CreateDefinition() => new(");
        b.AppendLine("        ContractName,");
        b.AppendLine("        ContractDigest,");
        b.AppendLine("        InitialState,");
        b.Append("        new string[] { ").Append(string.Join(", ", model.TerminalStates.Select(Literal))).AppendLine(" },");
        b.AppendLine("        new global::SingPlus.Contracts.ProtocolMessageDescriptorV1[]");
        b.AppendLine("        {");
        foreach (var message in model.Messages)
        {
            b.Append("            new global::SingPlus.Contracts.ProtocolMessageDescriptorV1(").Append(message.Id.ToString(CultureInfo.InvariantCulture)).Append("u, ").Append(Literal(message.Name)).Append(", ");
            b.Append("new global::SingPlus.Contracts.CapabilityRequirementV1[] { ").Append(string.Join(", ", message.Capabilities.Select(CapabilityExpression))).Append(" }, ");
            b.Append("new string[] { ").Append(string.Join(", ", message.Consumes.Select(Literal))).Append(" }, ");
            b.Append("new string[] { ").Append(string.Join(", ", message.Borrows.Select(Literal))).Append(" }, ");
            b.Append(message.ReturnsOwnership ? "true" : "false").Append(", ");
            AppendOwnershipKind(b, message.OwnershipPayloadKind);
            b.Append(", ");
            AppendOwnershipKind(b, message.ReturnOwnershipPayloadKind);
            b.AppendLine("),");
        }
        b.AppendLine("        },");
        b.AppendLine("        new global::SingPlus.Contracts.ProtocolTransitionV1[]");
        b.AppendLine("        {");
        foreach (var transition in model.Transitions) b.Append("            new global::SingPlus.Contracts.ProtocolTransitionV1(").Append(transition.MessageId.ToString(CultureInfo.InvariantCulture)).Append("u, ").Append(Literal(transition.From)).Append(", ").Append(Literal(transition.To)).AppendLine("),");
        b.AppendLine("        });");
        b.AppendLine("}");
        return b.ToString();
    }

    private static string GenerateDispatcher(ContractModel model)
    {
        var b = Header(model);
        b.Append("internal sealed class ").Append(model.TypeName).Append("Dispatcher(").Append(model.QualifiedType).AppendLine(" implementation)");
        b.AppendLine("{");
        b.Append("    private readonly ").Append(model.QualifiedType).AppendLine(" _implementation = implementation;");
        foreach (var message in model.Messages)
        {
            b.Append("    public ").Append(message.ReturnType).Append(" Dispatch_").Append(message.Name).Append('(').Append(ParameterList(message.Parameters)).Append(") => _implementation.@").Append(message.Name).Append('(').Append(ArgumentList(message.Parameters)).AppendLine(");");
        }
        b.AppendLine("}");
        b.AppendLine();
        b.Append("internal interface I").Append(model.TypeName).AppendLine("ClientTransport");
        b.AppendLine("{");
        foreach (var message in model.Messages) b.Append("    ").Append(message.ReturnType).Append(" Send_").Append(message.Name).Append("(uint messageId").Append(message.Parameters.Count == 0 ? string.Empty : ", " + ParameterList(message.Parameters)).AppendLine(");");
        b.AppendLine("}");
        b.AppendLine();
        b.Append("internal sealed class ").Append(model.TypeName).Append("Client(I").Append(model.TypeName).Append("ClientTransport transport) : ").Append(model.QualifiedType).AppendLine();
        b.AppendLine("{");
        b.Append("    private readonly I").Append(model.TypeName).AppendLine("ClientTransport _transport = transport;");
        foreach (var message in model.Messages)
        {
            b.Append("    public ").Append(message.ReturnType).Append(" @").Append(message.Name).Append('(').Append(ParameterList(message.Parameters)).Append(") => _transport.Send_").Append(message.Name).Append('(').Append(message.Id.ToString(CultureInfo.InvariantCulture)).Append('u');
            if (message.Parameters.Count != 0) b.Append(", ").Append(ArgumentList(message.Parameters));
            b.AppendLine(");");
        }
        b.AppendLine("}");
        return b.ToString();
    }

    private static string GenerateManifest(ContractModel model)
    {
        var b = Header(model);
        b.Append("internal static class ").Append(model.TypeName).AppendLine("Manifest");
        b.AppendLine("{");
        b.Append("    public const string CanonicalManifest = ").Append(Literal(model.Canonical)).AppendLine(";");
        b.Append("    public const string ContractDigest = ").Append(Literal(model.Digest)).AppendLine(";");
        b.AppendLine("}");
        return b.ToString();
    }

    private static string GenerateCapabilities(ContractModel model)
    {
        var b = Header(model);
        b.Append("internal static class ").Append(model.TypeName).AppendLine("Capabilities");
        b.AppendLine("{");
        foreach (var message in model.Messages)
        {
            var canonical = string.Join(";", message.Capabilities.Select(static c => c.Kind + ":" + c.ResourceId + ":" + c.Rights));
            b.Append("    public const string ").Append(message.Name).Append(" = ").Append(Literal(canonical)).AppendLine(";");
            b.Append("    public const string ").Append(message.Name).Append("_Ownership = ").Append(Literal("Consumes=" + string.Join(",", message.Consumes) + ";Borrows=" + string.Join(",", message.Borrows) + ";InputKind=" + message.OwnershipPayloadKind.ToString(CultureInfo.InvariantCulture) + ";Returns=" + (message.ReturnsOwnership ? "1" : "0") + ";ReturnKind=" + message.ReturnOwnershipPayloadKind.ToString(CultureInfo.InvariantCulture))).AppendLine(";");
        }
        b.AppendLine("}");
        return b.ToString();
    }

    private static StringBuilder Header(ContractModel model)
    {
        var b = new StringBuilder("#nullable enable\n");
        if (!string.IsNullOrEmpty(model.Namespace)) b.Append("namespace ").Append(model.Namespace).AppendLine(";\n");
        return b;
    }

    private static string ParameterList(IReadOnlyList<ParameterModel> parameters) => string.Join(", ", parameters.Select(static p => p.Type + " @" + p.Name));
    private static string ArgumentList(IReadOnlyList<ParameterModel> parameters) => string.Join(", ", parameters.Select(static p => "@" + p.Name));
    private static string CapabilityExpression(CapabilityModel c) => "new global::SingPlus.Contracts.CapabilityRequirementV1((global::SingPlus.Contracts.ResourceKind)" + c.Kind.ToString(CultureInfo.InvariantCulture) + ", " + Literal(c.ResourceId) + ", (global::SingPlus.Contracts.CapabilityRights)" + c.Rights.ToString(CultureInfo.InvariantCulture) + ")";
    private static string Literal(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
    private static string Sanitize(string value) => new string(value.Select(static c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    private sealed class ContractModel
    {
        public string Namespace { get; private set; } = string.Empty;
        public string TypeName { get; private set; } = string.Empty;
        public string QualifiedType { get; private set; } = string.Empty;
        public string FullName { get; private set; } = string.Empty;
        public string InitialState { get; private set; } = string.Empty;
        public IReadOnlyList<string> TerminalStates { get; private set; } = Array.Empty<string>();
        public IReadOnlyList<MessageModel> Messages { get; private set; } = Array.Empty<MessageModel>();
        public IReadOnlyList<TransitionModel> Transitions { get; private set; } = Array.Empty<TransitionModel>();
        public string Canonical { get; private set; } = string.Empty;
        public string Digest { get; private set; } = string.Empty;

        public static ContractModel Create(INamedTypeSymbol symbol)
        {
            var initial = AttributeString(symbol, "InitialStateAttribute") ?? "Initial";
            var terminals = symbol.GetAttributes().Where(static a => a.AttributeClass?.Name == "TerminalStateAttribute").Select(static a => (string?)a.ConstructorArguments[0].Value ?? string.Empty).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
            var messages = symbol.GetMembers().OfType<IMethodSymbol>().Where(static m => m.MethodKind == MethodKind.Ordinary).Select(m => MessageModel.Create(m, initial)).OrderBy(static m => m.Id).ThenBy(static m => m.Name, StringComparer.Ordinal).ToArray();
            var transitions = messages.SelectMany(static m => m.Transitions).OrderBy(static t => t.From, StringComparer.Ordinal).ThenBy(static t => t.MessageId).ThenBy(static t => t.To, StringComparer.Ordinal).ToArray();
            var fullName = symbol.ToDisplayString();
            var canonical = BuildCanonical(fullName, initial, terminals, messages, transitions);
            return new ContractModel
            {
                Namespace = symbol.ContainingNamespace.IsGlobalNamespace ? string.Empty : symbol.ContainingNamespace.ToDisplayString(),
                TypeName = symbol.Name,
                QualifiedType = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                FullName = fullName,
                InitialState = initial,
                TerminalStates = terminals,
                Messages = messages,
                Transitions = transitions,
                Canonical = canonical,
                Digest = Sha256(canonical)
            };
        }

        private static string BuildCanonical(string fullName, string initial, IReadOnlyList<string> terminals, IReadOnlyList<MessageModel> messages, IReadOnlyList<TransitionModel> transitions)
        {
            var lines = new List<string> { "contract=" + fullName, "initial=" + initial };
            lines.AddRange(terminals.Select(static x => "terminal=" + x));
            foreach (var m in messages)
            {
                lines.Add("message=" + m.Id.ToString(CultureInfo.InvariantCulture) + "|" + m.Name + "|" + m.ReturnType + "|" + string.Join(",", m.Parameters.Select(static p => p.Type + " " + p.Name)));
                lines.Add("cap=" + m.Id.ToString(CultureInfo.InvariantCulture) + "|" + string.Join(";", m.Capabilities.Select(static c => c.Kind + ":" + c.ResourceId + ":" + c.Rights)));
                lines.Add("ownership=" + m.Id.ToString(CultureInfo.InvariantCulture) + "|" + string.Join(",", m.Consumes) + "|" + string.Join(",", m.Borrows) + "|" + m.OwnershipPayloadKind.ToString(CultureInfo.InvariantCulture) + "|" + (m.ReturnsOwnership ? "1" : "0") + "|" + m.ReturnOwnershipPayloadKind.ToString(CultureInfo.InvariantCulture));
            }
            lines.AddRange(transitions.Select(static t => "transition=" + t.MessageId.ToString(CultureInfo.InvariantCulture) + "|" + t.From + "|" + t.To));
            return string.Join("\n", lines);
        }
    }

    private sealed class MessageModel
    {
        public uint Id { get; private set; }
        public string Name { get; private set; } = string.Empty;
        public string ReturnType { get; private set; } = string.Empty;
        public IReadOnlyList<ParameterModel> Parameters { get; private set; } = Array.Empty<ParameterModel>();
        public IReadOnlyList<CapabilityModel> Capabilities { get; private set; } = Array.Empty<CapabilityModel>();
        public IReadOnlyList<string> Consumes { get; private set; } = Array.Empty<string>();
        public IReadOnlyList<string> Borrows { get; private set; } = Array.Empty<string>();
        public bool ReturnsOwnership { get; private set; }
        public int OwnershipPayloadKind { get; private set; }
        public int ReturnOwnershipPayloadKind { get; private set; }
        public IReadOnlyList<TransitionModel> Transitions { get; private set; } = Array.Empty<TransitionModel>();

        public static MessageModel Create(IMethodSymbol method, string initial)
        {
            var messageAttr = method.GetAttributes().FirstOrDefault(static a => a.AttributeClass?.Name == "MessageAttribute");
            var id = messageAttr is null ? 0u : Convert.ToUInt32(messageAttr.ConstructorArguments[0].Value, CultureInfo.InvariantCulture);
            var capabilities = method.GetAttributes().Where(static a => a.AttributeClass?.Name == "RequiresCapabilityAttribute").Select(static a => new CapabilityModel
            {
                Kind = Convert.ToInt32(a.ConstructorArguments[0].Value, CultureInfo.InvariantCulture),
                ResourceId = (string?)a.ConstructorArguments[1].Value ?? string.Empty,
                Rights = Convert.ToInt32(a.ConstructorArguments[2].Value, CultureInfo.InvariantCulture)
            }).OrderBy(static c => c.Kind).ThenBy(static c => c.ResourceId, StringComparer.Ordinal).ThenBy(static c => c.Rights).ToArray();
            var parameters = method.Parameters.Select(static p => new ParameterModel { Name = p.Name, Type = p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) }).ToArray();
            var consumes = method.Parameters.Where(static p => p.GetAttributes().Any(static a => a.AttributeClass?.Name == "ConsumesAttribute")).Select(static p => p.Name).OrderBy(static p => p, StringComparer.Ordinal).ToArray();
            var borrows = method.Parameters.Where(static p => p.GetAttributes().Any(static a => a.AttributeClass?.Name == "BorrowsAttribute")).Select(static p => p.Name).OrderBy(static p => p, StringComparer.Ordinal).ToArray();
            var ownershipParameter = method.Parameters.FirstOrDefault(static p => p.GetAttributes().Any(static a => a.AttributeClass?.Name is "ConsumesAttribute" or "BorrowsAttribute"));
            var transitionAttributes = method.GetAttributes().Where(static a => a.AttributeClass?.Name == "TransitionAttribute").ToArray();
            var transitions = transitionAttributes.Length == 0 ? new[] { new TransitionModel { MessageId = id, From = initial, To = initial } } : transitionAttributes.Select(a => new TransitionModel { MessageId = id, From = (string?)a.ConstructorArguments[0].Value ?? initial, To = (string?)a.ConstructorArguments[1].Value ?? initial }).ToArray();
            return new MessageModel
            {
                Id = id,
                Name = method.Name,
                ReturnType = method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                Parameters = parameters,
                Capabilities = capabilities,
                Consumes = consumes,
                Borrows = borrows,
                ReturnsOwnership = method.GetAttributes().Any(static a => a.AttributeClass?.Name == "ReturnsOwnershipAttribute"),
                OwnershipPayloadKind = ownershipParameter is null ? 0 : OwnershipKind(ownershipParameter.Type, unwrapAsync: false),
                ReturnOwnershipPayloadKind = OwnershipKind(method.ReturnType, unwrapAsync: true),
                Transitions = transitions
            };
        }
    }

    private sealed class ParameterModel { public string Name { get; set; } = string.Empty; public string Type { get; set; } = string.Empty; }
    private sealed class CapabilityModel { public int Kind { get; set; } public string ResourceId { get; set; } = string.Empty; public int Rights { get; set; } }
    private sealed class TransitionModel { public uint MessageId { get; set; } public string From { get; set; } = string.Empty; public string To { get; set; } = string.Empty; }

    private static string? AttributeString(INamedTypeSymbol symbol, string attributeName)
    {
        var attribute = symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == attributeName);
        return attribute is null || attribute.ConstructorArguments.Length == 0 ? null : attribute.ConstructorArguments[0].Value as string;
    }

    private static string Sha256(string text)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
        var b = new StringBuilder(hash.Length * 2);
        foreach (var value in hash) b.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        return b.ToString();
    }
}
