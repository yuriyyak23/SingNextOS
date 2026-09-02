using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace SingPlus.Admission;

public static class AdmissionVerifier
{
    private const string Ruleset = "SingPlusAdmissionRulesV1|KernelNoHeap:newobj,newarr,box|ForbiddenApi:System.Console,System.Environment,System.GC,System.Activator,System.Threading.ThreadPool,System.Threading.Tasks.Task,System.Diagnostics.Process,System.IO.*,System.Net.*,System.Reflection.*,System.Linq.Expressions.*|ForbiddenAssemblies:System.Console,System.IO.*,System.Net.*,System.Reflection.Emit*,Microsoft.CSharp|UnknownDependency:deny";

    public static AdmissionVerificationResult Verify(string assemblyPath, string root, string profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);

        var fullPath = Path.GetFullPath(assemblyPath);
        var rootBytes = File.ReadAllBytes(fullPath);
        var assemblyDigest = Convert.ToHexString(SHA256.HashData(rootBytes)).ToLowerInvariant();
        var models = LoadLocalAssemblies(fullPath);
        try
        {
            var rootModel = models.Values.FirstOrDefault(m => string.Equals(m.Path, fullPath, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Root assembly could not be loaded as managed metadata.");
            var violations = new List<AdmissionViolation>();
            var dependencies = CollectDependencies(rootModel, violations);
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
                if (!byName.TryAdd(model.Name, model)) model.Dispose();
            }
            catch (BadImageFormatException)
            {
            }
        }
        return byName;
    }

    private static string[] CollectDependencies(AssemblyModel root, List<AdmissionViolation> violations)
    {
        var dependencies = new List<string>();
        foreach (var handle in root.Reader.AssemblyReferences)
        {
            var reference = root.Reader.GetAssemblyReference(handle);
            var name = root.Reader.GetString(reference.Name);
            dependencies.Add(name + "|" + reference.Version);
            if (IsForbiddenAssembly(name)) violations.Add(new AdmissionViolation("<assembly>", "forbidden-dependency", name));
            else if (!IsKnownDependency(name)) violations.Add(new AdmissionViolation("<assembly>", "unknown-dependency-category", name));
        }
        return dependencies.OrderBy(static x => x, StringComparer.Ordinal).ToArray();
    }

    private static int Traverse(AssemblyModel rootModel, string root, string profile, IReadOnlyDictionary<string, AssemblyModel> models, List<AdmissionViolation> violations)
    {
        var rootHandle = rootModel.FindMethod(root) ?? throw new InvalidOperationException($"Admission root '{root}' was not found.");
        var queue = new Queue<MethodLocation>();
        var visited = new HashSet<MethodLocation>();
        queue.Enqueue(new MethodLocation(rootModel.Name, rootHandle.Value));

        while (queue.Count != 0)
        {
            var location = queue.Dequeue();
            if (!visited.Add(location)) continue;
            if (!models.TryGetValue(location.AssemblyName, out var model)) continue;
            var definition = model.Reader.GetMethodDefinition(location.Handle);
            var methodName = model.GetMethodDisplayName(location.Handle);
            if (definition.RelativeVirtualAddress == 0) continue;
            var body = model.PeReader.GetMethodBody(definition.RelativeVirtualAddress);
            var il = body.GetILBytes().ToArray();
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
                }

                if (instruction.MetadataToken is not int token || instruction.OpCode.OperandType != System.Reflection.Emit.OperandType.InlineMethod) continue;
                var target = model.ResolveMethod(token, models);
                if (target.DisplayName is not null && IsForbiddenApi(target.DisplayName))
                    violations.Add(new AdmissionViolation(methodName, "forbidden-api", target.DisplayName));
                if (target.Location is MethodLocation next) queue.Enqueue(next);
            }
        }
        return visited.Count;
    }

    private static bool IsForbiddenAssembly(string name) =>
        name == "System.Console" || name == "Microsoft.CSharp" || name.StartsWith("System.IO.", StringComparison.Ordinal) ||
        name.StartsWith("System.Net.", StringComparison.Ordinal) || name.StartsWith("System.Reflection.Emit", StringComparison.Ordinal);

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

    private readonly record struct MethodLocation(string AssemblyName, MethodDefinitionHandle Handle);

    private sealed class AssemblyModel : IDisposable
    {
        private readonly FileStream _stream;

        public AssemblyModel(string path)
        {
            Path = path;
            _stream = File.OpenRead(path);
            PeReader = new PEReader(_stream, PEStreamOptions.LeaveOpen);
            if (!PeReader.HasMetadata) throw new BadImageFormatException(path);
            Reader = PeReader.GetMetadataReader();
            Name = Reader.GetString(Reader.GetAssemblyDefinition().Name);
        }

        public string Path { get; }
        public string Name { get; }
        public PEReader PeReader { get; }
        public MetadataReader Reader { get; }

        public MethodDefinitionHandle? FindMethod(string identity)
        {
            var split = identity.Split(new[] { "::" }, 2, StringSplitOptions.None);
            if (split.Length != 2) throw new ArgumentException("Root must use Type::Method format.", nameof(identity));
            foreach (var typeHandle in Reader.TypeDefinitions)
            {
                var type = Reader.GetTypeDefinition(typeHandle);
                var fullName = FullTypeName(type);
                if (!string.Equals(fullName, split[0], StringComparison.Ordinal)) continue;
                foreach (var methodHandle in type.GetMethods())
                {
                    if (string.Equals(Reader.GetString(Reader.GetMethodDefinition(methodHandle).Name), split[1], StringComparison.Ordinal)) return methodHandle;
                }
            }
            return null;
        }

        public string GetMethodDisplayName(MethodDefinitionHandle handle)
        {
            var method = Reader.GetMethodDefinition(handle);
            var type = Reader.GetTypeDefinition(method.GetDeclaringType());
            return FullTypeName(type) + "::" + Reader.GetString(method.Name);
        }

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
            if (type.AssemblyName is null || type.FullName is null || !models.TryGetValue(type.AssemblyName, out var targetModel)) return (null, display);
            var local = targetModel.FindMethod(type.FullName + "::" + methodName);
            return local is null ? (null, display) : (new MethodLocation(targetModel.Name, local.Value), display);
        }

        private (string? AssemblyName, string? FullName) ResolveParentType(EntityHandle parent)
        {
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
            return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
        }

        public void Dispose()
        {
            PeReader.Dispose();
            _stream.Dispose();
        }
    }
}
