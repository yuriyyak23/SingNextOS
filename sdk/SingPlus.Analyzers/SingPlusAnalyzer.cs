using System;
using System.Collections.Generic;
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
    private static readonly DiagnosticDescriptor CapturingClosure = Rule("SING1002", "Capturing closure is forbidden", "Capturing lambdas or closures are forbidden under KernelNoHeap", "Profile/NoHeap");
    private static readonly DiagnosticDescriptor DynamicCode = Rule("SING1003", "dynamic is forbidden", "dynamic dispatch is forbidden under KernelNoHeap", "Profile/NoHeap");
    private static readonly DiagnosticDescriptor ForbiddenApi = Rule("SING1004", "Runtime/host API is forbidden", "API '{0}' is forbidden under KernelNoHeap", "Profile/NoHeap");
    private static readonly DiagnosticDescriptor Boxing = Rule("SING1005", "Boxing is forbidden", "Boxing conversion to '{0}' is forbidden under KernelNoHeap", "Profile/NoHeap");
    private static readonly DiagnosticDescriptor PointerDereference = Rule("SING1006", "Pointer dereference is forbidden", "Pointer dereference is forbidden under KernelNoHeap", "Profile/NoHeap");
    private static readonly DiagnosticDescriptor FunctionPointerInvocation = Rule("SING1007", "Function pointer invocation is forbidden", "Function pointer invocation is forbidden under KernelNoHeap", "Profile/NoHeap");
    private static readonly DiagnosticDescriptor AddressFormation = MemoryRule("SING1008", "Address formation", "Address formation '{0}' is forbidden in {1}");
    private static readonly DiagnosticDescriptor UnmanagedRead = MemoryRule("SING1009", "Unmanaged read", "Unmanaged read is forbidden in {0}");
    private static readonly DiagnosticDescriptor UnmanagedWrite = MemoryRule("SING1010", "Unmanaged write", "Unmanaged write is forbidden in {0}");
    private static readonly DiagnosticDescriptor UnsafeCall = MemoryRule("SING1011", "Unsafe call propagation", "Call to unsafe method '{0}' is forbidden in {1}");
    private static readonly DiagnosticDescriptor ExplicitLayout = MemoryRule("SING1012", "Explicit-layout access", "Access to explicit-layout field '{0}' is forbidden in {1}");
    private static readonly DiagnosticDescriptor InteropBoundary = MemoryRule("SING1013", "Unmanaged interop boundary", "Unmanaged interop boundary '{0}' is forbidden in {1}");
    private static readonly DiagnosticDescriptor PointerIndex = MemoryRule("SING1014", "Pointer indexing", "Pointer indexing is forbidden in {0}");
    private static readonly DiagnosticDescriptor FunctionPointerAudit = MemoryRule("SING1015", "Function pointer invocation", "Function pointer invocation is forbidden in {0}");
    private static readonly DiagnosticDescriptor PointerBoundary = MemoryRule("SING1016", "Pointer-bearing call boundary", "Call to pointer-bearing method '{0}' requires memory boundary audit in {1}");
    private static readonly DiagnosticDescriptor FrameworkMemoryBoundary = MemoryRule("SING1017", "Framework memory boundary", "Framework memory API '{0}' is forbidden in {1}; memory audit does not grant capability authority");
    private static readonly DiagnosticDescriptor BorrowEscape = Rule("SING2001", "Borrow may escape its owner", "BorrowedSpan values must not escape through return", "Ownership");
    private static readonly DiagnosticDescriptor UseAfterMove = Rule("SING2002", "Ownership token used after move", "'{0}' is referenced after Move() consumed its ownership token", "Ownership");
    private static readonly DiagnosticDescriptor ContractMessage = Rule("SING3001", "Invalid SIP contract message", "SIP contract method '{0}' must declare a unique [Message(id)]", "IPC contracts");
    private static readonly DiagnosticDescriptor UnsupportedContractType = Rule("SING3002", "Unsupported SIP contract payload", "Contract member '{0}' uses an unsupported or unbounded payload type", "IPC contracts");
    private static readonly DiagnosticDescriptor SelfMint = Rule("SING4001", "Capability minting is authority-only", "SIP/driver code cannot call capability authority method '{0}'", "Capabilities");
    private static readonly DiagnosticDescriptor NondeterministicArtifact = Rule("SING5001", "Nondeterministic input is forbidden", "API '{0}' is forbidden in deterministic Sing+ artifacts", "Deterministic manifests");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [ManagedAllocation, CapturingClosure, DynamicCode, ForbiddenApi, Boxing, PointerDereference, FunctionPointerInvocation, AddressFormation, UnmanagedRead, UnmanagedWrite, UnsafeCall, ExplicitLayout, InteropBoundary, PointerIndex, FunctionPointerAudit, PointerBoundary, FrameworkMemoryBoundary, BorrowEscape, UseAfterMove, ContractMessage, UnsupportedContractType, SelfMint, NondeterministicArtifact];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(AnalyzeAllocation, OperationKind.ObjectCreation, OperationKind.AnonymousObjectCreation, OperationKind.ArrayCreation);
        context.RegisterOperationAction(AnalyzeConversion, OperationKind.Conversion);
        context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
        context.RegisterOperationAction(AnalyzeOperator, OperationKind.Binary, OperationKind.Unary,
            OperationKind.Increment, OperationKind.Decrement, OperationKind.CompoundAssignment);
        context.RegisterOperationAction(AnalyzeProperty, OperationKind.PropertyReference);
        context.RegisterOperationAction(AnalyzeEventReference, OperationKind.EventReference);
        context.RegisterSyntaxNodeAction(AnalyzeLambda, SyntaxKind.SimpleLambdaExpression, SyntaxKind.ParenthesizedLambdaExpression, SyntaxKind.AnonymousMethodExpression);
        context.RegisterSyntaxNodeAction(AnalyzeDynamic, SyntaxKind.IdentifierName);
        context.RegisterSyntaxNodeAction(AnalyzePointerDereference, SyntaxKind.PointerIndirectionExpression, SyntaxKind.ElementAccessExpression, SyntaxKind.PointerMemberAccessExpression);
        context.RegisterSyntaxNodeAction(AnalyzeFunctionPointerInvocation, SyntaxKind.InvocationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeAddressFormation, SyntaxKind.AddressOfExpression, SyntaxKind.FixedStatement, SyntaxKind.StackAllocArrayCreationExpression, SyntaxKind.ImplicitStackAllocArrayCreationExpression);
        context.RegisterSyntaxNodeAction(AnalyzePointerMemoryAccess, SyntaxKind.PointerIndirectionExpression, SyntaxKind.ElementAccessExpression, SyntaxKind.PointerMemberAccessExpression);
        context.RegisterSyntaxNodeAction(AnalyzeInteropDeclaration, SyntaxKind.MethodDeclaration);
        context.RegisterOperationAction(AnalyzeField, OperationKind.FieldReference);
        context.RegisterSyntaxNodeAction(AnalyzeBorrowReturn, SyntaxKind.ReturnStatement);
        context.RegisterSyntaxNodeAction(AnalyzeMoveUse, SyntaxKind.InvocationExpression);
        context.RegisterSymbolAction(AnalyzeContract, SymbolKind.NamedType);
    }

    private static DiagnosticDescriptor Rule(string id, string title, string message, string category) => new(id, title, message, category, DiagnosticSeverity.Error, isEnabledByDefault: true);
    private static DiagnosticDescriptor MemoryRule(string id, string title, string message) => Rule(id, title, message, "Memory access audit");

    // This is a source admission rule, not a capability grant. Region/domain authority is validated separately.
    private static string MemoryProfile(AnalyzerOptions options)
    {
        if (IsKernelNoHeap(options)) return "KernelNoHeap";
        var profile = Profile(options);
        return profile is "Sip" or "Driver" ? profile : string.Empty;
    }

    private static bool IsKernelNoHeap(AnalyzerOptions options) => options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue("build_property.SingPlusMemoryProfile", out var value) && string.Equals(value, "KernelNoHeap", StringComparison.Ordinal);

    private static string Profile(AnalyzerOptions options)
    {
        options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue("build_property.SingPlusProfile", out var value);
        return value ?? string.Empty;
    }

    private static void AnalyzeAllocation(OperationAnalysisContext context)
    {
        if (context.Operation is IObjectCreationOperation { Constructor: { } constructor })
            AnalyzeMemoryCallable(context, constructor);
        if (!IsKernelNoHeap(context.Options)) return;
        if (context.Operation is IObjectCreationOperation objectCreation && objectCreation.Type?.IsValueType == true) return;
        context.ReportDiagnostic(Diagnostic.Create(ManagedAllocation, context.Operation.Syntax.GetLocation(), context.Operation.Syntax.ToString()));
    }

    private static void AnalyzeConversion(OperationAnalysisContext context)
    {
        var conversion = (IConversionOperation)context.Operation;
        if (conversion.OperatorMethod is { } method) AnalyzeMemoryCallable(context, method);
        if (!IsKernelNoHeap(context.Options)) return;
        if (conversion.Operand.Type?.IsValueType == true && conversion.Type?.IsValueType == false)
            context.ReportDiagnostic(Diagnostic.Create(Boxing, conversion.Syntax.GetLocation(), conversion.Type?.ToDisplayString() ?? "object"));
    }

    private static void AnalyzeLambda(SyntaxNodeAnalysisContext context)
    {
        if (!IsKernelNoHeap(context.Options) || context.Node is not ExpressionSyntax expression) return;
        var flow = context.SemanticModel.AnalyzeDataFlow(expression);
        if (flow is { Succeeded: true, CapturedInside.Length: > 0 }) context.ReportDiagnostic(Diagnostic.Create(CapturingClosure, context.Node.GetLocation()));
    }

    private static void AnalyzeDynamic(SyntaxNodeAnalysisContext context)
    {
        if (!IsKernelNoHeap(context.Options)) return;
        var identifier = (IdentifierNameSyntax)context.Node;
        if (identifier.Identifier.ValueText == "dynamic" && context.SemanticModel.GetTypeInfo(identifier, context.CancellationToken).Type?.TypeKind == TypeKind.Dynamic)
            context.ReportDiagnostic(Diagnostic.Create(DynamicCode, identifier.GetLocation()));
    }

    private static void AnalyzePointerDereference(SyntaxNodeAnalysisContext context)
    {
        if (!IsKernelNoHeap(context.Options)) return;
        var isDereference = context.Node is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PointerIndirectionExpression } ||
            context.Node is MemberAccessExpressionSyntax { RawKind: (int)SyntaxKind.PointerMemberAccessExpression } ||
            context.Node is ElementAccessExpressionSyntax elementAccess &&
            context.SemanticModel.GetTypeInfo(elementAccess.Expression, context.CancellationToken).Type?.TypeKind == TypeKind.Pointer;
        if (isDereference) context.ReportDiagnostic(Diagnostic.Create(PointerDereference, context.Node.GetLocation()));
    }

    private static void AnalyzeFunctionPointerInvocation(SyntaxNodeAnalysisContext context)
    {
        var profile = MemoryProfile(context.Options);
        if (profile.Length == 0) return;
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (context.SemanticModel.GetTypeInfo(invocation.Expression, context.CancellationToken).Type?.TypeKind == TypeKind.FunctionPointer)
            context.ReportDiagnostic(Diagnostic.Create(profile == "KernelNoHeap" ? FunctionPointerInvocation : FunctionPointerAudit, invocation.GetLocation(), profile));
    }

    private static void AnalyzeAddressFormation(SyntaxNodeAnalysisContext context)
    {
        var profile = MemoryProfile(context.Options);
        if (profile.Length == 0) return;
        if (context.Node is (StackAllocArrayCreationExpressionSyntax or ImplicitStackAllocArrayCreationExpressionSyntax) &&
            context.Node is ExpressionSyntax stackAlloc &&
            context.SemanticModel.GetTypeInfo(stackAlloc, context.CancellationToken).ConvertedType?.TypeKind != TypeKind.Pointer &&
            stackAlloc.Parent is not EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Type: PointerTypeSyntax } } }) return;
        context.ReportDiagnostic(Diagnostic.Create(AddressFormation, context.Node.GetLocation(), context.Node.Kind().ToString(), profile));
    }

    private static void AnalyzePointerMemoryAccess(SyntaxNodeAnalysisContext context)
    {
        var profile = MemoryProfile(context.Options);
        if (profile.Length == 0) return;
        var node = context.Node;
        if (node is ElementAccessExpressionSyntax element &&
            context.SemanticModel.GetTypeInfo(element.Expression, context.CancellationToken).Type?.TypeKind != TypeKind.Pointer) return;
        if (node is MemberAccessExpressionSyntax member &&
            context.SemanticModel.GetTypeInfo(member.Expression, context.CancellationToken).Type?.TypeKind != TypeKind.Pointer) return;
        if (node is ElementAccessExpressionSyntax) context.ReportDiagnostic(Diagnostic.Create(PointerIndex, node.GetLocation(), profile));
        // Parentheses do not change the memory effect of the underlying pointer access.
        SyntaxNode access = node;
        while (true)
        {
            if (access.Parent is ParenthesizedExpressionSyntax parenthesized) access = parenthesized;
            else if (access.Parent is MemberAccessExpressionSyntax memberAccess && memberAccess.Expression == access &&
                context.SemanticModel.GetSymbolInfo(memberAccess, context.CancellationToken).Symbol is IFieldSymbol &&
                context.SemanticModel.GetTypeInfo(access, context.CancellationToken).Type?.IsValueType == true)
                access = memberAccess;
            else break;
        }
        var assignment = access.Parent as AssignmentExpressionSyntax;
        var isIncrement = access.Parent is PrefixUnaryExpressionSyntax prefix &&
            (prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression)) || access.Parent is PostfixUnaryExpressionSyntax postfix &&
            (postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression));
        var isAssigned = assignment?.Left == access;
        var argumentKind = access.Parent is ArgumentSyntax argument ? argument.RefKindKeyword.Kind() : SyntaxKind.None;
        var refExposure = access.Parent is RefExpressionSyntax;
        var readonlyExposure = refExposure && (
            access.Parent?.Parent is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Type: RefTypeSyntax { ReadOnlyKeyword.RawKind: (int)SyntaxKind.ReadOnlyKeyword } } } } ||
            access.Parent?.Parent is ReturnStatementSyntax &&
                context.SemanticModel.GetEnclosingSymbol(access.SpanStart, context.CancellationToken) is IMethodSymbol { ReturnsByRefReadonly: true });
        var addressOnly = access.Parent is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.AddressOfExpression };
        // A ref argument exposes a writable unmanaged location to the callee; out is write-only.
        var mayRead = !addressOnly && argumentKind != SyntaxKind.OutKeyword &&
            (!isAssigned || assignment is { RawKind: not (int)SyntaxKind.SimpleAssignmentExpression } || isIncrement);
        var mayWrite = !addressOnly && (isAssigned || isIncrement || argumentKind is SyntaxKind.RefKeyword or SyntaxKind.OutKeyword ||
            refExposure && !readonlyExposure);
        if (mayRead)
            context.ReportDiagnostic(Diagnostic.Create(UnmanagedRead, node.GetLocation(), profile));
        if (mayWrite)
            context.ReportDiagnostic(Diagnostic.Create(UnmanagedWrite, node.GetLocation(), profile));
    }

    private static void AnalyzeInteropDeclaration(SyntaxNodeAnalysisContext context)
    {
        var profile = MemoryProfile(context.Options);
        if (profile.Length == 0) return;
        var declaration = (MethodDeclarationSyntax)context.Node;
        var method = context.SemanticModel.GetDeclaredSymbol(declaration, context.CancellationToken);
        if (method is not null && IsInterop(method))
            context.ReportDiagnostic(Diagnostic.Create(InteropBoundary, declaration.Identifier.GetLocation(), method.Name, profile));
    }

    private static bool IsInterop(IMethodSymbol method) => method.IsExtern || method.GetAttributes().Any(static a =>
        a.AttributeClass?.ToDisplayString() is "System.Runtime.InteropServices.DllImportAttribute" or "System.Runtime.InteropServices.LibraryImportAttribute" or "System.Runtime.InteropServices.UnmanagedCallersOnlyAttribute");

    private static bool HasPointerSignature(IMethodSymbol method) =>
        method.ReturnType.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer ||
        method.Parameters.Any(static parameter => parameter.Type.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer);

    private static void AnalyzeField(OperationAnalysisContext context)
    {
        var profile = MemoryProfile(context.Options);
        if (profile.Length == 0) return;
        var field = ((IFieldReferenceOperation)context.Operation).Field;
        if (field.ContainingType.GetAttributes().Any(static a => a.AttributeClass?.ToDisplayString() == "System.Runtime.InteropServices.StructLayoutAttribute" &&
            a.ConstructorArguments.Length > 0 && a.ConstructorArguments[0].Value is 2))
            context.ReportDiagnostic(Diagnostic.Create(ExplicitLayout, context.Operation.Syntax.GetLocation(), field.Name, profile));
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;
        var containingType = method.ContainingType?.ToDisplayString() ?? string.Empty;
        var containingNamespace = method.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        var api = containingType + "." + method.Name;
        AnalyzeMemoryCallable(context, method);
        if (IsKernelNoHeap(context.Options) && IsForbiddenKernelApi(containingType, containingNamespace, method.Name)) context.ReportDiagnostic(Diagnostic.Create(ForbiddenApi, invocation.Syntax.GetLocation(), api));
        var profile = Profile(context.Options);
        if ((profile == "Sip" || profile == "Driver") && containingType.EndsWith("CapabilityAuthority", StringComparison.Ordinal) && (method.Name == "Mint" || method.Name == "Delegate")) context.ReportDiagnostic(Diagnostic.Create(SelfMint, invocation.Syntax.GetLocation(), method.Name));
        if (IsNondeterministic(containingType, method.Name)) context.ReportDiagnostic(Diagnostic.Create(NondeterministicArtifact, invocation.Syntax.GetLocation(), api));
    }

    private static void AnalyzeOperator(OperationAnalysisContext context)
    {
        var method = context.Operation switch
        {
            IBinaryOperation binary => binary.OperatorMethod,
            IUnaryOperation unary => unary.OperatorMethod,
            IIncrementOrDecrementOperation increment => increment.OperatorMethod,
            ICompoundAssignmentOperation compound => compound.OperatorMethod,
            _ => null
        };
        if (method is not null) AnalyzeMemoryCallable(context, method);
    }

    private static void AnalyzeMemoryCallable(OperationAnalysisContext context, IMethodSymbol method)
    {
        var containingType = method.ContainingType?.ToDisplayString() ?? string.Empty;
        var api = containingType + "." + method.Name;
        var memoryProfile = MemoryProfile(context.Options);
        if (memoryProfile.Length != 0)
        {
            // Pointer signatures remain auditable across reference-assembly boundaries even when source modifiers are unavailable.
            if (HasPointerSignature(method))
                context.ReportDiagnostic(Diagnostic.Create(PointerBoundary, context.Operation.Syntax.GetLocation(), api, memoryProfile));
            if (IsFrameworkMemoryBoundary(containingType, method.Name))
                context.ReportDiagnostic(Diagnostic.Create(FrameworkMemoryBoundary, context.Operation.Syntax.GetLocation(), api, memoryProfile));
            if (IsInterop(method)) context.ReportDiagnostic(Diagnostic.Create(InteropBoundary, context.Operation.Syntax.GetLocation(), api, memoryProfile));
            else if (HasUnsafeSourceCallable(method))
                context.ReportDiagnostic(Diagnostic.Create(UnsafeCall, context.Operation.Syntax.GetLocation(), api, memoryProfile));
        }
    }

    private static bool HasUnsafeSourceCallable(IMethodSymbol method) =>
        HasUnsafeSourceDeclaration(method) ||
        method.AssociatedSymbol is { } associated && HasUnsafeSourceDeclaration(associated);

    private static bool HasUnsafeSourceDeclaration(ISymbol symbol) =>
        symbol.DeclaringSyntaxReferences.Any(static reference => IsUnsafeSourceCallable(reference.GetSyntax())) ||
        symbol.ContainingType?.DeclaringSyntaxReferences.Any(static reference => IsUnsafeSourceCallable(reference.GetSyntax())) == true;

    private static bool IsUnsafeSourceCallable(SyntaxNode declaration)
    {
        if (declaration is not (BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AccessorDeclarationSyntax or
            PropertyDeclarationSyntax or IndexerDeclarationSyntax or EventDeclarationSyntax or EventFieldDeclarationSyntax or
            TypeDeclarationSyntax)) return false;
        // Audit declaration context, not the caller's context. Local functions and nested
        // types inherit unsafe blocks/members/types enclosing their own declaration.
        return declaration.AncestorsAndSelf().Any(static node => node switch
        {
            UnsafeStatementSyntax => true,
            BaseMethodDeclarationSyntax method => method.Modifiers.Any(SyntaxKind.UnsafeKeyword),
            LocalFunctionStatementSyntax local => local.Modifiers.Any(SyntaxKind.UnsafeKeyword),
            TypeDeclarationSyntax type => type.Modifiers.Any(SyntaxKind.UnsafeKeyword),
            PropertyDeclarationSyntax property => property.Modifiers.Any(SyntaxKind.UnsafeKeyword),
            IndexerDeclarationSyntax indexer => indexer.Modifiers.Any(SyntaxKind.UnsafeKeyword),
            _ => false
        });
    }

    private static void AnalyzeEventReference(OperationAnalysisContext context)
    {
        var reference = (IEventReferenceOperation)context.Operation;
        if (reference.Parent is not IEventAssignmentOperation assignment) return;
        var @event = reference.Event;
        var accessor = assignment.Adds ? @event.AddMethod : @event.RemoveMethod;
        if (accessor is not null && MemoryProfile(context.Options).Length != 0 &&
            (HasUnsafeSourceCallable(accessor) || HasUnsafeSourceDeclaration(@event)))
            context.ReportDiagnostic(Diagnostic.Create(UnsafeCall, assignment.Syntax.GetLocation(),
                (@event.ContainingType?.ToDisplayString() ?? string.Empty) + "." + @event.Name,
                MemoryProfile(context.Options)));
    }

    private static void AnalyzeProperty(OperationAnalysisContext context)
    {
        var property = (IPropertyReferenceOperation)context.Operation;
        var containingType = property.Property.ContainingType?.ToDisplayString() ?? string.Empty;
        var name = property.Property.Name;
        var memoryProfile = MemoryProfile(context.Options);
        if (memoryProfile.Length != 0)
        {
            IOperation access = property;
            while (access.Parent is IConversionOperation conversion && conversion.IsImplicit) access = conversion;
            var assignment = access.Parent as IAssignmentOperation;
            var writes = assignment?.Target == access || access.Parent is IIncrementOrDecrementOperation;
            var reads = !writes || assignment is ICompoundAssignmentOperation || access.Parent is IIncrementOrDecrementOperation;
            var getter = reads || property.Property.ReturnsByRef || property.Property.ReturnsByRefReadonly ? property.Property.GetMethod : null;
            var setter = writes ? property.Property.SetMethod : null;
            if (new[] { getter, setter }.Any(static method => method is not null &&
                HasUnsafeSourceCallable(method)))
                context.ReportDiagnostic(Diagnostic.Create(UnsafeCall, property.Syntax.GetLocation(), containingType + "." + name, memoryProfile));
        }
        if ((containingType == "System.DateTime" && (name == "Now" || name == "UtcNow")) || (containingType == "System.Environment" && (name == "MachineName" || name == "CurrentDirectory")))
            context.ReportDiagnostic(Diagnostic.Create(NondeterministicArtifact, property.Syntax.GetLocation(), containingType + "." + name));
    }

    private static bool IsForbiddenKernelApi(string type, string ns, string method) => type == "System.Console" || type == "System.Environment" || type == "System.GC" || type == "System.Activator" || type == "System.Threading.ThreadPool" || type == "System.Threading.Tasks.Task" || type == "System.Diagnostics.Process" || (type == "System.Delegate" && method == "CreateDelegate") || ns.StartsWith("System.IO", StringComparison.Ordinal) || ns.StartsWith("System.Net", StringComparison.Ordinal) || ns.StartsWith("System.Reflection", StringComparison.Ordinal) || ns.StartsWith("System.Linq.Expressions", StringComparison.Ordinal);

    private static bool IsFrameworkMemoryBoundary(string type, string method) =>
        (type == "System.Runtime.CompilerServices.Unsafe" && method != "SizeOf") ||
        type is "System.Runtime.InteropServices.MemoryMarshal" or "System.Runtime.InteropServices.CollectionsMarshal" or
            "System.Runtime.InteropServices.Marshal" or "System.Runtime.InteropServices.NativeMemory";

    private static bool IsNondeterministic(string type, string method) => (type == "System.Guid" && method == "NewGuid") || type == "System.Random";

    private static void AnalyzeBorrowReturn(SyntaxNodeAnalysisContext context)
    {
        var statement = (ReturnStatementSyntax)context.Node;
        if (statement.Expression is not IdentifierNameSyntax) return;
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
        if (type.Arity != 0) context.ReportDiagnostic(Diagnostic.Create(UnsupportedContractType, type.Locations.FirstOrDefault(), type.Name));
        var ids = new HashSet<int>();
        foreach (var method in type.GetMembers().OfType<IMethodSymbol>().Where(static m => m.MethodKind == MethodKind.Ordinary))
        {
            var message = method.GetAttributes().FirstOrDefault(static a => a.AttributeClass?.Name == "MessageAttribute");
            var valid = message is not null && message.ConstructorArguments.Length == 1 && message.ConstructorArguments[0].Value is int id && id > 0 && ids.Add(id);
            if (!valid) context.ReportDiagnostic(Diagnostic.Create(ContractMessage, method.Locations.FirstOrDefault(), method.Name));
            if (!method.ReturnsVoid && !IsSupportedContractType(method.ReturnType)) context.ReportDiagnostic(Diagnostic.Create(UnsupportedContractType, method.Locations.FirstOrDefault(), method.Name));
            foreach (var parameter in method.Parameters)
            {
                var consumes = parameter.GetAttributes().Any(static a => a.AttributeClass?.Name == "ConsumesAttribute");
                var borrows = parameter.GetAttributes().Any(static a => a.AttributeClass?.Name == "BorrowsAttribute");
                if (parameter.RefKind != RefKind.None || !IsSupportedContractType(parameter.Type) || (consumes && borrows))
                    context.ReportDiagnostic(Diagnostic.Create(UnsupportedContractType, parameter.Locations.FirstOrDefault(), method.Name + "." + parameter.Name));
            }
        }
    }

    private static bool IsSupportedContractType(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Enum) return true;
        if (type.SpecialType is SpecialType.System_Boolean or SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Char or SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal) return true;
        if (type is INamedTypeSymbol named && named.Name is "OwnedBuffer" or "OwnedRegion") return true;
        return type.GetAttributes().Any(static a => a.AttributeClass?.Name == "BoundedPayloadAttribute");
    }
}
