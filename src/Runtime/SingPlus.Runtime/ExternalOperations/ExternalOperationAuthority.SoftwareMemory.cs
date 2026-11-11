using System.Buffers.Binary;
using SingPlus.Contracts;

namespace SingPlus.Runtime;

public sealed partial class ExternalOperationAuthority
{
    internal KernelResult<OperationPreparation> PrepareSoftwareInitializedLoad(RegionOwner principal,
        OperationRegionUseRequest use, SoftwareSecureInitializedLoadIntent intent) =>
        PrepareCore(principal, [use], ExternalVisibilityRequirement.None, ExternalPublicationPolicy.Staged, null, null, intent);

    internal KernelResult ConfirmSoftwareInitializedLoadClosure(ExternalOperationHandle operation,
        SoftwareSecureInitializedLoadObservation proof)
    {
        lock (_gate)
        {
            var resolved = Resolve(operation);
            if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (!ReferenceEquals(record.InitializedLoad, proof.Intent) || !proof.ResourcesClosed ||
                record.InitializedLoadClosure is { } prior && !ReferenceEquals(prior, proof))
                return KernelResult.Fail(KernelError.ExternalEffectUncontained, "Original initialized-load closure required.");
            record.InitializedLoadClosure = proof;
            return KernelResult.Ok();
        }
    }

    internal KernelResult<ExternalOperationSnapshot> PublishSoftwareInitializedLoad(ExternalOperationHandle operation,
        SoftwareSecureInitializedLoadObservation proof, OperationDependencySnapshot dependencies,
        Action<ExternalPublicationDecisionV1> action,
        Func<Func<KernelResult<ExternalOperationSnapshot>>, KernelResult<ExternalOperationSnapshot>> sourceCommit,
        Func<Func<KernelResult<ExternalOperationSnapshot>>, KernelResult<ExternalOperationSnapshot>> sourceAdmission)
    {
        lock (_gate)
        {
            var resolved = Resolve(operation);
            if (!resolved.IsSuccess) return KernelResult<ExternalOperationSnapshot>.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (record.InitializedLoad is not { } original || !ReferenceEquals(original, proof.Intent) ||
                !ReferenceEquals(record.InitializedLoadClosure, proof) || !proof.ResourcesClosed ||
                proof.MachineAttempt == 0 || proof.Commit == 0 ||
                proof.Value != BinaryPrimitives.ReadUInt64LittleEndian(original.InitialBytes.Span))
                return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.PlatformDenied, "Original retired initialized-load proof required.");
        }
        return Publish(operation, dependencies, new(ExternalPublicationPolicy.Staged), action,
            sourceCommit, sourceAdmission, softwarePublication: true);
    }
}
