# 创建及只读校验独立副本

`scripts/Create-TasCopy.ps1` 从 `D:\Play\Steam\steamapps\common\THOTH` 只读复制到项目 `game`。它不启动游戏、不操作 Steam，不处理任何游戏进程，不删除副本或用户文件。

```powershell
# 已有副本只读检查；不下载、不建目录、不恢复存档、不修改配置。
& D:\Workspace4All\ThothTAS\scripts\Create-TasCopy.ps1 -ValidateOnly

# 首次创建或补齐缺失文件；恢复副本的固定初始存档。
& D:\Workspace4All\ThothTAS\scripts\Create-TasCopy.ps1
```

可选参数 `-ProjectRoot` 指向项目根目录，`-SourceDirectory` 指向合法的原版目录，`-BepInExArchive` 使用已存在的本地固定版本 ZIP。默认缓存文件是 `work/BepInEx_win_x86_5.4.23.5.zip`。普通模式缺少缓存时只从官方固定 URL 下载到项目 `work`；只读模式缺少缓存直接报错。

游戏版本始终依据项目已有的 `docs/game-fingerprint.tsv`。脚本不重新生成指纹、不采用当前文件的新摘要：源文件或已有副本不符合清单都会停止，保留已存在文件。清单必须包含 EXE、三个游戏程序集和关卡资源，并检查路径越界、重复条目、链接和目录联接。

BepInEx 固定为 5.4.23.5 x86；ZIP SHA256 固定如下：

```text
37651c79e40d6f909572a4f461ac25350bb3ef8fe7fbd29f1aa8791a33b84c82
```

该值已同时核对本地 ZIP 和 [官方 GitHub 5.4.23.5 发布资产](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5) 的 asset digest。安装前检查整个 ZIP 摘要，安装后逐文件检查固定包内容；`doorstop_config.ini` 保留用户配置内容，单独核对必要键。已有不同版本的包文件不会被直接覆盖。

运行模块从 `dist/module/ThothTAS.Runtime.dll` 复制到 `game/BepInEx/plugins`，预加载模块从 `dist/module/ThothTAS.Preloader.dll` 复制到 `game/BepInEx/patchers`。先运行 `scripts/Build.ps1` 可以生成这些文件。只读模式要求两处模块与交付目录完全一致。

预加载入口必须是：

```ini
[Preloader.Entrypoint]
Assembly = UnityEngine.dll
Type = MonoBehaviour
Method = .cctor
```

普通模式仅更新这些必要键，以及 Doorstop 的 `enabled=true` 和 `target_assembly=BepInEx\core\BepInEx.Preloader.dll`；其余配置和注释保留。更新前备份受控配置到 `work/copy-backups/<UTC时间>-<唯一ID>`。重复配置键拒绝继续，避免悄悄采用其中一个值。

已有 `settings.txt` 和其他副本文件保留。原版的 `THOTH.sav` 和 `steam_autocloud.vdf` 不复制；仅用项目固定的 `docs/baseline/THOTH.sav` 恢复副本存档。原版存档不读取后回写，Steam 配置和进程不触碰。若副本存档不同，普通模式先备份副本存档再恢复基线；只读模式报告当前存档摘要和是否等于基线，不修改它。正常游玩或转场后的当前存档变化是合法状态，Host 会在每次新回放前恢复固定基线。

固定基线本身必须符合已确认的 SHA256 `afcb6b465d05796d3ca8179fbeefa11ccf315a0115c4b87ab49bacb579be581e`，修改基线文件会被拒绝。只读报告的 `currentSaveExists`、`currentSaveSha256`、`currentSaveMatchesBaseline` 描述当前副本存档，允许它与固定基线不同或等待 Host 创建。

存档、受控模块及必要配置的覆盖先创建同目录独立临时文件，再用 `File.Replace` 替换已有目录项；新文件用 `File.Move` 放到目标名称。这样即使旧目录项是 NTFS 硬链接，也不会沿该链接把新内容写入另一个名称的原文件。原始受控文件仍先备份。`File.Replace` 兼容 Windows PowerShell 5.1；若文件系统或权限不允许原子替换，报错并保留临时材料，不退回直接覆盖。

普通模式使用 Host 相同的副本文件锁，运行中的会话会使安装失败，不会因此结束会话。发现额外插件或预加载补丁时停止并保留这些文件，避免把其他模块的作用误当作固定 TAS 环境。

成功输出包含固定清单数量、ZIP 摘要、基线摘要、复制及保留文件数量，明确标记 `startedGameProcesses=0` 和 `changedSteamState=false`。文件校验通过不能代替实际游戏逐帧回放验证。
