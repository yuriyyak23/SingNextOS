param(
    [ValidateSet('LocalAdapter','Ise','Hardware')]
    [string]$Lane = 'LocalAdapter',
    [string]$HybridCpuRoot = 'C:\Users\Yuriy Kurnosov\Desktop\HybridCPU v2'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$qualifiedRevision = '9e001bf29df06ad3d4ff7337f81d4e5bc0a62fc9'
$observedRevision = (& git -C $HybridCpuRoot rev-parse --verify 'HEAD^{commit}').Trim()
if ($LASTEXITCODE -ne 0) { throw 'Unable to read the HybridCPU checkout HEAD.' }

Push-Location $repositoryRoot
try {
    & dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-restore `
        --filter 'FullyQualifiedName~DirectBoot|FullyQualifiedName~ProtectedBootStateMachineTests|FullyQualifiedName~HybridCpuBootAdapterTests|FullyQualifiedName~HybridBootInfoImporterTests|FullyQualifiedName~HybridBootProductionAdapterTests|FullyQualifiedName~HybridBootAuthorityAdmissionTests|FullyQualifiedName~RepositoryArchitecturePolicyTests|FullyQualifiedName~BootCapsuleAdmissionTests|FullyQualifiedName~PlatformBackendResetEpochTests' `
        --logger 'console;verbosity=minimal'
    if ($LASTEXITCODE -ne 0) { throw 'Direct SingNext local qualification tests failed.' }

    if ($Lane -ne 'LocalAdapter') {
        if ($observedRevision -ne $qualifiedRevision) {
            throw "ExternalBlocked: observed HybridCPU $observedRevision differs from qualified pin $qualifiedRevision; requalification evidence is required."
        }
        throw "ExternalBlocked: $Lane requires reset-loadable capsule, entry/bootstrap/reset/CXL evidence not supplied by this repository."
    }

    Write-Host "Direct SingNext local deterministic tests passed. HybridCPU observed=$observedRevision qualified=$qualifiedRevision adapter-contours=AdapterQualified capsule-chain=ContractOnlyBlocked"
}
finally {
    Pop-Location
}
