#requires -Version 5.1
[CmdletBinding()]
param([string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$createScript = Join-Path $ProjectRoot 'scripts/Create-TasCopy.ps1'
$archive = Join-Path $ProjectRoot 'work/BepInEx_win_x86_5.4.23.5.zip'
$isolated = Join-Path $ProjectRoot ('work/create-copy-actual-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
$startedUtc = [DateTime]::UtcNow
$source = 'D:\Play\Steam\steamapps\common\THOTH'
$sourceSaveHash = (Get-FileHash -LiteralPath (Join-Path $source 'THOTH.sav') -Algorithm SHA256).Hash
$sourceSettingsHash = (Get-FileHash -LiteralPath (Join-Path $source 'settings.txt') -Algorithm SHA256).Hash
$report = [ordered]@{version=1;kind='actual-copy-creation-and-file-validation';passed=$false;startedUtc=$startedUtc.ToString('o');finishedUtc=$null;isolatedRoot=$isolated;sourceReadOnly=$source;gameProcessesStarted=0;scope='实际创建独立副本及真实文件校验；不启动游戏，不是实际回放验收。';initialCreation=$null;initialValidation=$null;hardLinkTest=$null;finalValidation=$null;error=$null}
function Sha([string]$p) { return (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant() }
function Assert-InTestRoot([string]$p) {
    $absolute = [IO.Path]::GetFullPath($p)
    if (!$absolute.StartsWith([IO.Path]::GetFullPath($isolated).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "测试路径越界：$absolute" }
}
try {
    foreach ($folder in @('docs/baseline', 'dist/module', 'work')) { [IO.Directory]::CreateDirectory((Join-Path $isolated $folder)) | Out-Null }
    foreach ($relative in @('docs/game-fingerprint.tsv', 'docs/game-fingerprint.json', 'docs/levels.txt', 'docs/baseline/THOTH.sav', 'dist/module/ThothTAS.Runtime.dll', 'dist/module/ThothTAS.Preloader.dll')) {
        Copy-Item -LiteralPath (Join-Path $ProjectRoot $relative) -Destination (Join-Path $isolated $relative)
    }
    Write-Output ('[' + [DateTimeOffset]::UtcNow.ToOffset([TimeSpan]::FromHours(9)).ToString('yyyy-MM-dd HH:mm:ss zzz') + '] 实际创建隔离副本：' + $isolated)
    $report.initialCreation = & $createScript -ProjectRoot $isolated -SourceDirectory $source -BepInExArchive $archive
    $report.initialValidation = & $createScript -ProjectRoot $isolated -SourceDirectory $source -BepInExArchive $archive -ValidateOnly
    if (!$report.initialCreation.passed -or !$report.initialValidation.passed) { throw '首次创建或只读复验失败。' }

    $save = Join-Path $isolated 'game/THOTH.sav'
    $sentinel = Join-Path $isolated 'work/hardlink-origin.sav'
    $priorSave = Join-Path $isolated 'work/first-created-baseline.sav'
    foreach ($path in @($save, $sentinel, $priorSave)) { Assert-InTestRoot $path }
    [IO.File]::WriteAllText($sentinel, 'isolated hard link sentinel: preserve these bytes', [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($save, $priorSave)
    New-Item -ItemType HardLink -Path $save -Target $sentinel | Out-Null
    $sentinelBefore = Sha $sentinel
    if ((Sha $save) -ne $sentinelBefore) { throw '测试硬链接没有指向隔离哨兵文件。' }
    $settings = Join-Path $isolated 'game/settings.txt'
    $bepConfig = Join-Path $isolated 'game/BepInEx/config/BepInEx.cfg'
    foreach ($path in @($settings, $bepConfig)) { Assert-InTestRoot $path }
    [IO.File]::AppendAllText($settings, "`r`n// isolated user setting marker`r`n", [Text.UTF8Encoding]::new($false))
    $settingsBefore = Sha $settings
    $configText = [IO.File]::ReadAllText($bepConfig).Replace('Type = MonoBehaviour', 'Type = Application') + "`r`n[User.Settings]`r`nKeepMe = untouched`r`n"
    [IO.File]::WriteAllText($bepConfig, $configText, [Text.UTF8Encoding]::new($false))
    $secondCreate = & $createScript -ProjectRoot $isolated -SourceDirectory $source -BepInExArchive $archive
    $sentinelAfter = Sha $sentinel
    $baselineHash = Sha (Join-Path $isolated 'docs/baseline/THOTH.sav')
    $hardLinkSafe = $sentinelBefore -eq $sentinelAfter -and (Sha $save) -eq $baselineHash
    $settingsPreserved = (Sha $settings) -eq $settingsBefore
    $configPreserved = [IO.File]::ReadAllText($bepConfig).Contains('KeepMe = untouched')
    $configUpdated = [IO.File]::ReadAllText($bepConfig).Contains('Type = MonoBehaviour')
    $report.hardLinkTest = [ordered]@{passed=($hardLinkSafe -and $settingsPreserved -and $configPreserved -and $configUpdated);fixtureUsesOriginalSave=$false;sentinelPath=$sentinel;sentinelBeforeSha256=$sentinelBefore;sentinelAfterSha256=$sentinelAfter;copySaveSha256=(Sha $save);baselineSha256=$baselineHash;userSettingsPreserved=$settingsPreserved;unrelatedConfigPreserved=$configPreserved;requiredConfigUpdated=$configUpdated;secondCreation=$secondCreate}
    if (!$report.hardLinkTest.passed) { throw '硬链接保护或配置保留检查失败。' }
    $report.finalValidation = & $createScript -ProjectRoot $isolated -SourceDirectory $source -BepInExArchive $archive -ValidateOnly
    if (!$report.finalValidation.passed) { throw '最终只读复验失败。' }
    $report.sourceSaveUnchanged = (Get-FileHash -LiteralPath (Join-Path $source 'THOTH.sav') -Algorithm SHA256).Hash -eq $sourceSaveHash
    $report.sourceSettingsUnchanged = (Get-FileHash -LiteralPath (Join-Path $source 'settings.txt') -Algorithm SHA256).Hash -eq $sourceSettingsHash
    if (!$report.sourceSaveUnchanged -or !$report.sourceSettingsUnchanged) { throw '只读源文件前后摘要变化。' }
    $report.passed = $true
} catch { $report.error = $_.Exception.ToString() }
$report.finishedUtc = [DateTime]::UtcNow.ToString('o')
$reportDirectory = Join-Path $ProjectRoot 'docs/verification'
[IO.Directory]::CreateDirectory($reportDirectory) | Out-Null
$report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $reportDirectory 'create-copy-actual.json') -Encoding utf8
$lines = @('# THOTH TAS 实际副本创建及文件检验', '', ('结果：' + $(if ($report.passed) { '通过' } else { '未通过' }) + '。游戏进程启动数：0。'), '', '范围：普通创建模式真实只读复制原游戏、安装固定BepInEx包、部署模块、配置MonoBehaviour预加载并恢复独立初始存档；随后执行只读校验。此报告不是游戏回放或确定性验收。', '', ('保留测试根目录：`' + $isolated + '`'), '', '- 初次正常创建后只读复验固定游戏文件、BepInEx包、模块和基线。', '- 在隔离目录创建存档硬链接，恢复基线后检查另一个名称的哨兵内容不变。未对原版存档建立硬链接。', '- 保留已有settings.txt自定义标记及无关配置，同时修复必要的MonoBehaviour入口。', '- 最后再次只读复验；比较只读源存档和settings前后SHA256。', '', '所有检查、复制数量、摘要、备份位置和错误均见同目录create-copy-actual.json。测试目录保留，不删除主副本或源游戏。')
if ($report.error) { $lines += @('', '```text', $report.error, '```') }
$lines | Set-Content -LiteralPath (Join-Path $reportDirectory 'create-copy-actual.md') -Encoding utf8
$report | ConvertTo-Json -Depth 10
if (!$report.passed) { exit 2 }
