param([string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot), [string]$OutputDirectory = '', [switch]$Resume, [string[]]$Scenes = @())
$ErrorActionPreference='Stop'
$ProjectRoot=[IO.Path]::GetFullPath($ProjectRoot)
if(!$OutputDirectory){$OutputDirectory=Join-Path $ProjectRoot ('work/acceptance/levels-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$hostExe=Join-Path $ProjectRoot 'dist/host/ThothTAS.Host.exe'
if(!(Test-Path -LiteralPath $hostExe)){throw '请先运行 scripts/Build.ps1'}
$levels=Get-Content -LiteralPath (Join-Path $ProjectRoot 'docs/levels.txt') | Where-Object {$_ -and !$_.StartsWith('#')}
if($Scenes.Count){foreach($scene in $Scenes){if($scene -cnotin $levels){throw ('未知关卡: '+$scene)}};$levels=@($levels | Where-Object {$_ -cin $Scenes})}
$results=@()
foreach($level in $levels){
    $dir=Join-Path $OutputDirectory $level
    $report=Join-Path $dir 'report.json'
    $existing=if(Test-Path -LiteralPath $report){Get-Content -Raw -LiteralPath $report | ConvertFrom-Json}else{$null}
    if(!$Resume -or $null -eq $existing -or !$existing.Passed){
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        if($existing){Copy-Item -LiteralPath $report -Destination (Join-Path $dir ('report-history-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'.json'))}
        $script=Join-Path $dir 'start.tas'
        [IO.File]::WriteAllText($script,"Version,1`nStart,$level`nSeed,1`n`n2, N`n",[Text.UTF8Encoding]::new($false))
        Write-Output ("[{0}] {1}: 两次独立进程基线比较" -f (Get-Date -Format 'HH:mm:ss'),$level)
        try {& $hostExe verify $script --root $ProjectRoot --count 2 --out $dir 2>&1 | Tee-Object -FilePath (Join-Path $dir 'execution.txt')}
        catch {$_ | Out-String | Add-Content -LiteralPath (Join-Path $dir 'execution.txt')}
    }
    $r=if(Test-Path -LiteralPath $report){Get-Content -Raw -LiteralPath $report | ConvertFrom-Json}else{$null}
    $issues=@()
    if($r){foreach($run in $r.Runs){
        $log=Join-Path $run.Directory 'unity.log'
        if(Test-Path -LiteralPath $log){$issues+=@(Select-String -LiteralPath $log -Pattern 'NullReferenceException|MissingMethodException|InvalidProgramException|Crash!!!|Couldn.t be loaded|Unable to load scene|THOTH TAS:' | ForEach-Object {$_.Line})}
        # Gameplay fields can differ only in casing; PowerShell PSCustomObject
        # rejects such valid JSON keys. Read the distinct top-level scene only.
        $first=Get-Content -LiteralPath (Join-Path $run.Directory 'frames.jsonl') -First 1
        $scene=[regex]::Match($first,'"scene":"([^"\\]+)"').Groups[1].Value
        if($scene -cne $level){$issues+=('基线场景不匹配: '+$scene)}
    }}
    $results+= [PSCustomObject]@{scene=$level;passed=($null -ne $r -and $r.Passed -and $issues.Count -eq 0);runs=if($r){$r.Runs.Count}else{0};issues=$issues;report=$report}
    @{total=$levels.Count;finished=$results.Count;passed=@($results | Where-Object passed).Count;results=$results} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'summary.json') -Encoding utf8
}
$passed=@($results | Where-Object passed).Count
$text=@('# THOTH 指定关卡起点实际验证', '', "完成 $($results.Count)/$($levels.Count)，通过 $passed。每关2个独立游戏进程；比较基线、2个零输入完整帧、全部随机记录和阶段。", '', '|场景|通过|日志|', '|---|---|---|')
$text+= $results | ForEach-Object {'|'+$_.scene+'|'+$_.passed+'|'+$_.report+'|'}
$text+= @('', 'Teleporters-5 是额外资源房间，正常主流程在 Teleporters-4 转至下一组。此入口测试不代表其自然出口有效。', '跨电脑、未执行的战斗与转场路径不属于此报告证据。')
$text | Set-Content -LiteralPath (Join-Path $OutputDirectory 'summary.md') -Encoding utf8
if($passed -ne $levels.Count){exit 2}
