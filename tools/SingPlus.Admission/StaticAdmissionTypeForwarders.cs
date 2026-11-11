using System.Runtime.CompilerServices;
using SingPlus.Admission;
// Preserve the existing CLI assembly type identities; implementation has one library owner.
[assembly: TypeForwardedTo(typeof(AdmissionVerifier))]
[assembly: TypeForwardedTo(typeof(AdmissionViolation))]
[assembly: TypeForwardedTo(typeof(SingPlusAdmissionProofV1))]
[assembly: TypeForwardedTo(typeof(AdmissionVerificationResult))]
[assembly: TypeForwardedTo(typeof(ManagedCapPolicyDescriptor))]
[assembly: TypeForwardedTo(typeof(ManagedAsyncPeKind))]
[assembly: TypeForwardedTo(typeof(ManagedAsyncPeQualificationResult))]
[assembly: TypeForwardedTo(typeof(ManagedAsyncPeQualification))]
[assembly: TypeForwardedTo(typeof(ManagedAsyncAbi))]
[assembly: TypeForwardedTo(typeof(ManagedAsyncSemanticOperation))]
[assembly: TypeForwardedTo(typeof(ManagedAsyncFrontendContractV1))]
[assembly: TypeForwardedTo(typeof(ManagedAsyncAdmissionDisposition))]
[assembly: TypeForwardedTo(typeof(ManagedAsyncFrontendQualification))]
