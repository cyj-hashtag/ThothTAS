# 顺序章节与分关脚本拼接

`Room,关卡名` 将一个脚本分为顺序章节。没有 `Room` 的旧脚本保留原来的输入帧预算行为，包括已写好的 `examples/basic-1.tas` 和 `examples/basic-2.tas`；拼接不会修改这些原文件。

```text
Version,1
Start,Basic-1
Seed,1

# Source: basic-1.tas
Room,Basic-1
75, M=0, A=14
34, M=0, A=-30

# Source: basic-2.tas
Room,Basic-2
70, M=-30, A=-37
5, M=160, A=-48
```

上面的输入只演示格式，不是已经验证可通关的脚本。每个 `Room` 内的帧号从零开始，移动、射击、固定世界坐标瞄准和括号逗号的写法与旧格式相同。`Version`、`Start` 和可选的 `Seed` 位于所有章节之前。`Seed` 缺省为 1，整段只使用一个种子。

首个 `Room` 必须与 `Start` 相同。每章至少有一段输入；不允许重复关卡、未知关卡、空章、首个 `Room` 之前的输入，或章节开始后重新写头部。关卡标识按原资源名称精确区分大小写，例如 `zapper-1`。格式错误保留行号和列号。

## 执行契约

显式章节在实际关卡玩家激活后的完整帧边界开始。观察到游戏实际过关后，本章剩余输入被跳过；转场期间输入归零，等下一关玩家激活后才执行下一章的第零帧。末章也必须实际过关，才能完成整段脚本。

如果章节输入已经用尽，游戏仍未过关，回放必须暂停并说明当前关卡和章节帧，不能把下一章输入施加到当前关卡。若自然转场进入的关卡与下一章不符，也必须暂停。这里只定义章节规则；真实执行是否满足规则，应以对应游戏回放报告为准。

`TasScript.TotalFrames` 是所有输入段帧数的合计，属于输入预算。实际记录的全局帧数会包含转场的零输入帧，也会因提前过关而跳过尾部输入。各章本地帧不包含转场。运行侧的总预算上限为输入预算加每次章节转场最多 3600 帧。

## 拼接规则与验证

拼接按选择文件的顺序进行。各文件可以是旧的单关脚本，也可以已经包含章节；拼接结果统一输出显式 `Room`。不同文件的 `Seed` 必须相同，包括缺省值。工具不会悄悄改种子。

拼接按 `docs/levels.txt` 的游戏自然顺序检查相邻关系，不按文件名或字母顺序排序。`Basic-4 → Lasers-1` 和 `Teleporters-4 → zapper-1` 可以连接。`Teleporters-5` 是额外资源，其出口 `Teleporters-6` 不存在；它不能加入拼接，检查自然顺序时会从列表中排除它。

每章前会写入 `# Source: 文件名 (input lines 起始行-末行)`，保留原输入行的内容和输入行之间的注释。输入最前面和最后面的非输入注释不影响运行，可能不随章复制。解析得到的 `SourceLine` 指向拼接后的不可变脚本快照；原文件的范围在 `SourceOrigin` 中。原文件格式错误和拼接顺序、种子错误则报告对应原文件名及行列。

单关分别成功不代表拼接成功。第二关的初始随机序列、实体残留和计时状态来自连续运行，不会重播种或恢复该关的独立初始状态。拼接之后必须从首关起点执行并重复比较整段真实回放，才能把这份拼接结果标为已验证。

编辑器的“拼接脚本”可以按选择顺序组合文件，并在确认窗口调整顺序。命令行入口也保留输入顺序，并拒绝覆盖原输入或已有输出：

```powershell
dotnet run --project src/ThothTAS.Host -c Release -- compose --out work/basic-1-2-composed.tas examples/basic-1.tas examples/basic-2.tas
dotnet run --project src/ThothTAS.Host -c Release -- validate work/basic-1-2-composed.tas
```

如果示例输出已经存在，请换一个新文件名。上述两步只拼接并校验文本，不启动游戏；后续使用 `verify` 才会实际运行副本。

## Core 接口

```csharp
TasScript ScriptParser.Parse(string text, HashSet<string> knownStarts);
string ScriptComposer.Compose(IEnumerable<ScriptSource> sources, IList<string> orderedStarts);
// new ScriptSource(fileName, fileText)

script.HasExplicitRooms;
script.Chapters;                          // 只读集合
script.GetInputAtChapterFrame(chapterIndex, localFrame, out sourceLine);

chapter.Index;                            // 从零开始
chapter.Scene;
chapter.SourceLine;                       // Room 行；隐式章节为 Start 行
chapter.SourceOrigin;                     // 可空的 Source 注释
chapter.TotalFrames;
chapter.Segments;                         // StartFrame 为本章本地帧
chapter.GetInputAtFrame(localFrame, out sourceLine);

script.Segments;                          // 兼容旧调用：输入预算扁平展开
script.GetInputAtFrame(inputBudgetFrame, out sourceLine);
```

`ScriptComposer` 不读写文件，不启动游戏。调用者负责读取关卡顺序与文件内容、保存输出，并避免覆盖任一输入文件。它抛出的 `ScriptComposeException` 包含 `SourceName`、`Line`、`Column`、`Reason`；源解析失败时 `InnerException` 为原 `ScriptParseException`。只有已知关卡集合的解析器检查格式；需要自然相邻检查时，应使用拼接器并传入实际关卡顺序。

纯格式验证入口：

```powershell
dotnet run --project tests/ThothTAS.Core.Tests -c Release
```

章节与拼接的纯测试验证格式、预算、索引、来源、种子和自然相邻规则。该入口不会运行 THOTH，不提供真实过关或确定性结论。
