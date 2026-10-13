[CmdletBinding()]
param(
    [string]$ProjectRoot=(Split-Path -Parent $PSScriptRoot),
    [string]$OutputDirectory='',
    [ValidateRange(2,100)][int]$Count=5,
    [string]$ScriptPath='',
    [switch]$CommunityMod,
    [switch]$CheckAllStarts,
    [switch]$Modes
)
$ErrorActionPreference='Stop'
$ProjectRoot=[IO.Path]::GetFullPath($ProjectRoot)
if(!$OutputDirectory){$OutputDirectory=Join-Path $ProjectRoot ('work/modified-verification/'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8))}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
$allowed=(Join-Path $ProjectRoot 'work').TrimEnd('\')+'\'
if(!$OutputDirectory.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){throw 'OutputDirectory must be inside this project work directory.'}
if((Test-Path -LiteralPath $OutputDirectory) -and @(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count){throw 'Use a new empty output directory.'}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$encoding=New-Object Text.UTF8Encoding($false)
$hostExe=Join-Path $ProjectRoot 'dist/host/ThothTAS.Host.exe'
$sources=if($ScriptPath){@($ScriptPath)}else{@('examples/validation/basic-death.tas','examples/validation/balls-collision.tas','examples/validation/teleport.tas','examples/validation/boss-big.tas','examples/validation/zapper-clock.tas','examples/validation/inputs.tas','examples/validation/basic-transition.tas')}
$identity=@('game/BepInEx/plugins/ThothTAS.Runtime.dll','game/BepInEx/patchers/ThothTAS.Preloader.dll','dist/host/ThothTAS.Host.exe','docs/game-fingerprint.tsv','docs/baseline/THOTH.sav') | ForEach-Object { [ordered]@{path=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $ProjectRoot $_) -Algorithm SHA256).Hash} }
$stages=New-Object Collections.Generic.List[object]
$failure=$null
try{
    foreach($source in $sources){
        $path=if([IO.Path]::IsPathRooted($source)){$source}else{Join-Path $ProjectRoot $source}
        $before=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        $text=[IO.File]::ReadAllText($path,[Text.Encoding]::UTF8)
        if($text -match '(?m)^Rules,'){$text=[regex]::Replace($text,'(?m)^Rules,[^\r\n]*','Rules,Deterministic-1')}
        else{$text=[regex]::Replace($text,'(?m)^Version,1\r?\n',"Version,1`nRules,Deterministic-1`n")}
        $name=[IO.Path]::GetFileNameWithoutExtension($path)
        $copy=Join-Path $OutputDirectory ($name+'.tas')
        [IO.File]::WriteAllText($copy,$text,$encoding)
        $stage=Join-Path $OutputDirectory ($name+'-warm')
        $arguments=@('verify-reset',$copy,'--root',$ProjectRoot,'--count',[string]$Count,'--speed','2','--out',$stage)
        if($CommunityMod){$arguments+='--community-mod'}
        & $hostExe @arguments
        $code=$LASTEXITCODE
        $stages.Add([ordered]@{name=$name;kind='actual-cold-versus-warm';exitCode=$code;report=(Join-Path $stage 'reset-report.json');sourceSha256=$before})
        if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $before){throw "Source changed: $path"}
        if($code -ne 0){throw "Strict real-game reset verification failed: $name"}
        if($Modes){
            $modeOutput=Join-Path $OutputDirectory ($name+'-modes')
            $arguments=@('verify',$copy,'--root',$ProjectRoot,'--count','2','--modes','--out',$modeOutput)
            if($CommunityMod){$arguments+='--community-mod'}
            & $hostExe @arguments
            $code=$LASTEXITCODE
            $stages.Add([ordered]@{name=$name;kind='actual-independent-processes-and-modes';exitCode=$code;report=(Join-Path $modeOutput 'report.json')})
            if($code -ne 0){throw "Real-game speed/pause/step verification failed: $name"}
        }
    }
    if($CheckAllStarts){
        $arguments=@('run','--project',(Join-Path $ProjectRoot 'tests/ThothTAS.Acceptance'),'-c','Release','--','modified-starts','--root',$ProjectRoot,'--out',(Join-Path $OutputDirectory 'all-starts'))
        if($CommunityMod){$arguments+='--community-mod'}
        & dotnet @arguments
        $code=$LASTEXITCODE
        $stages.Add([ordered]@{name='all-starts';kind='actual-two-warm-entries-per-level';exitCode=$code;report=(Join-Path $OutputDirectory 'all-starts/report.json')})
        if($code -ne 0){throw 'Real-game all-starts verification failed.'}
    }
}catch{$failure=$_.Exception.ToString()}
$report=[ordered]@{version=1;rules='Deterministic-1';passed=($null -eq $failure);error=$failure;communityMod=[bool]$CommunityMod;identity=$identity;stages=$stages;scope='Actual game. Strict warm comparison covers every baseline and frame field, phase digest and complete neutral-random record, with each tested reset below 1000ms. Modified rules differ from original THOTH. All-starts compares two warm entries per room in one long-lived process; independent cold comparison is provided by representative stages. Startup and switching rules/mods require a fresh process.'}
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'summary.json'),($report | ConvertTo-Json -Depth 8),$encoding)
if($failure){throw $failure}
Write-Output ('Actual deterministic-rules verification passed: '+(Join-Path $OutputDirectory 'summary.json'))
