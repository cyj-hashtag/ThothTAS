#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$ProjectRoot=(Split-Path -Parent $PSScriptRoot),
    [string]$Script='',
    [string]$OutputDirectory='',
    [ValidateRange(2,100)][int]$Count=5,
    [switch]$Reset
)
$ErrorActionPreference='Stop'
$ProjectRoot=[IO.Path]::GetFullPath($ProjectRoot)
if(!$OutputDirectory){$OutputDirectory=Join-Path $ProjectRoot ('work/community-verification/'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8))}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if((Test-Path -LiteralPath $OutputDirectory) -and @(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count){throw '输出目录须为空，避免将新验证与旧记录混在一起。Output directory must be empty.'}
[IO.Directory]::CreateDirectory($OutputDirectory)|Out-Null
& (Join-Path $PSScriptRoot 'Prepare-CommunityMod.ps1') -ProjectRoot $ProjectRoot
$runner=Join-Path $ProjectRoot 'dist/host/ThothTAS.Host.exe'
if(!$Script){
    $Script=Join-Path $OutputDirectory 'composed-basic.tas'
    & $runner compose --root $ProjectRoot --out $Script (Join-Path $ProjectRoot 'examples/basic-1.tas') (Join-Path $ProjectRoot 'examples/basic-2.tas')
    if($LASTEXITCODE -ne 0){throw '脚本拼接失败。Composition failed.'}
}
$Script=[IO.Path]::GetFullPath($Script)
$identity=[ordered]@{version=1;community='rmadlal/v2.8';timerClock='complete native frames at 60 Hz';files=@()}
foreach($relative in @('work/community-mod/v2.8/Assembly-CSharp.dll','game/BepInEx/plugins/ThothTAS.Runtime.dll','game/BepInEx/patchers/ThothTAS.Preloader.dll','dist/host/ThothTAS.Host.dll','docs/game-fingerprint.json')){
    $identity.files+=@{name=$relative;sha256=(Get-FileHash -LiteralPath (Join-Path $ProjectRoot $relative) -Algorithm SHA256).Hash}
}
$identity.scriptSha256=(Get-FileHash -LiteralPath $Script -Algorithm SHA256).Hash
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'identity.json'),($identity|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
& $runner verify $Script --root $ProjectRoot --community-mod --count $Count --modes --out (Join-Path $OutputDirectory 'cold')
if($LASTEXITCODE -ne 0){throw '实际社区模式重复回放未通过，请查看cold/report.json。Actual community replay verification failed.'}
if($Reset){
    & $runner verify-reset $Script --root $ProjectRoot --community-mod --count $Count --out (Join-Path $OutputDirectory 'warm')
    if($LASTEXITCODE -ne 0){throw '实际社区模式同进程重置检查未通过，请查看warm/reset-report.json。Actual warm reset check failed.'}
}
Write-Output "实际验证完成 / Actual verification completed: $OutputDirectory"
