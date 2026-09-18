using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace SingPlus.Admission;

public static class AdmissionVerifier
{
    private const string Ruleset = "SingPlusAdmissionRulesV10|AssemblyIdentity:unique|ParentType:named-generic,nested,unsupported-reject|Root:unique-name|LocalCall:unique-name-or-reject|KernelNoHeap:newobj,newarr,box,ldind,stind,ldobj,stobj,cpblk,initblk,localloc,calli,interop(PinvokeImpl,InternalCall,Unmanaged,NonCil,DllImport,LibraryImport,UnmanagedCallersOnly),explicit-layout-field,framework-memory-boundary|ConcreteReachableBody:required,AbstractRoot:deny|RuntimeAsyncV2:lowering-unavailable|ForbiddenApi:System.Console,System.Environment,System.GC,System.Activator,System.Threading.ThreadPool,System.Threading.Tasks.Task,System.Diagnostics.Process,System.IO.*,System.Net.*,System.Reflection.*,System.Linq.Expressions.*|ForbiddenAssemblies:System.Console,System.IO.*,System.Net.*,System.Reflection.Emit*,Microsoft.CSharp|UnknownDependency:deny|LocalSingPlusDependency:required,identity-match,raw-sha256,transitive";

    public static AdmissionVerificationResult Verify(string assemblyPath, string root, string profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);

        var fullPath = Path.GetFullPath(assemblyPath);
        var models = LoadLocalAssemblies(fullPath);
        try
        {
            var rootModel = models.Values.FirstOrDefault(m => string.Equals(m.Path, fullPath, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Root assembly could not be loaded as managed metadata.");
            var assemblyDigest = rootModel.ContentDigest;
            var violations = new List<AdmissionViolation>();
            var dependencies = CollectDependencies(rootModel, models, profile, violations);
            var dependencyDigest = SingPlusAdmissionProofV1.Digest(string.Join("\n", dependencies));
            var rulesetDigest = SingPlusAdmissionProofV1.Digest(Ruleset);
            var reachable = Traverse(rootModel, root, profile, models, violations);
            var orderedViolations = violations.OrderBy(static v => v.CanonicalKey, StringComparer.Ordinal).ToArray();
            var proofSeed = string.Join("\n", new[]
            {
                SingPlusAdmissionProofV1.Schema,
                root,
                profile,
                assemblyDigest,
                reachable.ToString(System.Globalization.CultureInfo.InvariantCulture),
                orderedViolations.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                dependencyDigest,
                rulesetDigest,
                string.Join("\n", orderedViolations.Select(static v => v.CanonicalKey))
            });
            var proof = new SingPlusAdmissionProofV1
            {
                Root = root,
                Profile = profile,
                AssemblyDigest = assemblyDigest,
                ReachableMethodCount = reachable,
                ForbiddenOperationCount = orderedViolations.Length,
                DependencyDigest = dependencyDigest,
                RulesetDigest = rulesetDigest,
                ProofDigest = SingPlusAdmissionProofV1.Digest(proofSeed)
            };
            return new AdmissionVerificationResult(proof, orderedViolations);
        }
        finally
        {
            foreach (var model in models.Values.Distinct()) model.Dispose();
        }
    }

    private static Dictionary<string, AssemblyModel> LoadLocalAssemblies(string rootPath)
    {
        var byName = new Dictionary<string, AssemblyModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(Path.GetDirectoryName(rootPath)!, "*.dll").Append(rootPath).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static p => p, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var model = new AssemblyModel(Path.GetFullPath(path));
                if (!byName.TryAdd(model.Name, model))
                {
                    model.Dispose();
                    foreach (var loaded in byName.Values) loaded.Dispose();
                    throw new InvalidOperationException($"Ambiguous local assembly identity '{model.Name}'.");
                }
            }
            catch (BadImageFormatException)
            {
            }
        }
        return byName;
    }

    private static string[] CollectDependencies(AssemblyModel root, IReadOnlyDictionary<string, AssemblyModel> models, string profile, List<AdmissionViolation> violations)
    {
        var dependencies = new List<string>();
        var pending = new Queue<AssemblyModel>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Enqueue(root);
        while (pending.Count != 0)
        {
            var model = pending.Dequeue();
            if (!visited.Add(model.Name)) continue;
            foreach (var handle in model.Reader.AssemblyReferences)
            {
                var reference = model.Reader.GetAssemblyReference(handle);
                var name = model.Reader.GetString(reference.Name);
                var hasLocal = models.TryGetValue(name, out var local);
                var identity = model.Name + "->" + name + "|" + reference.Version;
                var content = hasLocal ? local!.ContentDigest : "external";
                dependencies.Add(identity + "|" + content);
                if (IsForbiddenAssembly(name)) violations.Add(new AdmissionViolation(model.Name, "forbidden-dependency", name));
                else if (!IsKnownDependency(name)) violations.Add(new AdmissionViolation(model.Name, "unknown-dependency-category", name));
                else if (string.Equals(profile, "KernelNoHeap", StringComparison.Ordinal) &&
                         name.StartsWith("SingPlus.", StringComparison.Ordinal) && !hasLocal)
                    violations.Add(new AdmissionViolation(model.Name, "missing-local-dependency", name));
                else if (string.Equals(profile, "KernelNoHeap", StringComparison.Ordinal) &&
                         name.StartsWith("SingPlus.", StringComparison.Ordinal) && hasLocal && local!.Version != reference.Version)
                    violations.Add(new AdmissionViolation(model.Name, "local-dependency-identity-mismatch", identity));
                if (hasLocal) pending.Enqueue(local!);
            }
        }
        return dependencies.OrderBy(static x => x, StringComparer.Ordinal).ToArray();
    }

    private static int Traverse(AssemblyModel rootModel, string root, string profile, IReadOnlyDictionary<string, AssemblyModel> models, List<AdmissionViolation> violations)
    {
        var rootHandle = rootModel.FindMethod(root, requireUnique: true) ?? throw new InvalidOperationException($"Admission root '{root}' was not found.");
        var queue = new Queue<MethodLocation>();
        var visited = new HashSet<MethodLocation>();
        queue.Enqueue(new MethodLocation(rootModel.Name, rootHandle));

        while (queue.Count != 0)
        {
            var location = queue.Dequeue();
            if (!visited.Add(location)) continue;
            if (!models.TryGetValue(location.AssemblyName, out var model)) continue;
            var definition = model.Reader.GetMethodDefinition(location.Handle);
            var methodName = model.GetMethodDisplayName(location.Handle);
            if (string.Equals(profile, "KernelNoHeap", StringComparison.Ordinal) &&
                ManagedAsyncPeQualification.HasRuntimeAsyncMarker(definition))
                violations.Add(new AdmissionViolation(methodName, "unsupported-async-abi", "Runtime Async V2 lowering is unavailable"));
            if (string.Equals(profile, "KernelNoHeap", StringComparison.Ordinal) &&
                model.IsInteropMethod(definition))
                violations.Add(new AdmissionViolation(methodName, "interop-boundary", "Unmanaged method boundary"));
            if (definition.RelativeVirtualAddress == 0)
            {
                // Abstract call targets describe dispatch contracts, not unresolved extern
                // implementations. Preserve that integration surface; an abstract root is
                // still not an executable body and must fail closed.
                if (string.Equals(profile, "KernelNoHeap", StringComparison.Ordinal) &&
                    ((definition.Attributes & System.Reflection.MethodAttributes.Abstract) == 0 ||
                     (location.AssemblyName == rootModel.Name && location.Handle == rootHandle)))
                    violations.Add(new AdmissionViolation(methodName, "unavailable-method-body", "Reachable method has no auditable CIL body"));
                continue;
            }
            var body = model.PeReader.GetMethodBody(definition.RelativeVirtualAddress);
            var il = body.GetILBytes()?.ToArray() ?? Array.Empty<byte>();
            foreach (var instruction in IlReader.Read(il))
            {
                if (string.Equals(profile, "KernelNoHeap", StringComparison.Ordinal))
                {
                    if (instruction.OpCode == System.Reflection.Emit.OpCodes.Newobj)
                        violations.Add(new AdmissionViolation(methodName, "newobj", "managed/object construction"));
                    else if (instruction.OpCode == System.Reflection.Emit.OpCodes.Newarr)
                        violations.Add(new AdmissionViolation(methodName, "newarr", "managed array allocation"));
                    else if (instruction.OpCode == System.Reflection.Emit.OpCodes.Box)
                        violations.Add(new AdmissionViolation(methodName, "box", "boxing conversion"));
                    else if (IsUnmanagedMemoryOpcode(instruction.OpCode))
                        violations.Add(new AdmissionViolation(methodName, "unmanaged-memory", instruction.OpCode.Name ?? "unknown"));
                    else if (instruction.OpCode == System.Reflection.Emit.OpCodes.Calli)
                        violations.Add(new AdmissionViolation(methodName, "function-pointer-invoke", "calli"));
                    else if (instruction.OpCode == System.Reflection.Emit.OpCodes.Localloc)
                        violations.Add(new AdmissionViolation(methodName, "pointer-stackalloc", "localloc"));
                    if (instruction.OpCode.OperandType == System.Reflection.Emit.OperandType.InlineField &&
                        instruction.MetadataToken is int fieldToken && model.IsExplicitLayoutField(fieldToken, models))
                        violations.Add(new AdmissionViolation(methodName, "explicit-layout-field", "field access"));
                }

                if (instruction.MetadataToken is not int token || instruction.OpCode.OperandType != System.Reflection.Emit.OperandType.InlineMethod) continue;
                var target = model.ResolveMethod(token, models);
                if (target.DisplayName is not null && IsForbiddenApi(target.DisplayName))
                    violations.Add(new AdmissionViolation(methodName, "forbidden-api", target.DisplayName));
                if (string.Equals(profile, "KernelNoHeap", StringComparison.Ordinal) && target.DisplayName is not null &&
                    IsFrameworkMemoryBoundary(target.DisplayName))
                    violations.Add(new AdmissionViolation(methodName, "framework-memory-boundary", target.DisplayName));
                if (target.Location is MethodLocation next) queue.Enqueue(next);
            }
        }
        return visited.Count;
    }

    private static bool IsForbiddenAssembly(string name) =>
        name == "System.Console" || name == "Microsoft.CSharp" || name.StartsWith("System.IO.", StringComparison.Ordinal) ||
        name.StartsWith("System.Net.", StringComparison.Ordinal) || name.StartsWith("System.Reflection.Emit", StringComparison.Ordinal);

    private static bool IsUnmanagedMemoryOpcode(System.Reflection.Emit.OpCode opcode)
    {
        var name = opcode.Name ?? string.Empty;
        return name.StartsWith("ldind.", StringComparison.Ordinal) || name.StartsWith("stind.", StringComparison.Ordinal) ||
            name is "ldobj" or "stobj" or "cpblk" or "initblk";
    }

    private static bool IsKnownDependency(string name) =>
        name == "mscorlib" || name == "netstandard" || name.StartsWith("System.", StringComparison.Ordinal) ||
        name.StartsWith("Microsoft.", StringComparison.Ordinal) || name.StartsWith("SingPlus.", StringComparison.Ordinal);

    private static bool IsForbiddenApi(string displayName)
    {
        var separator = displayName.IndexOf("::", StringComparison.Ordinal);
        var type = separator >= 0 ? displayName[..separator] : displayName;
        return type == "System.Console" || type == "System.Environment" || type == "System.GC" || type == "System.Activator" ||
            type == "System.Threading.ThreadPool" || type == "System.Threading.Tasks.Task" || type == "System.Diagnostics.Process" ||
            type == "System.Delegate" || type.StartsWith("System.IO.", StringComparison.Ordinal) || type.StartsWith("System.Net.", StringComparison.Ordinal) ||
            type.StartsWith("System.Reflection.", StringComparison.Ordinal) || type.StartsWith("System.Linq.Expressions.", StringComparison.Ordinal);
    }

    private static bool IsFrameworkMemoryBoundary(string displayName)
    {
        var separator = displayName.IndexOf("::", StringComparison.Ordinal);
        if (separator < 0) return false;
        var type = displayName[..separator];
        var method = displayName[(separator + 2)..];
        return (type == "System.Runtime.CompilerServices.Unsafe" && method != "SizeOf") ||
            type is "System.Runtime.InteropServices.MemoryMarshal" or "System.Runtime.InteropServices.CollectionsMarshal" or
                "System.Runtime.InteropServices.Marshal" or "System.Runtime.InteropServices.NativeMemory";
    }

    private readonly record struct MethodLocation(string AssemblyName, MethodDefinitionHandle Handle);

    private sealed class AssemblyModel : IDisposable
    {
        private readonly MemoryStream _stream;

        public AssemblyModel(string path)
        {
            Path = path;
            var image = File.ReadAllBytes(path);
            ContentDigest = Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();
            _stream = new MemoryStream(image, writable: false);
            PeReader = new PEReader(_stream, PEStreamOptions.LeaveOpen);
            if (!PeReader.HasMetadata) throw new BadImageFormatException(path);
            Reader = PeReader.GetMetadataReader();
            Name = Reader.GetString(Reader.GetAssemblyDefinition().Name);
        }

        public string Path { get; }
        public string ContentDigest { get; }
        public Version Version => Reader.GetAssemblyDefinition().Version;
        public string Name { get; }
        public PEReader PeReader { get; }
        public MetadataReader Reader { get; }

        public MethodDefinitionHandle? FindMethod(string identity, bool requireUnique = false)
        {
            var split = identity.Split(new[] { "::" }, 2, StringSplitOptions.None);
            if (split.Length != 2) throw new ArgumentException("Root must use Type::Method format.", nameof(identity));
            MethodDefinitionHandle? match = null;
            foreach (var typeHandle in Reader.TypeDefinitions)
            {
                var type = Reader.GetTypeDefinition(typeHandle);
                var fullName = FullTypeName(type);
                if (!string.Equals(fullName, split[0], StringComparison.Ordinal)) continue;
                foreach (var methodHandle in type.GetMethods())
                {
                    if (!string.Equals(Reader.GetString(Reader.GetMethodDefinition(methodHandle).Name), split[1], StringComparison.Ordinal)) continue;
                    if (!requireUnique) return methodHandle;
                    if (match is not null)
                        throw new InvalidOperationException($"Admission method identity '{identity}' is overloaded; signature-qualified resolution is required.");
                    match = methodHandle;
                }
            }
            return match;
        }

        public string GetMethodDisplayName(MethodDefinitionHandle handle)
        {
            var method = Reader.GetMethodDefinition(handle);
            var type = Reader.GetTypeDefinition(method.GetDeclaringType());
            return FullTypeName(type) + "::" + Reader.GetString(method.Name);
        }

        public bool IsInteropMethod(MethodDefinition method)
        {
            if ((method.Attributes & System.Reflection.MethodAttributes.PinvokeImpl) != 0) return true;
            if ((method.ImplAttributes & (System.Reflection.MethodImplAttributes.InternalCall |
                    System.Reflection.MethodImplAttributes.Unmanaged)) != 0 ||
                (method.ImplAttributes & System.Reflection.MethodImplAttributes.CodeTypeMask) != System.Reflection.MethodImplAttributes.IL)
                return true;
            foreach (var handle in method.GetCustomAttributes())
            {
                var constructor = Reader.GetCustomAttribute(handle).Constructor;
                var typeName = constructor.Kind switch
                {
                    HandleKind.MemberReference => ResolveParentType(Reader.GetMemberReference((MemberReferenceHandle)constructor).Parent).FullName,
                    HandleKind.MethodDefinition => FullTypeName(Reader.GetTypeDefinition(Reader.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType())),
                    _ => null
                };
                // These markers are denial signals, never proof of trust or execution authority.
                if (typeName is "System.Runtime.InteropServices.DllImportAttribute" or
                    "System.Runtime.InteropServices.LibraryImportAttribute" or
                    "System.Runtime.InteropServices.UnmanagedCallersOnlyAttribute") return true;
            }
            return false;
        }

        public bool IsExplicitLayoutField(int token, IReadOnlyDictionary<string, AssemblyModel> models)
        {
            EntityHandle handle;
            try { handle = MetadataTokens.EntityHandle(token); }
            catch (ArgumentException) { return false; }
            if (handle.Kind == HandleKind.FieldDefinition)
            {
                var field = Reader.GetFieldDefinition((FieldDefinitionHandle)handle);
                return IsExplicitLayout(Reader.GetTypeDefinition(field.GetDeclaringType()));
            }
            if (handle.Kind != HandleKind.MemberReference) return false;
            var member = Reader.GetMemberReference((MemberReferenceHandle)handle);
            var parent = ResolveParentType(member.Parent);
            if (parent.AssemblyName is null || parent.FullName is null || !models.TryGetValue(parent.AssemblyName, out var target)) return false;
            return target.Reader.TypeDefinitions.Any(typeHandle =>
            {
                var type = target.Reader.GetTypeDefinition(typeHandle);
                return string.Equals(target.FullTypeName(type), parent.FullName, StringComparison.Ordinal) && IsExplicitLayout(type);
            });
        }

        private static bool IsExplicitLayout(TypeDefinition type) =>
            (type.Attributes & System.Reflection.TypeAttributes.LayoutMask) == System.Reflection.TypeAttributes.ExplicitLayout;

        public (MethodLocation? Location, string? DisplayName) ResolveMethod(int token, IReadOnlyDictionary<string, AssemblyModel> models)
        {
            EntityHandle handle;
            try { handle = MetadataTokens.EntityHandle(token); }
            catch (ArgumentException) { return (null, null); }

            if (handle.Kind == HandleKind.MethodDefinition)
            {
                var method = (MethodDefinitionHandle)handle;
                return (new MethodLocation(Name, method), GetMethodDisplayName(method));
            }
            if (handle.Kind == HandleKind.MethodSpecification)
            {
                var spec = Reader.GetMethodSpecification((MethodSpecificationHandle)handle);
                return ResolveEntityMethod(spec.Method, models);
            }
            if (handle.Kind == HandleKind.MemberReference)
                return ResolveMemberReference((MemberReferenceHandle)handle, models);
            return (null, null);
        }

        private (MethodLocation? Location, string? DisplayName) ResolveEntityMethod(EntityHandle handle, IReadOnlyDictionary<string, AssemblyModel> models)
        {
            if (handle.Kind == HandleKind.MethodDefinition)
            {
                var method = (MethodDefinitionHandle)handle;
                return (new MethodLocation(Name, method), GetMethodDisplayName(method));
            }
            if (handle.Kind == HandleKind.MemberReference) return ResolveMemberReference((MemberReferenceHandle)handle, models);
            return (null, null);
        }

        private (MethodLocation? Location, string? DisplayName) ResolveMemberReference(MemberReferenceHandle handle, IReadOnlyDictionary<string, AssemblyModel> models)
        {
            var member = Reader.GetMemberReference(handle);
            var methodName = Reader.GetString(member.Name);
            var type = ResolveParentType(member.Parent);
            var display = type.FullName is null ? methodName : type.FullName + "::" + methodName;
            if (type.AssemblyName is null || type.FullName is null)
                throw new InvalidOperationException($"Unsupported method parent for '{display}'.");
            if (!models.TryGetValue(type.AssemblyName, out var targetModel)) return (null, display);
            // Cross-assembly signature resolution is not implemented. Never audit an arbitrary overload.
            var local = targetModel.FindMethod(type.FullName + "::" + methodName, requireUnique: true);
            if (local is null) throw new InvalidOperationException($"Unresolved local call target '{display}'.");
            return (new MethodLocation(targetModel.Name, local.Value), display);
        }

        private (string? AssemblyName, string? FullName) ResolveParentType(EntityHandle parent)
        {
            if (parent.Kind == HandleKind.TypeSpecification)
            {
                var blob = Reader.GetBlobReader(Reader.GetTypeSpecification((TypeSpecificationHandle)parent).Signature);
                // A constructed named type has the same definition body for every type argument.
                // Other TypeSpec parents require explicit importer support, never display-only admission.
                if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance ||
                    blob.ReadSignatureTypeCode() != SignatureTypeCode.TypeHandle)
                    throw new InvalidOperationException("Unsupported TypeSpecification method/field parent.");
                return ResolveParentType(blob.ReadTypeHandle());
            }
            if (parent.Kind == HandleKind.TypeDefinition)
            {
                var type = Reader.GetTypeDefinition((TypeDefinitionHandle)parent);
                return (Name, FullTypeName(type));
            }
            if (parent.Kind != HandleKind.TypeReference) return (null, null);
            var reference = Reader.GetTypeReference((TypeReferenceHandle)parent);
            var ns = Reader.GetString(reference.Namespace);
            var typeName = Reader.GetString(reference.Name);
            var full = string.IsNullOrEmpty(ns) ? typeName : ns + "." + typeName;
            var scope = reference.ResolutionScope;
            if (scope.Kind == HandleKind.TypeReference)
            {
                var enclosing = ResolveParentType(scope);
                return (enclosing.AssemblyName, enclosing.FullName + "+" + typeName);
            }
            if (scope.Kind == HandleKind.AssemblyReference)
            {
                var assemblyReference = Reader.GetAssemblyReference((AssemblyReferenceHandle)scope);
                return (Reader.GetString(assemblyReference.Name), full);
            }
            if (scope.Kind == HandleKind.ModuleDefinition) return (Name, full);
            return (null, full);
        }

        private string FullTypeName(TypeDefinition type)
        {
            var ns = Reader.GetString(type.Namespace);
            var name = Reader.GetString(type.Name);
            if (type.GetDeclaringType() is { IsNil: false } enclosing)
                return FullTypeName(Reader.GetTypeDefinition(enclosing)) + "+" + name;
            return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
        }

        public void Dispose()
        {
            PeReader.Dispose();
            _stream.Dispose();
        }
    }
}
