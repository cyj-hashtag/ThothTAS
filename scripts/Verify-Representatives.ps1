param([string]$ProjectRoot=(Split-Path -Parent $PSScriptRoot),[string]$OutputDirectory='', [switch]$Resume, [ValidateRange(2,100)][int]$Count=2, [switch]$AllModes)
$ErrorActionPreference='Stop'
$ProjectRoot=[IO.Path]::GetFullPath($ProjectRoot)
if(!$OutputDirectory){$OutputDirectory=Join-Path $ProjectRoot ('work/acceptance/representatives-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$cases=@(
    @{name='inputs';file='inputs.tas';modes=$false},
    @{name='basic-transition';file='basic-transition.tas';modes=$true},
    @{name='basic-death';file='basic-death.tas';modes=$false},
    @{name='balls-collision';file='balls-collision.tas';modes=$false},
    @{name='boss-big';file='boss-big.tas';modes=$false},
    @{name='teleport';file='teleport.tas';modes=$true},
    @{name='zapper-clock';file='zapper-clock.tas';modes=$true}
)
$results=@()
foreach($case in $cases){
    $script=Join-Path $ProjectRoot ('examples/validation/'+$case.file)
    if(!(Test-Path -LiteralPath $script)){throw ('缺少已准备脚本: '+$script)}
    $dir=Join-Path $OutputDirectory $case.name
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $reportPath=Join-Path $dir 'report.json'
    $existing=if(Test-Path -LiteralPath $reportPath){Get-Content -Raw -LiteralPath $reportPath | ConvertFrom-Json}else{$null}
    if(!$Resume -or $null -eq $existing -or !$existing.Passed){
        if($existing){Copy-Item -LiteralPath $reportPath -Destination (Join-Path $dir ('report-history-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'.json'))}
        $arguments=@('verify',$script,'--root',$ProjectRoot,'--count',[string]$Count,'--out',$dir)
        if($AllModes -or $case.modes){$arguments+='--modes'}
        Write-Output ("{0}: {1}次独立回放{2}" -f $case.name,$Count,$(if($AllModes -or $case.modes){'+暂停/逐帧/0.5x/2x'}else{''}))
        try {& (Join-Path $ProjectRoot 'dist/host/ThothTAS.Host.exe') @arguments 2>&1 | Tee-Object -FilePath (Join-Path $dir 'execution.txt')}
        catch {$_ | Out-String | Add-Content -LiteralPath (Join-Path $dir 'execution.txt')}
    }
    $r=if(Test-Path -LiteralPath $reportPath){Get-Content -Raw -LiteralPath $reportPath | ConvertFrom-Json}else{$null}
    $results+=@{name=$case.name;passed=($null -ne $r -and $r.Passed);runs=$(if($r){$r.Runs.Count}else{0});report=$reportPath}
    @{finished=$results.Count;total=$cases.Count;passed=@($results | Where-Object passed).Count;results=$results} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'summary.json') -Encoding utf8
}
if(@($results | Where-Object passed).Count -ne $cases.Count){exit 2}
