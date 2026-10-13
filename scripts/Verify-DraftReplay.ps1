[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ScriptPath,
    [string]$ProjectRoot=(Split-Path -Parent $PSScriptRoot),
    [string]$OutputDirectory
)
$ErrorActionPreference='Stop'
$ProjectRoot=[IO.Path]::GetFullPath($ProjectRoot)
$ScriptPath=[IO.Path]::GetFullPath($ScriptPath)
if(-not(Test-Path -LiteralPath $ScriptPath -PathType Leaf)){throw 'TAS script not found.'}
if(-not $OutputDirectory){$OutputDirectory=Join-Path $ProjectRoot ('work/draft-replay-verification/'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N'))}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
$editor=Join-Path $ProjectRoot 'dist/editor/ThothTAS.Editor.exe'
if(-not(Test-Path -LiteralPath $editor)){throw 'Build the editor first.'}
if(-not(Test-Path -LiteralPath (Join-Path $ProjectRoot 'work/community-mod/v2.8/Assembly-CSharp.dll'))){throw 'Run scripts/Prepare-CommunityMod.ps1 first; this check includes both game profiles.'}
$originalHash=(Get-FileHash -LiteralPath $ScriptPath -Algorithm SHA256).Hash
foreach($test in @('draft-safety','replay-safety')){
    $destination=Join-Path $OutputDirectory $test
    $info=New-Object Diagnostics.ProcessStartInfo
    $info.FileName=$editor
    $info.WorkingDirectory=$ProjectRoot
    $info.UseShellExecute=$false
    $info.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    $info.Arguments='--root "'+$ProjectRoot+'" --self-test-'+$test+' "'+$ScriptPath+'" --out "'+$destination+'"'
    $process=[Diagnostics.Process]::Start($info)
    try{$process.WaitForExit();if($process.ExitCode -ne 0){throw "Failed: $test; report: $destination/report.json"}}
    finally{$process.Dispose()}
}
if((Get-FileHash -LiteralPath $ScriptPath -Algorithm SHA256).Hash -ne $originalHash){throw 'Input script changed during verification.'}
Write-Output "Actual draft and replay verification passed: $OutputDirectory"
