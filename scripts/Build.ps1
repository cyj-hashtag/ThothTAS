param([string]$ProjectRoot=(Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference='Stop'
$ProjectRoot=[IO.Path]::GetFullPath($ProjectRoot)
foreach($p in @('ThothTAS.Runtime','ThothTAS.Preloader')){
    & dotnet build (Join-Path $ProjectRoot "src/$p/$p.csproj") -c Release
    if($LASTEXITCODE -ne 0){throw "构建失败: $p"}
}
New-Item -ItemType Directory -Force -Path (Join-Path $ProjectRoot 'game/BepInEx/plugins'),(Join-Path $ProjectRoot 'game/BepInEx/patchers'),(Join-Path $ProjectRoot 'dist/module') | Out-Null
Copy-Item -LiteralPath (Join-Path $ProjectRoot 'src/ThothTAS.Runtime/bin/Release/net35/ThothTAS.Runtime.dll') -Destination (Join-Path $ProjectRoot 'game/BepInEx/plugins/ThothTAS.Runtime.dll')
Copy-Item -LiteralPath (Join-Path $ProjectRoot 'src/ThothTAS.Preloader/bin/Release/net35/ThothTAS.Preloader.dll') -Destination (Join-Path $ProjectRoot 'game/BepInEx/patchers/ThothTAS.Preloader.dll')
Copy-Item -LiteralPath (Join-Path $ProjectRoot 'src/ThothTAS.Runtime/bin/Release/net35/ThothTAS.Runtime.dll'),(Join-Path $ProjectRoot 'src/ThothTAS.Preloader/bin/Release/net35/ThothTAS.Preloader.dll') -Destination (Join-Path $ProjectRoot 'dist/module')
foreach($p in @('ThothTAS.Host','ThothTAS.Editor')){
    $folder=if($p -eq 'ThothTAS.Host'){'host'}else{'editor'}
    & dotnet publish (Join-Path $ProjectRoot "src/$p/$p.csproj") -c Release -r win-x64 --self-contained true -o (Join-Path $ProjectRoot "dist/$folder")
    if($LASTEXITCODE -ne 0){throw "发布失败: $p"}
}
