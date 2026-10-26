using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace SingPlus.Admission;

public enum ManagedAsyncPeKind : byte
{
    NotAsync,
    LegacyCilStateMachine,
    RuntimeAsyncV2,
    Rejected
}

public readonly record struct ManagedAsyncPeQualificationResult(ManagedAsyncPeKind Kind, string Reason)
{
    public ManagedAsyncAdmissionDisposition AdmissionDisposition => Kind switch
    {
        ManagedAsyncPeKind.LegacyCilStateMachine => ManagedAsyncAdmissionDisposition.LegacyCompatible,
        ManagedAsyncPeKind.RuntimeAsyncV2 => ManagedAsyncAdmissionDisposition.RecognizedButLoweringUnavailable,
        _ => ManagedAsyncAdmissionDisposition.Rejected
    };
}

public static class ManagedAsyncPeQualification
{
    // Draft ECMA-335 runtime-async MethodImpl flag. Recognition only: no importer/lowering authority.
    private const ushort RuntimeAsyncMethodImplFlag = 0x2000;
    private const ushort InvalidRuntimeAsyncImplMask = 0x27; // non-CIL code type, unmanaged, or synchronized

    // Reused by admission against its existing byte snapshot; even malformed marked
    // methods require an unsupported frontend ABI and cannot bypass the lowering gate.
    internal static bool HasRuntimeAsyncMarker(MethodDefinition method) =>
        ((ushort)method.ImplAttributes & RuntimeAsyncMethodImplFlag) != 0;

    public static ManagedAsyncPeQualificationResult Recognize(string assemblyPath, string methodIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(methodIdentity);
        try
        {
            return RecognizeCore(assemblyPath, methodIdentity);
        }
        catch (BadImageFormatException)
        {
            return new(ManagedAsyncPeKind.Rejected, "Assembly contains invalid PE or managed metadata.");
        }
    }

    private static ManagedAsyncPeQualificationResult RecognizeCore(string assemblyPath, string methodIdentity)
    {
        string[] split = methodIdentity.Split(new[] { "::" }, 2, StringSplitOptions.None);
        if (split.Length != 2 || split.Any(string.IsNullOrWhiteSpace))
            return new(ManagedAsyncPeKind.Rejected, "Method identity must be Type::Method.");

        using FileStream stream = File.OpenRead(assemblyPath);
        using PEReader pe = new PEReader(stream);
        if (!pe.HasMetadata) return new(ManagedAsyncPeKind.Rejected, "Assembly has no managed metadata.");
        MetadataReader reader = pe.GetMetadataReader();
        MethodDefinitionHandle? match = null;
        foreach (TypeDefinitionHandle typeHandle in reader.TypeDefinitions)
        {
            TypeDefinition type = reader.GetTypeDefinition(typeHandle);
            string fullName = FullTypeName(reader, typeHandle);
            if (!string.Equals(fullName, split[0], StringComparison.Ordinal)) continue;
            foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
            {
                if (!string.Equals(reader.GetString(reader.GetMethodDefinition(methodHandle).Name), split[1], StringComparison.Ordinal)) continue;
                if (match is not null) return new(ManagedAsyncPeKind.Rejected, "Method identity is overloaded; use a signature-qualified importer hook.");
                match = methodHandle;
            }
        }
        if (match is null) return new(ManagedAsyncPeKind.Rejected, "Method was not found in the assembly.");
        MethodDefinition definition = reader.GetMethodDefinition(match.Value);
        bool runtimeAsync = HasRuntimeAsyncMarker(definition);
        CustomAttributeHandle[] legacyAttributes = definition.GetCustomAttributes()
            .Where(handle => IsAsyncStateMachineAttribute(reader, handle)).ToArray();
        if (legacyAttributes.Any(handle => !IsTrustedAsyncStateMachineAttribute(reader, handle)))
            return new(ManagedAsyncPeKind.Rejected, "Async state-machine marker originates outside the framework contract.");
        bool legacy = legacyAttributes.Length > 0;
        if (runtimeAsync && legacy) return new(ManagedAsyncPeKind.Rejected, "Conflicting runtime-async and legacy state-machine metadata.");
        if (runtimeAsync)
        {
            if (definition.RelativeVirtualAddress == 0 || ((ushort)definition.ImplAttributes & InvalidRuntimeAsyncImplMask) != 0 ||
                (definition.Attributes & System.Reflection.MethodAttributes.PinvokeImpl) != 0 ||
                !HasSupportedRuntimeAsyncReturn(reader, definition))
                return new(ManagedAsyncPeKind.Rejected, "Runtime Async V2 MethodImpl flag has an invalid CIL method or return signature.");
            return new(ManagedAsyncPeKind.RuntimeAsyncV2, "Runtime Async V2 metadata recognized; lowering unavailable.");
        }
        if (legacy)
        {
            if (legacyAttributes.Length != 1 || definition.RelativeVirtualAddress == 0 ||
                ((ushort)definition.ImplAttributes & InvalidRuntimeAsyncImplMask) != 0 ||
                (definition.Attributes & System.Reflection.MethodAttributes.PinvokeImpl) != 0 ||
                !HasValidLegacyStateMachine(reader, legacyAttributes[0]))
                return new(ManagedAsyncPeKind.Rejected, "Legacy async attribute has no valid local CIL state machine.");
            return new(ManagedAsyncPeKind.LegacyCilStateMachine, "Legacy CIL state machine recognized.");
        }
        return new(ManagedAsyncPeKind.NotAsync, "Method has no recognized async ABI metadata.");
    }

    private static bool IsAsyncStateMachineAttribute(MetadataReader reader, CustomAttributeHandle handle)
    {
        CustomAttribute attribute = reader.GetCustomAttribute(handle);
        if (attribute.Constructor.Kind != HandleKind.MemberReference) return false;
        MemberReference constructor = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
        if (constructor.Parent.Kind != HandleKind.TypeReference) return false;
        TypeReference type = reader.GetTypeReference((TypeReferenceHandle)constructor.Parent);
        return reader.GetString(type.Namespace) == "System.Runtime.CompilerServices" &&
            reader.GetString(type.Name) == "AsyncStateMachineAttribute";
    }

    private static bool IsTrustedAsyncStateMachineAttribute(MetadataReader reader, CustomAttributeHandle handle)
    {
        CustomAttribute attribute = reader.GetCustomAttribute(handle);
        MemberReference constructor = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
        return IsKnownFrameworkReference(reader, reader.GetTypeReference((TypeReferenceHandle)constructor.Parent));
    }

    private static bool HasValidLegacyStateMachine(MetadataReader reader, CustomAttributeHandle handle)
    {
        string? serializedType;
        try
        {
            BlobReader blob = reader.GetBlobReader(reader.GetCustomAttribute(handle).Value);
            if (blob.ReadUInt16() != 1) return false;
            serializedType = blob.ReadSerializedString();
        }
        catch (BadImageFormatException)
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(serializedType)) return false;
        string localAssembly = reader.GetString(reader.GetAssemblyDefinition().Name);
        foreach (TypeDefinitionHandle typeHandle in reader.TypeDefinitions)
        {
            TypeDefinition type = reader.GetTypeDefinition(typeHandle);
            string fullName = FullTypeName(reader, typeHandle);
            if (!IsLocalSerializedType(serializedType, fullName, localAssembly)) continue;
            bool implementsAsync = type.GetInterfaceImplementations().Any(interfaceHandle =>
            {
                EntityHandle implemented = reader.GetInterfaceImplementation(interfaceHandle).Interface;
                if (implemented.Kind != HandleKind.TypeReference) return false;
                TypeReference reference = reader.GetTypeReference((TypeReferenceHandle)implemented);
                return reader.GetString(reference.Namespace) == "System.Runtime.CompilerServices" &&
                    reader.GetString(reference.Name) == "IAsyncStateMachine" &&
                    IsKnownFrameworkReference(reader, reference);
            });
            if (!implementsAsync) return false;
            return type.GetMethods().Any(methodHandle =>
            {
                MethodDefinition method = reader.GetMethodDefinition(methodHandle);
                return reader.GetString(method.Name) == "MoveNext" && method.RelativeVirtualAddress != 0;
            });
        }
        return false;
    }

    private static bool IsLocalSerializedType(string serializedType, string fullName, string assemblyName)
    {
        if (string.Equals(serializedType, fullName, StringComparison.Ordinal)) return true;
        if (!serializedType.StartsWith(fullName + ",", StringComparison.Ordinal)) return false;
        string assemblyIdentity = serializedType[(fullName.Length + 1)..].TrimStart();
        int comma = assemblyIdentity.IndexOf(',');
        string claimedAssembly = comma < 0 ? assemblyIdentity : assemblyIdentity[..comma];
        return string.Equals(claimedAssembly, assemblyName, StringComparison.Ordinal);
    }

    private static string FullTypeName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        TypeDefinition type = reader.GetTypeDefinition(handle);
        string name = reader.GetString(type.Name);
        if (type.GetDeclaringType() is { IsNil: false } parent)
            return FullTypeName(reader, parent) + "+" + name;
        string ns = reader.GetString(type.Namespace);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    private static bool IsKnownFrameworkReference(MetadataReader reader, TypeReference type)
    {
        if (type.ResolutionScope.Kind != HandleKind.AssemblyReference) return false;
        AssemblyReference assembly = reader.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope);
        return reader.GetString(assembly.Name) is "System.Runtime" or "System.Private.CoreLib" or
            "mscorlib" or "netstandard";
    }

    private static bool HasSupportedRuntimeAsyncReturn(MetadataReader reader, MethodDefinition method)
    {
        try
        {
            BlobReader blob = reader.GetBlobReader(method.Signature);
            SignatureHeader header = blob.ReadSignatureHeader();
            if (header.CallingConvention != SignatureCallingConvention.Default) return false;
            if (header.IsGeneric) blob.ReadCompressedInteger();
            blob.ReadCompressedInteger(); // parameter count
            SignatureTypeCode code = blob.ReadSignatureTypeCode();
            bool generic = code == SignatureTypeCode.GenericTypeInstance;
            if (generic) code = blob.ReadSignatureTypeCode();
            if (code != SignatureTypeCode.TypeHandle) return false;
            EntityHandle handle = blob.ReadTypeHandle();
            if (handle.Kind != HandleKind.TypeReference) return false;
            TypeReference type = reader.GetTypeReference((TypeReferenceHandle)handle);
            string ns = reader.GetString(type.Namespace);
            string name = reader.GetString(type.Name);
            if (!IsKnownTaskReference(reader, type)) return false;
            return ns == "System.Threading.Tasks" && (generic
                ? name is "Task`1" or "ValueTask`1"
                : name is "Task" or "ValueTask");
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }

    private static bool IsKnownTaskReference(MetadataReader reader, TypeReference type)
    {
        if (IsKnownFrameworkReference(reader, type)) return true;
        if (type.ResolutionScope.Kind != HandleKind.AssemblyReference) return false;
        AssemblyReference assembly = reader.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope);
        return reader.GetString(assembly.Name) == "System.Threading.Tasks";
    }
}
