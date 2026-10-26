[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repositoryRoot
try {
    # Uses already available local dependencies. No restore or remote source operation.
    dotnet build SingNextOS.slnx --no-restore --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'vNext solution build failed.' }
    dotnet test tests/SingPlus.Tests/SingPlus.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~VNext|FullyQualifiedName~Phase08OrdinaryCheckpointTests|FullyQualifiedName~Phase06DeterministicTracingTests|FullyQualifiedName~HybridCpuExternalOperationProviderTests' --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'vNext focused qualification failed.' }
    dotnet test SingNextOS.slnx --no-build --no-restore --filter 'FullyQualifiedName!~SingPlus.Tests.Gui' --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Full non-GUI suite failed; vNext qualification remains incomplete.' }
    git diff --check
    if ($LASTEXITCODE -ne 0) { throw 'vNext diff check failed.' }
}
finally {
    Pop-Location
}
