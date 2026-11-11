[CmdletBinding()]
param([string]$WorkDir,[string]$OutputDirectory,[ValidateRange(60,600)][int]$TimeoutSeconds=180)
# Capsule-free reproducer: each scenario owns a fresh QEMU process.
$parameters=@{Scenario=@('before-first-commit','committed-control','endpoint-uncommit');Access=@('MEM');TimeoutSeconds=$TimeoutSeconds}
if($WorkDir){$parameters.WorkDir=$WorkDir}
if($OutputDirectory){$parameters.OutputDirectory=$OutputDirectory}
$report=& "$PSScriptRoot/Investigate-CxlHdmDecoderGate.ps1" @parameters
$report | Add-Member -NotePropertyName ReproducerScope -NotePropertyValue 'QEMU HDM alias lifecycle; no capsule, signature, kernel or qtest' -PassThru
