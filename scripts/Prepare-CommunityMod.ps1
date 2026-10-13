#requires -Version 5.1
[CmdletBinding()]
param([string]$ProjectRoot=(Split-Path -Parent $PSScriptRoot),[string]$AssemblyFile='')
$ErrorActionPreference='Stop'
$ProjectRoot=[IO.Path]::GetFullPath($ProjectRoot)
$expected='2a35ddea8a48144e926ac33796ffd63267a5ac129fd2495ef7f10a7d893e33fd'
$destination=Join-Path $ProjectRoot 'work/community-mod/v2.8/Assembly-CSharp.dll'
if(Test-Path -LiteralPath $destination){
    if((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected){throw '已有社区Mod指纹不匹配，保留原文件并停止。Existing community Mod hash mismatch; file preserved.'}
    Write-Output "社区Mod已准备 / Community Mod ready: $destination"
    return
}
$directory=[IO.Path]::GetDirectoryName($destination)
[IO.Directory]::CreateDirectory($directory) | Out-Null
$temporary=Join-Path $directory ('download-'+[guid]::NewGuid().ToString('N')+'.tmp')
try{
    if($AssemblyFile){Copy-Item -LiteralPath ([IO.Path]::GetFullPath($AssemblyFile)) -Destination $temporary}
    else{
        [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri 'https://github.com/rmadlal/thoth-mod/releases/download/v2.8/Assembly-CSharp.dll' -OutFile $temporary -UseBasicParsing
    }
    $actual=(Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash.ToLowerInvariant()
    if($actual -ne $expected){throw "社区Mod指纹不匹配 / Community Mod hash mismatch: $actual"}
    [IO.File]::Move($temporary,$destination)
    Write-Output "社区Mod已准备 / Community Mod ready: $destination"
    Write-Output '只保存在work；不替换游戏磁盘程序集。Stored in work; original game assemblies are not replaced.'
}finally{if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary}}
