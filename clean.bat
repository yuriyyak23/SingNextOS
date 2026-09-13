@echo off
setlocal EnableExtensions EnableDelayedExpansion

pushd "%~dp0"

if exist ".vs" rd /q /s ".vs"
if exist "TestResults" rd /q /s "TestResults"

if exist "HybridCPU_ISE.Tests" (
    pushd "HybridCPU_ISE.Tests"
    if not exist "SafetyAndVerification" mkdir "SafetyAndVerification"
    if not exist "MemoryAndRouting" mkdir "MemoryAndRouting"
    if not exist "ArchitectureAndExecution" mkdir "ArchitectureAndExecution"
    if not exist "FSPAndSpeculative" mkdir "FSPAndSpeculative"
    if not exist "ISAModel" mkdir "ISAModel"
    if not exist "EvaluationAndMetrics" mkdir "EvaluationAndMetrics"
    if not exist "InstructionsAndOpcodes" mkdir "InstructionsAndOpcodes"
    if not exist "PhasingAndExtensions" mkdir "PhasingAndExtensions"
    if not exist "Miscellaneous" mkdir "Miscellaneous"
    popd
)

for /r %%F in (*.csproj) do (
    set "ProjectDir=%%~dpF"
    if exist "!ProjectDir!bin" rd /s /q "!ProjectDir!bin"
    if exist "!ProjectDir!obj" rd /s /q "!ProjectDir!obj"
)

popd

echo Clean complete.
