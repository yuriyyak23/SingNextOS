using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace SingPlus.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SingPlusAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor ManagedAllocation = Rule("SING1001", "Managed allocation is forbidden", "'{0}' performs a managed allocation under KernelNoHeap", "Profile/NoHeap");
    private static readonly DiagnosticDescriptor CapturingClosure = Rule("SING1002", "Capturing closure is forbidden", "Capturing lambdas or local functions are forbidden under KernelNoHeap", "Profile/NoHeap");
    private static readonly DiagnosticDescriptor DynamicCode = Rule("SING1003", "dynamic is forbidden", "dynamic dispatch is forbidden under KernelNoHeap", "Profile/NoHeap");
    private static readonly DiagnosticDescriptor ForbiddenApi = Rule("SING1004", "Runtime/host API is forbidden", "API '{0}' is forbidden under KernelNoHeap", "Profile/NoHeap");
    private static readonly DiagnosticDescriptor Boxing = Rule("SING1005", "Boxing is forbidden", "Boxing conversion to '{0}' is forbidden under KernelNoHeap", "Profile/NoHeap");
    private static readonly DiagnosticDescriptor BorrowEscape = Rule("SING2001", "Borrow may escape its owner", "BorrowedSpan values must not be returned from a method", "Ownership");
    private static readonly DiagnosticDescriptor UseAfterMove = Rule("SING2002", "Ownership token used after move", "'{0}' is referenced after Move() consumed its ownership token", "Ownership");
    private static readonly DiagnosticDescriptor ContractMessage = Rule("SING3001", "Invalid SIP contract message", "SIP contract method '{0}' must declare a unique [Message(id)]", "IPC contracts");
    private static readonly DiagnosticDescriptor SelfMint = Rule("SING4001", "Capability minting is authority-only", "SIP/driver code cannot call capability authority method '{0}'", "Capabilities");
    private static readonly DiagnosticDescriptor NondeterministicArtifact = Rule("SING5001", "Nondeterministic input is forbidden", "API '{0}' is forbidden in deterministic Sing+ artifacts", "Deterministic manifests");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [ManagedAllocation, CapturingClosure, DynamicCode, ForbiddenApi, Boxing, BorrowEscape, UseAfterMove, ContractMessage, SelfMint, NondeterministicArtifact];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(AnalyzeAllocation, OperationKind.ObjectCreation, OperationKind.AnonymousObjectCreation, OperationKind.ArrayCreation);
        context.RegisterOperationAction(AnalyzeConversion, OperationKind.Conversion);
        context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
        context.RegisterSyntaxNodeAction(AnalyzeLambda, SyntaxKind.SimpleLambdaExpression, SyntaxKind.ParenthesizedLambdaExpression, SyntaxKind.AnonymousMethodExpression);
        context.RegisterSyntaxNodeAction(AnalyzeDynamic, SyntaxKind.IdentifierName);
        context.RegisterSyntaxNodeAction(AnalyzeBorrowReturn, SyntaxKind.ReturnStatement);
        context.RegisterSyntaxNodeAction(AnalyzeMoveUse, SyntaxKind.InvocationExpression);
        context.RegisterSymbolAction(AnalyzeContract, SymbolKind.NamedType);
    }

    private static DiagnosticDescriptor Rule(string id, string title, string message, string category) => new(id, title, message, category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static bool IsKernelNoHeap(AnalyzerOptions options) => options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue("build_property.SingPlusMemoryProfile", out var value) && string.Equals(value, "KernelNoHeap", StringComparison.Ordinal);

    private static string Profile(AnalyzerOptions options)
    {
        options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue("build_property.SingPlusProfile", out var value);
        return value ?? string.Empty;
    }

    private static void AnalyzeAllocation(OperationAnalysisContext context)
    {
        if (!IsKernelNoHeap(context.Options)) return;
        if (context.Operation is IObjectCreationOperation objectCreation && objectCreation.Type?.IsValueType == true) return;
        context.ReportDiagnostic(Diagnostic.Create(ManagedAllocation, context.Operation.Syntax.GetLocation(), context.Operation.Syntax.ToString()));
    }

    private static void AnalyzeConversion(OperationAnalysisContext context)
    {
        if (!IsKernelNoHeap(context.Options)) return;
        var conversion = (IConversionOperation)context.Operation;
        if (conversion.Conversion.IsBoxing) context.ReportDiagnostic(Diagnostic.Create(Boxing, conversion.Syntax.GetLocation(), conversion.Type?.ToDisplayString() ?? "object"));
    }

    private static void AnalyzeLambda(SyntaxNodeAnalysisContext context)
    {
        if (!IsKernelNoHeap(context.Options)) return;
        var flow = context.SemanticModel.AnalyzeDataFlow(context.Node);
        if (flow.Succeeded && flow.CapturedInside.Length != 0) context.ReportDiagnostic(Diagnostic.Create(CapturingClosure, context.Node.GetLocation()));
    }

    private static void AnalyzeDynamic(SyntaxNodeAnalysisContext context)
    {
        if (!IsKernelNoHeap(context.Options)) return;
        var identifier = (IdentifierNameSyntax)context.Node;
        if (identifier.Identifier.ValueText == "dynamic" && context.SemanticModel.GetTypeInfo(identifier, context.CancellationToken).Type?.TypeKind == TypeKind.Dynamic)
            context.ReportDiagnostic(Diagnostic.Create(DynamicCode, identifier.GetLocation()));
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;
        var containingType = method.ContainingType?.ToDisplayString() ?? string.Empty;
        var containingNamespace = method.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        var api = containingType + "." + method.Name;
        if (IsKernelNoHeap(context.Options) && IsForbiddenKernelApi(containingType, containingNamespace, method.Name)) context.ReportDiagnostic(Diagnostic.Create(ForbiddenApi, invocation.Syntax.GetLocation(), api));
        var profile = Profile(context.Options);
        if ((profile == "Sip" || profile == "Driver") && containingType.EndsWith("CapabilityAuthority", StringComparison.Ordinal) && (method.Name == "Mint" || method.Name == "Delegate")) context.ReportDiagnostic(Diagnostic.Create(SelfMint, invocation.Syntax.GetLocation(), method.Name));
        if (IsNondeterministic(containingType, method.Name)) context.ReportDiagnostic(Diagnostic.Create(NondeterministicArtifact, invocation.Syntax.GetLocation(), api));
    }

    private static bool IsForbiddenKernelApi(string type, string ns, string method) => type == "System.Console" || type == "System.Environment" || type == "System.GC" || type == "System.Activator" || type == "System.Threading.ThreadPool" || type == "System.Threading.Tasks.Task" || type == "System.Diagnostics.Process" || (type == "System.Delegate" && method == "CreateDelegate") || ns.StartsWith("System.IO", StringComparison.Ordinal) || ns.StartsWith("System.Net", StringComparison.Ordinal) || ns.StartsWith("System.Reflection", StringComparison.Ordinal) || ns.StartsWith("System.Linq.Expressions", StringComparison.Ordinal);

    private static bool IsNondeterministic(string type, string method) => (type == "System.Guid" && method == "NewGuid") || type == "System.Random" || (type == "System.DateTime" && (method == "get_Now" || method == "get_UtcNow")) || (type == "System.Environment" && (method == "get_MachineName" || method == "get_CurrentDirectory"));

    private static void AnalyzeBorrowReturn(SyntaxNodeAnalysisContext context)
    {
        var statement = (ReturnStatementSyntax)context.Node;
        if (statement.Expression is null) return;
        var type = context.SemanticModel.GetTypeInfo(statement.Expression, context.CancellationToken).Type;
        if (type?.Name == "BorrowedSpan") context.ReportDiagnostic(Diagnostic.Create(BorrowEscape, statement.GetLocation()));
    }

    private static void AnalyzeMoveUse(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Move", Expression: IdentifierNameSyntax receiver }) return;
        var block = invocation.FirstAncestorOrSelf<BlockSyntax>();
        if (block is null) return;
        var laterUse = block.DescendantNodes().OfType<IdentifierNameSyntax>().FirstOrDefault(i => i.SpanStart > invocation.Span.End && i.Identifier.ValueText == receiver.Identifier.ValueText);
        if (laterUse is not null) context.ReportDiagnostic(Diagnostic.Create(UseAfterMove, laterUse.GetLocation(), receiver.Identifier.ValueText));
    }

    private static void AnalyzeContract(SymbolAnalysisContext context)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (type.TypeKind != TypeKind.Interface || !type.GetAttributes().Any(static a => a.AttributeClass?.Name == "SipContractAttribute")) return;
        var ids = new HashSet<int>();
        foreach (var method in type.GetMembers().OfType<IMethodSymbol>().Where(static m => m.MethodKind == MethodKind.Ordinary))
        {
            var message = method.GetAttributes().FirstOrDefault(static a => a.AttributeClass?.Name == "MessageAttribute");
            var valid = message is not null && message.ConstructorArguments.Length == 1 && message.ConstructorArguments[0].Value is int id && id > 0 && ids.Add(id);
            if (!valid) context.ReportDiagnostic(Diagnostic.Create(ContractMessage, method.Locations.FirstOrDefault(), method.Name));
        }
    }
}
