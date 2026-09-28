#!/usr/bin/env bash
set -euo pipefail

lane="${1:-LocalAdapter}"
hybridcpu_root="${HYBRIDCPU_ROOT:-../HybridCPU v2}"
qualified_revision="9e001bf29df06ad3d4ff7337f81d4e5bc0a62fc9"
observed_revision="$(git -C "$hybridcpu_root" rev-parse --verify 'HEAD^{commit}')"
repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"

cd -- "$repository_root"
dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore \
  --filter 'FullyQualifiedName~DirectBoot|FullyQualifiedName~ProtectedBootStateMachineTests|FullyQualifiedName~HybridCpuBootAdapterTests|FullyQualifiedName~HybridBootInfoImporterTests|FullyQualifiedName~HybridBootProductionAdapterTests|FullyQualifiedName~HybridBootAuthorityAdmissionTests|FullyQualifiedName~RepositoryArchitecturePolicyTests|FullyQualifiedName~BootCapsuleAdmissionTests|FullyQualifiedName~PlatformBackendResetEpochTests' \
  --logger 'console;verbosity=minimal'

if [[ "$lane" != "LocalAdapter" ]]; then
  if [[ "$observed_revision" != "$qualified_revision" ]]; then
    printf 'ExternalBlocked: observed HybridCPU %s differs from qualified pin %s; requalification evidence is required.\n' "$observed_revision" "$qualified_revision" >&2
    exit 3
  fi
  printf 'ExternalBlocked: %s requires reset-loadable capsule, entry/bootstrap/reset/CXL evidence not supplied by this repository.\n' "$lane" >&2
  exit 4
fi

printf 'Direct SingNext local deterministic tests passed. HybridCPU observed=%s qualified=%s adapter-contours=AdapterQualified capsule-chain=ContractOnlyBlocked\n' "$observed_revision" "$qualified_revision"
