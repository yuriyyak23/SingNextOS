[CmdletBinding()]
param([string]$WorkDir,[string]$OutputDirectory,[ValidateSet('MEM','MMIO')][string[]]$Access=@('MEM','MMIO'),[switch]$SkipBuild)
$parameters=@{HostBridgeHdm=$true;Scenario='transactional-map-retire';Access=$Access;TimeoutSeconds=240;SkipBuild=$SkipBuild}
if($WorkDir){$parameters.WorkDir=$WorkDir}
if($OutputDirectory){$parameters.OutputDirectory=$OutputDirectory}
$report=& "$PSScriptRoot/Investigate-CxlHdmDecoderGate.ps1" @parameters
if(!$report.InvestigationCompleted -or @($report.Cases).Count -ne $Access.Count){throw 'Managed mapping investigation incomplete.'}
foreach($case in $report.Cases){
    if(!$case.ExpectationMet -or !$case.ManagedMapRetireExecuted -or !$case.WriteBlocked -or !$case.MappingGenerationAdvanced -or !$case.StaleEpochQuarantined -or !$case.QuarantinedRemapRejected){throw 'Managed mapping lifecycle observation failed.'}
    $order=@($case.HardwareOperations|Where-Object Operation -eq Disable|Select-Object -ExpandProperty Name)
    if(($order -join ',') -ne 'HostBridge,RootPort,Endpoint'){throw 'Retire did not use host-first reverse-hop disable.'}
    $closed=$case.Snapshots|Where-Object Stage -eq '03-managed-retire'
    if(@($closed.DirectAliases).Count -ne 0){throw 'Alias retained after managed Retire.'}
}
if($report.DecoderIsolationQualified -or $report.TransactionalHdmMappingRetirementQualified){throw 'Lab test must not assert production isolation.'}
$report
