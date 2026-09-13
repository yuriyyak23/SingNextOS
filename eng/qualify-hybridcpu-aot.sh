#!/usr/bin/env bash

set -euo pipefail

readonly expected_hybridcpu_revision="9e001bf29df06ad3d4ff7337f81d4e5bc0a62fc9"
readonly expected_dotnet_sdk="10.0.204"
readonly script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
readonly sing_repository="$(cd -- "${script_directory}/.." && pwd -P)"

if [[ $# -ne 1 ]]; then
  echo "Usage: eng/qualify-hybridcpu-aot.sh <HybridCPU-v2-repository>" >&2
  exit 64
fi

readonly hybridcpu_repository="$(cd -- "$1" && pwd -P)"
readonly output_directory="${sing_repository}/artifacts/hybridcpu-aot-qualification"
readonly first_pass_directory="${output_directory}/pass1"
readonly kernel_assembly="${sing_repository}/src/Kernel/SingPlus.Kernel/bin/Release/net10.0/SingPlus.Kernel.dll"
readonly boot_assembly="${sing_repository}/src/Kernel/Boot/SingPlus.Boot/bin/Release/net10.0/SingPlus.Boot.dll"
readonly first_kernel_assembly="${first_pass_directory}/SingPlus.Kernel.dll"
readonly first_boot_assembly="${first_pass_directory}/SingPlus.Boot.dll"
readonly first_admission_proof="${first_pass_directory}/SingPlusAdmissionProofV1.json"
readonly admission_proof="${output_directory}/SingPlusAdmissionProofV1.json"
readonly qualification_report="${output_directory}/SingPlusHybridCpuQualificationV1.json"
readonly qualification_checksums="${output_directory}/SHA256SUMS"
readonly boot_project="src/Kernel/Boot/SingPlus.Boot/SingPlus.Boot.csproj"
readonly qualification_project="tools/SingPlus.HybridCpuQualification/SingPlus.HybridCpuQualification.csproj"
readonly admission_tool="tools/SingPlus.Admission/bin/Release/net10.0/SingPlus.Admission.dll"
readonly qualification_tool="tools/SingPlus.HybridCpuQualification/bin/Release/net10.0/SingPlus.HybridCpuQualification.dll"

if [[ "$(git -C "${sing_repository}" rev-parse --show-toplevel)" != "${sing_repository}" ]]; then
  echo "SingNextOS path is not the exact Git worktree root." >&2
  exit 2
fi

if [[ "$(git -C "${hybridcpu_repository}" rev-parse --show-toplevel)" != "${hybridcpu_repository}" ]]; then
  echo "HybridCPU-v2 path is not the exact Git worktree root." >&2
  exit 2
fi

readonly hybridcpu_revision="$(git -C "${hybridcpu_repository}" rev-parse --verify 'HEAD^{commit}')"
if [[ "${hybridcpu_revision}" != "${expected_hybridcpu_revision}" ]]; then
  echo "HybridCPU-v2 HEAD ${hybridcpu_revision} does not match ${expected_hybridcpu_revision}." >&2
  exit 2
fi

cd -- "${sing_repository}"

readonly actual_dotnet_sdk="$(dotnet --version)"
if [[ "${actual_dotnet_sdk}" != "${expected_dotnet_sdk}" ]]; then
  echo ".NET SDK ${actual_dotnet_sdk} does not match ${expected_dotnet_sdk}." >&2
  exit 2
fi

mkdir -p -- "${first_pass_directory}"
rm -f -- \
  "${first_kernel_assembly}" \
  "${first_boot_assembly}" \
  "${first_admission_proof}" \
  "${admission_proof}" \
  "${qualification_report}" \
  "${qualification_checksums}"

dotnet restore "${boot_project}"
dotnet restore "${qualification_project}"
dotnet build "${qualification_project}" \
  --configuration Release \
  --no-restore \
  -p:ContinuousIntegrationBuild=true

dotnet clean "${boot_project}" --configuration Release
dotnet build "${boot_project}" \
  --configuration Release \
  --no-restore \
  -p:ContinuousIntegrationBuild=true
dotnet "${admission_tool}" verify \
  --assembly "${kernel_assembly}" \
  --root SingPlus.Kernel.KernelEntryPoint::Run \
  --profile KernelNoHeap \
  --proof "${admission_proof}"
cp -- "${kernel_assembly}" "${first_kernel_assembly}"
cp -- "${boot_assembly}" "${first_boot_assembly}"
cp -- "${admission_proof}" "${first_admission_proof}"

dotnet clean "${boot_project}" --configuration Release
dotnet build "${boot_project}" \
  --configuration Release \
  --no-restore \
  -p:ContinuousIntegrationBuild=true
dotnet "${admission_tool}" verify \
  --assembly "${kernel_assembly}" \
  --root SingPlus.Kernel.KernelEntryPoint::Run \
  --profile KernelNoHeap \
  --proof "${admission_proof}"

dotnet "${qualification_tool}" record-external-blocked \
  --sing-repository "${sing_repository}" \
  --hybridcpu-repository "${hybridcpu_repository}" \
  --expected-hybridcpu-revision "${expected_hybridcpu_revision}" \
  --dotnet-sdk-version "${actual_dotnet_sdk}" \
  --first-pass-kernel-assembly "${first_kernel_assembly}" \
  --first-pass-boot-assembly "${first_boot_assembly}" \
  --first-pass-admission-proof "${first_admission_proof}" \
  --kernel-assembly "${kernel_assembly}" \
  --boot-assembly "${boot_assembly}" \
  --admission-proof "${admission_proof}" \
  --output "${qualification_report}"

sha256sum \
  artifacts/hybridcpu-aot-qualification/pass1/SingPlus.Kernel.dll \
  artifacts/hybridcpu-aot-qualification/pass1/SingPlus.Boot.dll \
  artifacts/hybridcpu-aot-qualification/pass1/SingPlusAdmissionProofV1.json \
  src/Kernel/SingPlus.Kernel/bin/Release/net10.0/SingPlus.Kernel.dll \
  src/Kernel/Boot/SingPlus.Boot/bin/Release/net10.0/SingPlus.Boot.dll \
  artifacts/hybridcpu-aot-qualification/SingPlusAdmissionProofV1.json \
  artifacts/hybridcpu-aot-qualification/SingPlusHybridCpuQualificationV1.json \
  > "${qualification_checksums}"
cat -- "${qualification_checksums}"
cat -- "${qualification_report}"
