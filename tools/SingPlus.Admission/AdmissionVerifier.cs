using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace SingPlus.Admission;

public static class AdmissionVerifier
{
    private const string Ruleset = "SingPlusAdmissionRulesV14|Profiles:KernelNoHeapV10,ManagedCapV1,BootCapsuleV1|BootCapsule:reachable-noheap,exact-local-dependency-allowlist,native-inventory-deny|ManagedCap:full-local-closure-module-scan,positive-exact-framework-member-surface,unknown-member-deny,ambient-reference-static-recursive-value-closure-deny,native-inventory-deny|AssemblyIdentity:unique|ParentType:named-generic,nested,unsupported-reject|Root:unique-name|LocalCall:exact-metadata-signature-or-reject|KernelNoHeap:newobj,newarr,box,ldind,stind,ldobj,stobj,cpblk,initblk,localloc,calli,interop(PinvokeImpl,InternalCall,Unmanaged,NonCil,DllImport,LibraryImport,UnmanagedCallersOnly),explicit-layout-field,framework-memory-boundary|ConcreteReachableBody:required,AbstractRoot:deny|RuntimeAsyncV2:lowering-unavailable|ForbiddenApi:System.Console,System.Environment,System.GC,System.Activator,System.Threading.ThreadPool,System.Threading.Tasks.Task,System.Diagnostics.Process,System.IO.*,System.Net.*,System.Reflection.*,System.Linq.Expressions.*|ForbiddenAssemblies:System.Console,System.IO.*,System.Net.*,System.Reflection.Emit*,Microsoft.CSharp|UnknownDependency:deny|LocalSingPlusDependency:required,identity-match,raw-sha256,transitive";

    public static ManagedCapPolicyDescriptor GetManagedCapPolicyDescriptor()
    {
        var policy = ProfilePolicy.Resolve("ManagedCap");
        return new(policy.CanonicalIdentity, SingPlusAdmissionProofV1.Digest(Ruleset + "|" + policy.CanonicalIdentity),
            "framework-surface-v1", policy.FrameworkSurfaceDigest);
    }

    public static AdmissionVerificationResult Verify(string assemblyPath, string root, string profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);

        var fullPath = Path.GetFullPath(assemblyPath);
        var policy = ProfilePolicy.Resolve(profile);
        var models = LoadLocalAssemblies(fullPath);
        try
        {
            var rootModel = models.Values.FirstOrDefault(m => string.Equals(m.Path, fullPath, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Root assembly could not be loaded as managed metadata.");
            var assemblyDigest = rootModel.ContentDigest;
            var violations = new List<AdmissionViolation>();
            if (!policy.IsKnown)
                violations.Add(new AdmissionViolation(rootModel.Name, "unknown-profile-policy", profile));
            var closure = CollectDependencies(rootModel, models, policy, violations);
            var dependencyEvidence = closure.Evidence.Concat(CollectNativeInventory(fullPath, policy, violations));
            var dependencyDigest = SingPlusAdmissionProofV1.Digest(string.Join("\n", dependencyEvidence.OrderBy(static item => item, StringComparer.Ordinal)));
            var rulesetDigest = SingPlusAdmissionProofV1.Digest(Ruleset + "|" + policy.CanonicalIdentity);
            var reachable = policy.FullModuleScan
                ? ScanFullClosure(rootModel, root, policy, models, closure.AssemblyNames, violations)
                : Traverse(rootModel, root, policy, models, violations);
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
                if (!string.Equals(model.Path, rootPath, StringComparison.OrdinalIgnoreCase) &&
                    !IsLocalAdmissionAssemblyName(model.Name))
                {
                    model.Dispose();
                    continue;
                }
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

    private static DependencyClosure CollectDependencies(AssemblyModel root, IReadOnlyDictionary<string, AssemblyModel> models, ProfilePolicy policy, List<AdmissionViolation> violations)
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
                else if (!policy.IsDependencyAllowed(name)) violations.Add(new AdmissionViolation(model.Name, "unknown-dependency-category", name));
                else if (policy.PositiveFrameworkSurface && !hasLocal && !policy.IsFrameworkAssemblyVersionAllowed(name, reference.Version))
                    violations.Add(new AdmissionViolation(model.Name, "unclassified-framework-version", name + "|" + reference.Version));
                else if (policy.RequiresLocalDependency(name) && !hasLocal)
                    violations.Add(new AdmissionViolation(model.Name, "missing-local-dependency", name));
                else if (policy.RequiresLocalDependency(name) && hasLocal && local!.Version != reference.Version)
                    violations.Add(new AdmissionViolation(model.Name, "local-dependency-identity-mismatch", identity));
                if (hasLocal) pending.Enqueue(local!);
            }
        }
        return new DependencyClosure(dependencies.OrderBy(static x => x, StringComparer.Ordinal).ToArray(), visited);
    }

    private static int Traverse(AssemblyModel rootModel, string root, ProfilePolicy policy, IReadOnlyDictionary<string, AssemblyModel> models, List<AdmissionViolation> violations)
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
            foreach (var next in ScanMethod(rootModel, rootHandle, model, location.Handle, policy, models, violations))
                queue.Enqueue(next);
        }
        return visited.Count;
    }

    private static int ScanFullClosure(AssemblyModel rootModel, string root, ProfilePolicy policy,
        IReadOnlyDictionary<string, AssemblyModel> models, IReadOnlySet<string> closure, List<AdmissionViolation> violations)
    {
        var rootHandle = rootModel.FindMethod(root, requireUnique: true) ?? throw new InvalidOperationException($"Admission root '{root}' was not found.");
        var count = 0;
        foreach (var assemblyName in closure.OrderBy(static name => name, StringComparer.Ordinal))
        {
            if (!models.TryGetValue(assemblyName, out var model)) continue;
            ScanManagedCapMetadata(model, policy, violations);
            ScanStaticState(model, violations);
            foreach (var handle in model.Reader.MethodDefinitions)
            {
                count++;
                _ = ScanMethod(rootModel, rootHandle, model, handle, policy, models, violations);
            }
        }
        return count;
    }

    private static IReadOnlyList<MethodLocation> ScanMethod(AssemblyModel rootModel, MethodDefinitionHandle rootHandle,
        AssemblyModel model, MethodDefinitionHandle handle, ProfilePolicy policy,
        IReadOnlyDictionary<string, AssemblyModel> models, List<AdmissionViolation> violations)
    {
        var targets = new List<MethodLocation>();
        var definition = model.Reader.GetMethodDefinition(handle);
        var methodName = model.GetMethodDisplayName(handle);
        if (policy.EnforceRestrictedCil && ManagedAsyncPeQualification.HasRuntimeAsyncMarker(definition))
            violations.Add(new AdmissionViolation(methodName, "unsupported-async-abi", "Runtime Async V2 lowering is unavailable"));
        if (policy.EnforceRestrictedCil && model.IsInteropMethod(definition))
            violations.Add(new AdmissionViolation(methodName, "interop-boundary", "Unmanaged method boundary"));
        if (definition.RelativeVirtualAddress == 0)
        {
            if (policy.EnforceRestrictedCil && ((definition.Attributes & System.Reflection.MethodAttributes.Abstract) == 0 ||
                (model.Name == rootModel.Name && handle == rootHandle)))
                violations.Add(new AdmissionViolation(methodName, "unavailable-method-body", "Audited method has no CIL body"));
            return targets;
        }
        var body = model.PeReader.GetMethodBody(definition.RelativeVirtualAddress);
        var il = body.GetILBytes()?.ToArray() ?? Array.Empty<byte>();
        foreach (var instruction in IlReader.Read(il))
        {
            if (policy.EnforceRestrictedCil)
            {
                if (instruction.OpCode == System.Reflection.Emit.OpCodes.Newobj && policy.ForbidAllocation)
                    violations.Add(new AdmissionViolation(methodName, "newobj", "managed/object construction"));
                else if (instruction.OpCode == System.Reflection.Emit.OpCodes.Newarr && policy.ForbidAllocation)
                    violations.Add(new AdmissionViolation(methodName, "newarr", "managed array allocation"));
                else if (instruction.OpCode == System.Reflection.Emit.OpCodes.Box && policy.ForbidAllocation)
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
            if (policy.EnforceRestrictedCil && target.DisplayName is not null && IsFrameworkMemoryBoundary(target.DisplayName))
                violations.Add(new AdmissionViolation(methodName, "framework-memory-boundary", target.DisplayName));
            if (policy.PositiveFrameworkSurface && target.Location is null && target.DisplayName is not null &&
                !policy.IsFrameworkMemberAllowed(target.AssemblyName, target.DisplayName, target.Signature))
                violations.Add(new AdmissionViolation(methodName, "unclassified-framework-member", target.CanonicalIdentity));
            if (target.Location is MethodLocation next) targets.Add(next);
        }
        return targets;
    }

    private static void ScanStaticState(AssemblyModel model, List<AdmissionViolation> violations)
    {
        foreach (var handle in model.Reader.FieldDefinitions)
        {
            var field = model.Reader.GetFieldDefinition(handle);
            if ((field.Attributes & System.Reflection.FieldAttributes.Static) == 0 ||
                (field.Attributes & System.Reflection.FieldAttributes.Literal) != 0) continue;
            var signature = model.Reader.GetBlobBytes(field.Signature);
            if (SignatureCanCarryReference(signature) || model.FieldValueTypeCanCarryReference(handle))
                violations.Add(new AdmissionViolation(model.GetFieldDisplayName(handle), "ambient-mutable-static-reference", Convert.ToHexString(signature).ToLowerInvariant()));
        }
    }

    private static void ScanManagedCapMetadata(AssemblyModel model, ProfilePolicy policy, List<AdmissionViolation> violations)
    {
        if (!policy.PositiveFrameworkSurface) return;
        foreach (var identity in model.ExternalTypeReferences())
            if (!policy.IsFrameworkTypeAllowed(identity.AssemblyName, identity.FullName))
                violations.Add(new AdmissionViolation(model.Name, "unclassified-framework-type", identity.AssemblyName + "|" + identity.FullName));
    }

    private static bool SignatureCanCarryReference(ReadOnlySpan<byte> signature)
    {
        // FIELD (0x06), optional custom modifiers, then ECMA-335 element type.
        for (var i = 1; i < signature.Length; i++)
            if (signature[i] is 0x0e or 0x12 or 0x14 or 0x15 or 0x1c or 0x1d) return true;
        return false;
    }

    private static IEnumerable<string> CollectNativeInventory(string rootPath, ProfilePolicy policy, List<AdmissionViolation> violations)
    {
        if (!policy.RejectUndeclaredNativeAssets) yield break;
        var root = Path.GetFullPath(rootPath);
        var declaredRuntimeAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dependencyManifest = Path.ChangeExtension(root, ".deps.json");
        if (File.Exists(dependencyManifest))
        {
            var manifestBytes = File.ReadAllBytes(dependencyManifest);
            yield return "deps|" + SingPlusAdmissionProofV1.Digest(manifestBytes);
            using var document = JsonDocument.Parse(manifestBytes);
            if (document.RootElement.TryGetProperty("targets", out var targets))
                foreach (var target in targets.EnumerateObject())
                    foreach (var library in target.Value.EnumerateObject())
                        if (library.Name.StartsWith("runtimepack.Microsoft.NETCore.App.Runtime.", StringComparison.Ordinal) &&
                            library.Value.TryGetProperty("native", out var native))
                            foreach (var asset in native.EnumerateObject())
                                declaredRuntimeAssets.Add(Path.GetFileName(asset.Name));
        }
        foreach (var path in Directory.EnumerateFiles(Path.GetDirectoryName(root)!).OrderBy(static p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(Path.GetFullPath(path), root, StringComparison.OrdinalIgnoreCase)) continue;
            var extension = Path.GetExtension(path);
            if (extension is not (".dll" or ".so" or ".dylib")) continue;
            try
            {
                using var stream = File.OpenRead(path);
                using var pe = new PEReader(stream);
                if (pe.HasMetadata) continue;
            }
            catch (BadImageFormatException) { }
            var name = Path.GetFileName(path);
            if (declaredRuntimeAssets.Contains(name))
            {
                yield return "runtime-native|" + name + "|" + SingPlusAdmissionProofV1.Digest(File.ReadAllBytes(path));
                continue;
            }
            violations.Add(new AdmissionViolation("native-inventory", "undeclared-native-asset", name));
            yield return "native|" + name + "|" + SingPlusAdmissionProofV1.Digest(File.ReadAllBytes(path));
        }
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

    private static bool IsLocalAdmissionAssemblyName(string name) =>
        name.StartsWith("SingPlus.", StringComparison.Ordinal) ||
        name.StartsWith("SingNext.Boot.", StringComparison.Ordinal) ||
        name == "HybridCpu.Boot.Contracts";

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

    private sealed record ProfilePolicy(
        string Name,
        string CanonicalIdentity,
        bool IsKnown,
        bool FullModuleScan,
        bool EnforceRestrictedCil,
        bool ForbidAllocation,
        bool PositiveFrameworkSurface,
        bool RequireLocalSingPlusDependencies,
        bool RejectUndeclaredNativeAssets)
    {
        private static readonly HashSet<string> ManagedCapMembers = new(StringComparer.Ordinal)
        {
            "System.Private.CoreLib|System.Object::.ctor|200001",
            "System.Private.CoreLib|System.Math::Abs|00010808",
            "System.Private.CoreLib|System.Math::Abs|0001080a",
            "System.Private.CoreLib|System.Math::Abs|0001080c"
        };

        private static readonly HashSet<string> ManagedCapTypes = new(StringComparer.Ordinal)
        {
            "System.Object",
            "System.Math",
            "System.Attribute",
            "System.Runtime.CompilerServices.CompilationRelaxationsAttribute",
            "System.Runtime.CompilerServices.RuntimeCompatibilityAttribute",
            "System.Runtime.CompilerServices.RefSafetyRulesAttribute",
            "System.Diagnostics.DebuggableAttribute",
            "System.Diagnostics.DebuggableAttribute+DebuggingModes",
            "System.Reflection.AssemblyCompanyAttribute",
            "System.Reflection.AssemblyConfigurationAttribute",
            "System.Reflection.AssemblyFileVersionAttribute",
            "System.Reflection.AssemblyInformationalVersionAttribute",
            "System.Reflection.AssemblyProductAttribute",
            "System.Reflection.AssemblyTitleAttribute",
            "System.Runtime.Versioning.TargetFrameworkAttribute"
        };

        public static ProfilePolicy Resolve(string profile) => profile switch
        {
            "KernelNoHeap" => new(profile, "KernelNoHeapV10|net11", true, false, true, true, false, true, false),
            "ManagedCap" => new(profile, "ManagedCapV1|net11|framework-surface-v1", true, true, true, false, true, true, true),
            "BootCapsule" => new(profile, "BootCapsuleV1|net11|reachable-noheap|exact-dependencies", true, false, true, true, false, true, true),
            _ => new(profile, "UnknownProfile|deny", false, false, true, true, true, true, true)
        };

        public bool IsDependencyAllowed(string name)
        {
            if (Name != "BootCapsule") return IsKnownDependency(name);
            return name is "mscorlib" or "netstandard" or "System.Private.CoreLib" or "System.Runtime" or
                "SingNext.Boot.Core" or "SingPlus.Platform.HybridCpu.Boot" or "HybridCpu.Boot.Contracts" or "SingNext.Boot.Capsule";
        }

        public bool RequiresLocalDependency(string name)
        {
            if (!RequireLocalSingPlusDependencies) return false;
            if (Name == "BootCapsule")
                return name is "SingNext.Boot.Core" or "SingPlus.Platform.HybridCpu.Boot" or "HybridCpu.Boot.Contracts" or "SingNext.Boot.Capsule";
            return name.StartsWith("SingPlus.", StringComparison.Ordinal);
        }

        public bool IsFrameworkMemberAllowed(string? assemblyName, string displayName, string signature)
        {
            if (assemblyName is null) return false;
            // Calls into the local SingPlus closure are resolved to a MethodLocation. An
            // unresolved SingPlus member is never admitted as a framework surface.
            return ManagedCapMembers.Contains(assemblyName + "|" + displayName + "|" + signature);
        }

        public bool IsFrameworkTypeAllowed(string? assemblyName, string? fullName) =>
            assemblyName is "System.Private.CoreLib" or "System.Runtime" && fullName is not null && ManagedCapTypes.Contains(fullName);

        public bool IsFrameworkAssemblyVersionAllowed(string name, Version version) =>
            (name == "System.Private.CoreLib" || name.StartsWith("System.", StringComparison.Ordinal)) && version.Major == 11;

        public string FrameworkSurfaceDigest => SingPlusAdmissionProofV1.Digest(string.Join("\n",
            ManagedCapMembers.Select(static item => "M|" + item).Concat(ManagedCapTypes.Select(static item => "T|System.Private.CoreLib-or-System.Runtime|" + item))
                .OrderBy(static item => item, StringComparer.Ordinal)));
    }

    private sealed record DependencyClosure(string[] Evidence, IReadOnlySet<string> AssemblyNames);

    private readonly record struct MethodLocation(string AssemblyName, MethodDefinitionHandle Handle);

    private readonly record struct ResolvedMethod(MethodLocation? Location, string? AssemblyName, string? DisplayName, string Signature)
    {
        public string CanonicalIdentity => (AssemblyName ?? "unknown") + "|" + (DisplayName ?? "unknown") + "|" + Signature;
    }

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

        public MethodDefinitionHandle? FindMethod(string identity, string signature)
        {
            var split = identity.Split(new[] { "::" }, 2, StringSplitOptions.None);
            if (split.Length != 2) throw new ArgumentException("Method must use Type::Method format.", nameof(identity));
            MethodDefinitionHandle? match = null;
            MethodDefinitionHandle? uniqueNameMatch = null;
            var nameMatchCount = 0;
            foreach (var typeHandle in Reader.TypeDefinitions)
            {
                var type = Reader.GetTypeDefinition(typeHandle);
                if (!string.Equals(FullTypeName(type), split[0], StringComparison.Ordinal)) continue;
                foreach (var methodHandle in type.GetMethods())
                {
                    var method = Reader.GetMethodDefinition(methodHandle);
                    if (!string.Equals(Reader.GetString(method.Name), split[1], StringComparison.Ordinal)) continue;
                    nameMatchCount++;
                    uniqueNameMatch = methodHandle;
                    if (!string.Equals(SignatureHex(method.Signature), signature, StringComparison.Ordinal)) continue;
                    if (match is not null)
                        throw new InvalidOperationException($"Admission method identity '{identity}|{signature}' is ambiguous.");
                    match = methodHandle;
                }
            }
            if (match is not null) return match;
            // TypeDefOrRef indices are local to a metadata image, so otherwise-identical
            // Span/ref-struct signatures can have different raw blobs across assemblies.
            // A unique same-named target still selects exactly one body; overloads remain
            // fail-closed and require exact signature-qualified resolution.
            return nameMatchCount == 1 ? uniqueNameMatch : null;
        }

        public string GetMethodDisplayName(MethodDefinitionHandle handle)
        {
            var method = Reader.GetMethodDefinition(handle);
            var type = Reader.GetTypeDefinition(method.GetDeclaringType());
            return FullTypeName(type) + "::" + Reader.GetString(method.Name);
        }

        public string GetFieldDisplayName(FieldDefinitionHandle handle)
        {
            var field = Reader.GetFieldDefinition(handle);
            var type = Reader.GetTypeDefinition(field.GetDeclaringType());
            return FullTypeName(type) + "::" + Reader.GetString(field.Name);
        }

        public IEnumerable<(string? AssemblyName, string? FullName)> ExternalTypeReferences()
        {
            foreach (var handle in Reader.TypeReferences)
            {
                var identity = ResolveParentType(handle);
                if (!string.Equals(identity.AssemblyName, Name, StringComparison.OrdinalIgnoreCase)) yield return identity;
            }
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

        public bool FieldValueTypeCanCarryReference(FieldDefinitionHandle handle) =>
            FieldValueTypeCanCarryReference(handle, new HashSet<TypeDefinitionHandle>());

        private bool FieldValueTypeCanCarryReference(
            FieldDefinitionHandle handle,
            HashSet<TypeDefinitionHandle> path)
        {
            var field = Reader.GetFieldDefinition(handle);
            var blob = Reader.GetBlobReader(field.Signature);
            _ = blob.ReadSignatureHeader();
            if (blob.RemainingBytes == 0) return true;
            var elementType = blob.ReadByte();
            if (elementType != 0x11) return false; // ECMA-335 ELEMENT_TYPE_VALUETYPE

            EntityHandle typeHandle;
            try { typeHandle = blob.ReadTypeHandle(); }
            catch (BadImageFormatException) { return true; }
            if (typeHandle.Kind != HandleKind.TypeDefinition) return true;

            var definitionHandle = (TypeDefinitionHandle)typeHandle;
            if (!path.Add(definitionHandle)) return true;
            var definition = Reader.GetTypeDefinition(definitionHandle);
            foreach (var nestedFieldHandle in definition.GetFields())
            {
                var nestedField = Reader.GetFieldDefinition(nestedFieldHandle);
                if ((nestedField.Attributes & System.Reflection.FieldAttributes.Static) != 0) continue;
                var signature = Reader.GetBlobBytes(nestedField.Signature);
                if (SignatureCanCarryReference(signature) ||
                    FieldValueTypeCanCarryReference(nestedFieldHandle, path))
                {
                    path.Remove(definitionHandle);
                    return true;
                }
            }
            path.Remove(definitionHandle);
            return false;
        }

        public ResolvedMethod ResolveMethod(int token, IReadOnlyDictionary<string, AssemblyModel> models)
        {
            EntityHandle handle;
            try { handle = MetadataTokens.EntityHandle(token); }
            catch (ArgumentException) { return new(null, null, null, string.Empty); }

            if (handle.Kind == HandleKind.MethodDefinition)
            {
                var method = (MethodDefinitionHandle)handle;
                var definition = Reader.GetMethodDefinition(method);
                return new(new MethodLocation(Name, method), Name, GetMethodDisplayName(method), SignatureHex(definition.Signature));
            }
            if (handle.Kind == HandleKind.MethodSpecification)
            {
                var spec = Reader.GetMethodSpecification((MethodSpecificationHandle)handle);
                return ResolveEntityMethod(spec.Method, models);
            }
            if (handle.Kind == HandleKind.MemberReference)
                return ResolveMemberReference((MemberReferenceHandle)handle, models);
            return new(null, null, null, string.Empty);
        }

        private ResolvedMethod ResolveEntityMethod(EntityHandle handle, IReadOnlyDictionary<string, AssemblyModel> models)
        {
            if (handle.Kind == HandleKind.MethodDefinition)
            {
                var method = (MethodDefinitionHandle)handle;
                var definition = Reader.GetMethodDefinition(method);
                return new(new MethodLocation(Name, method), Name, GetMethodDisplayName(method), SignatureHex(definition.Signature));
            }
            if (handle.Kind == HandleKind.MemberReference) return ResolveMemberReference((MemberReferenceHandle)handle, models);
            return new(null, null, null, string.Empty);
        }

        private ResolvedMethod ResolveMemberReference(MemberReferenceHandle handle, IReadOnlyDictionary<string, AssemblyModel> models)
        {
            var member = Reader.GetMemberReference(handle);
            var methodName = Reader.GetString(member.Name);
            var type = ResolveParentType(member.Parent);
            var display = type.FullName is null ? methodName : type.FullName + "::" + methodName;
            if (type.AssemblyName is null || type.FullName is null)
                throw new InvalidOperationException($"Unsupported method parent for '{display}'.");
            var signature = SignatureHex(member.Signature);
            if (type.AssemblyName == "mscorlib" || type.AssemblyName == "netstandard" ||
                type.AssemblyName.StartsWith("System.", StringComparison.Ordinal))
                return new(null, type.AssemblyName, display, signature);
            if (!models.TryGetValue(type.AssemblyName, out var targetModel)) return new(null, type.AssemblyName, display, signature);
            // Metadata signatures are required here: selecting a same-named overload would widen the audited call graph.
            var local = targetModel.FindMethod(type.FullName + "::" + methodName, signature);
            if (local is null) throw new InvalidOperationException($"Unresolved signature-qualified local call target '{display}|{signature}'.");
            return new(new MethodLocation(targetModel.Name, local.Value), targetModel.Name, display, signature);
        }

        private string SignatureHex(BlobHandle handle) => Convert.ToHexString(Reader.GetBlobBytes(handle)).ToLowerInvariant();

        private (string? AssemblyName, string? FullName) ResolveParentType(EntityHandle parent)
        {
            if (parent.Kind == HandleKind.TypeSpecification)
            {
                var typeSignature = Reader.GetTypeSpecification((TypeSpecificationHandle)parent).Signature;
                var blob = Reader.GetBlobReader(typeSignature);
                var typeCode = blob.ReadSignatureTypeCode();
                if (typeCode is SignatureTypeCode.Array or SignatureTypeCode.SZArray)
                    return ("System.Private.CoreLib", "System.Array");
                // A constructed named type has the same definition body for every type argument.
                // Other TypeSpec parents require explicit importer support, never display-only admission.
                if (typeCode != SignatureTypeCode.GenericTypeInstance ||
                    blob.ReadSignatureTypeCode() != SignatureTypeCode.TypeHandle)
                    throw new InvalidOperationException($"Unsupported TypeSpecification method/field parent '{SignatureHex(typeSignature)}'.");
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

public sealed record ManagedCapPolicyDescriptor(
    string AdmissionPolicyVersion,
    string AdmissionPolicyDigest,
    string FrameworkSurfaceVersion,
    string FrameworkSurfaceDigest);
