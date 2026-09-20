# AGENTS.md — StableRestorer 技术说明与操作指南

面向在本仓库里工作的 AI agent / 开发者。用户文档见 [README.md](README.md)。

本文件记录**约定、内部细节与踩过的坑**，尤其是那些一旦违反就会静默产生错误结果的地方。
所有本机路径、用户名与实测私有数据都已移除；需要数字时给出**如何自己测出来**的方法。

---

## 1. 项目定位

把 osu!lazer 的哈希文件库（`files/{h0}/{h0h1}/{hash}`）按数据库里记录的原始文件名，
还原成 osu!stable 的 `Songs/` 布局。

- 输入：osu!lazer 数据目录（`client.realm` + `files/`）
- 输出：osu!stable 根目录下的 `Songs/`
- 默认手段：硬链接（同一卷）；否则复制

### 硬性安全不变量（改动时必须保持）

1. lazer 目录**只读**打开（`IsReadOnly = true`），绝不写入或删除其中任何文件。
2. 输出目录不得等于、位于或包含 lazer 数据目录 —— `RestoreOptions.ValidateDirectories()`
   在**打开数据库之前**校验，属于快速失败路径。
3. 只增不删。唯一允许的删除动作是 `--overwrite` 下替换**目标文件自身**，且前提是已确认它
   不是同一份文件。
4. 已存在的同 inode 目标（已是硬链接）必须跳过，不得重复创建。

---

## 2. 技术栈与目录结构

- .NET 10（`net10.0`），单项目控制台程序，依赖 `Realm` 20.1.0。
- 发布为 self-contained 单文件 exe；`realm-wrappers.dll` 是原生库，**必须与 exe 同目录**，
  单文件打包不会把它嵌进去。
- **不要开启 trimming / PublishTrimmed**：Realm 靠反射发现模型类型并做原生互操作，
  裁剪后不会编译报错，只会在运行时抛出难以理解的 Realm schema 错误。

```
StableRestorer/
├─ RealmSchema/Models.cs     与 osu!lazer schema 52 逐字段对齐的 Realm 模型（见 §3）
├─ Lazer/LazerDatabase.cs    只读打开 client.realm，投影成纯 POCO 快照
├─ IO/StableNaming.cs        文件夹命名与"从文件夹名解析 set id"
├─ IO/FileSystem.cs          同 inode 判定、CreateHardLink、卷序列号比较
├─ IO/FileRestorer.cs        单文件的链接/复制/校验/冲突策略
├─ Engine/RestoreOptions.cs  选项、目录护栏、报告数据结构
├─ Engine/RestoreEngine.cs   已有文件夹扫描、set id 复用、过滤、统计
├─ Cli/CommandLine.cs        命令行帮助文本
├─ Cli/InstallLocator.cs     两端安装位置自动探测
├─ Cli/SchemaProbe.cs        schemas 子命令
├─ Cli/OsuDb.cs              osu!.db 读取器（实验性，见 §8）
├─ Cli/Wizard.cs             交互模式
└─ Program.cs                命令分发、schema 探测、JSON 报告
```

关键分层：**`LazerDatabase` 把 Realm 数据投影成不可变 POCO 后立即释放 Realm**，
之后所有文件系统操作都在普通对象上进行。Realm 对象是线程受限的，Realm 关闭后即失效，
新增字段时必须同步到 `SongSnapshot` / `FileEntry`，不要在文件系统阶段回读 Realm。

---

## 3. Realm 模型必须与游戏完全一致（最高优先级）

Realm 会把"声明的 schema"与文件内的 schema 严格比对：只接受**精确匹配**或**向后兼容的升级**，
不支持只读模式下降级。因此 `Models.cs` 里的**类名、`[MapTo]` 目标名、属性名与类型，以及顺序无关
但必须齐全的属性集合**都要与 `osu.Game` 的持久化模型一致 —— 包括本工具根本不读的字段
（例如 `Score.MaximumStatisticsJson`）。

少了属性会报 "Property 'X' has been added"；属性名不对会报同样的错，但原因可能是 `[MapTo]`。

### 已经踩过的两个命名坑

| C# 属性名（osu.Game 里） | Realm 中实际存储的名字 |
| --- | --- |
| `RealmUser.CountryString` | **`CountryCode`** |
| `ScoreInfo.RankInt` | **`Rank`** |

规律：osu.Game 用 `[MapTo(nameof(SomeEnum))]` 把枚举包装属性映射成另一个名字。
遇到"属性被新增"的报错时，**先去 osu.Game 源码看这个属性有没有 `[MapTo]`**。

### 排查方法

```powershell
stablerestorer schemas --lazer <lazer数据目录>
```

逐个候选版本报告"哪条声明与文件不一致"。加 `--verbose` 可在 `scan`/`restore` 里看到
每个候选版本被拒的原因。

### 升级到新 lazer 版本时

1. 用 `schemas` 确认新文件的实际版本号；
2. 对照 `osu.Game` 同名模型文件，把 `Models.cs` 的差异补齐；
3. 改 `LazerDatabase.SupportedSchemaVersion`；
4. `Models.cs` 顶部的注释说明了对照来源是 ppy/osu 仓库，不要写死本机路径。

---

## 4. 标识符：set id / map id

osu! 的两级 ID 都唯一，本工具的一切匹配都围绕它们：

| 名称 | Realm 字段 | 说明 |
| --- | --- | --- |
| **set id** | `BeatmapSetInfo.OnlineID` | 谱面**集** ID。stable 用它命名文件夹。`<= 0` 表示从未提交到 osu! |
| **map id** | `BeatmapInfo.OnlineID` | 单张**难度** ID |

- 难度与 `.osu` 文件通过 `BeatmapInfo.Hash` == `RealmNamedFileUsage.File.Hash` 对应，
  所以每个文件都能追溯到它属于哪张难度（`FileEntry.MapId`）。
- **set id ≤ 0 的谱面集默认跳过**（`--keep-unsubmitted` 可改为导入）。
  注意这与 `BeatmapSetInfo.Protected` 不是同一个概念：
  `Protected` 表示"由 lazer 随游戏附带"，它**可能有** online ID，也可能没有。
  实测数据里两者不完全重合，所以判据用 set id，不要改回 `Protected`。
- `scan` 会报告重复的 set id。真实数据里出现过重复，不要假设 set id 一定唯一，
  但可以假设它**几乎**唯一。

---

## 5. 文件夹识别与命名

### 命名规则（`StableNaming`）

```text
Songs/{setId} {Artist} - {Title}
```

- `Artist` / `Title` 取该谱面集第一个带 metadata 的难度；
- 非法字符替换为 `_`，并去掉结尾空格与点（Windows 会静默吃掉它们，导致路径往返不一致）；
- 总长超过 155 字符时先截断 `Artist`，再截断 `Title`；
- 同名冲突追加 ` (2)`、` (3)`… 并写入报告 notices。

### 识别规则（关键）

`StableNaming.TryParseSetIdFromFolderName` 从**文件夹名开头的整数**解析 set id。

用 set id 而不是名字匹配，是因为两边的命名**来源不同**：stable 用 osu 文件里的
`Artist - Title` 原文，lazer 用数据库 metadata。典型差异：

```text
stable: {id} Artist - Title feat Name
realm : {id} Artist - Title feat. Name      ← metadata 多了句点
stable: {id} ColorsSlash - ...
realm : {id} Colors_Slash - ...             ← metadata 里有下划线
```

按名字比会误判成"两边各有不同谱面"，按 set id 比才能正确识别为同一个谱面集。
命中后**复用 stable 的文件名**并补齐缺失文件，绝不另建一个。

### 边界情况

- **无数字前缀的老文件夹**：2007–2009 年左右的命名，解析不出 set id，因而不会被合并。
  实测存在少量这类文件夹。想彻底解决需要读 stable 的 `osu!.db`（见 §8）。
- **同一 set id 出现在多个文件夹**：真实数据里出现过（重复谱面集），
  目前两个都处理，不做特殊合并。
- 报告里 `packagesReusedExistingFolder` 表示命中并复用了已有文件夹的数量，这是判断
  "会不会产生重复谱面"的核心指标。

---

## 6. 文件创建策略（`FileRestorer`）

判定顺序：

1. 源不存在 → `SourceMissing`，不写入；
2. 源内容 SHA-256 与它的文件名不一致 → `SourceHashMismatch`，不写入（除非 `--no-verify-hash`）；
3. 目标存在且与源**同 inode** → `AlreadyLinked`，跳过；
4. 目标存在但内容不同 → 默认 `SkippedExisting` + 记入 notices，`--overwrite` 才替换；
5. `--dry-run` → `PlannedOnly`，不做任何写入；
6. **先创建父目录**，再创建文件。

### 两个必须记住的文件系统事实

1. **`CreateHardLink` 不会隐式创建父目录**（`File.Copy` 会）。
   Realm 里的文件名可以带子目录（`SB/names/x.png`、`mikodance/md0.png`），
   父目录不存在时返回 `ERROR_PATH_NOT_FOUND (3)`。
   历史 bug：只建了谱面集根目录，导致几万个嵌套文件全部失败。
   现在 `FileRestorer` 按目录缓存并统一 `Directory.CreateDirectory`。
2. **NTFS 每文件硬链接上限 1024**。被上千个谱面共享的打击音（例如 `soft-sliderslide`）
   必然撞上上限，`CreateHardLink` 返回 `ERROR_TOO_MANY_LINKS (1142)`，只能复制。
   这不是错误，`CopiedAfterLinkFailure` 是预期结果。

### 计数口径（容易写错）

- `Bytes`（bytes referenced）：所有**计划**处理的文件逻辑大小之和。
- `BytesWritten`（bytes actually consumed）：**只统计真正新占用的空间**。
  新建硬链接 = 0；复制 = 文件大小；替换已有文件 = 文件大小；演练 = 0。
  历史 bug：把硬链接也算进去，导致报告虚高到与逻辑总量相同。

---

## 7. 安装位置探测（`InstallLocator`）

对照 osu! 自身实现：

- lazer：各平台默认数据目录（Windows 为 `%APPDATA%\osu`）→ 各磁盘根目录的**直接子目录**，
  找含 `client.realm` 的；
- stable：注册表 `osustable.File.osz` / `osu!` 的文件关联 → 默认路径
  （`%LOCALAPPDATA%\osu!`、`C:\osu!`、`~/.osu`、`~/osu!`）→ **lazer 配置里记录的 stable 路径**
  → 各磁盘根目录的直接子目录，找含 `Songs/` 或 `osu!.db` 的。

对照的源码位置（ppy/osu 仓库）：
`osu.Desktop/OsuGameDesktop.getStableInstallPath()`、`osu.Game/IO/StableStorage.locateSongsDirectory()`。

注意点：

- 匹配 stable 时必须校验 `Songs/` 或 `osu!.db` 真的存在，否则一个空的残留目录
  （例如空的 `%LOCALAPPDATA%\osu!`）会被误报成安装。
- stable 的 Songs 位置可能被 `osu!.{用户名}.cfg` 里的 `BeatmapDirectory` 改写，
  必须是绝对路径才生效，相对路径相对 stable 根目录解析 —— 与 osu! 行为保持一致。
- 只扫描磁盘根目录的**一层**，避免在大磁盘上耗时。

---

## 8. `osu!.db` 读取器：未完成，不要信任

`Cli/OsuDb.cs` + `osudb` 子命令的目标是读出 stable 自己记录的 folder name
（用于那少数无数字前缀的老文件夹）。

**当前状态：解析不通过。** 难点在于不同 osu! 版本对
(a) 字符串长度前缀（单字节 vs `0x0b` + 7-bit 变长）与
(b) 时间戳字段字节数（7 vs 8）
的写法不一致，一处判断错就会让后续所有字段整体错位。

处理方式（改动时请保持这个原则）：reader 会自我校验 —— 玩家名是否合理、beatmap 数是否合理、
解出的文件夹数是否等于文件头声明的数量 —— 任何一条不满足就抛
`FormatNotUnderstoodException` 并**拒绝输出任何文件夹名**，退出码 6。
**宁可失败，也不要给出看似合理实则错误的结果**：错位的解析会输出错误文件夹名，
而这可能被用来删文件。

调试方法：读文件头前 ~200 字节的十六进制，按字段手工核对偏移，再决定布局假设。
不要靠"读起来像"来推断。

---

## 9. 交互模式（`Wizard`）

流程：**读/建配置 → 自动探测 → 显示当前配置与警告 → 菜单**。

- 配置文件 `stablerestorer.settings.json` 放在 exe 同目录；写不进去则退到
  `%APPDATA%\StableRestorer`。
- 无参数启动**且 stdin 未重定向**时进入交互；重定向（脚本调用）时打印用法，避免挂住。
  显式 `interactive` 始终生效。
- 输出目录不单独设置：目标就是 stable 安装目录（`OutputDirectory == StableDirectory`）。
- 主界面显示硬链接可行性：用 `GetVolumeInformationW` 比较卷序列号，
  不同卷时给出明确警告。
- 输入读取会剥掉 UTF-8 BOM —— 用管道喂输入时很容易带上，否则菜单项会识别失败。
- 真实还原前必须打印摘要并要求确认。

### 交互模式的危险点

"开始还原"会**直接写进真实 stable 安装目录**。用脚本/管道驱动交互模式做测试时，
一定要先把 stable 指向测试目录；如果目录不存在，`PromptDirectory` 会拒绝并**保留原值**，
脚本的后续输入会错位，可能就在真实目录上跑起来（这个坑真实发生过，并产生了重复文件夹）。

---

## 10. 开发与发布命令

```powershell
# 构建
dotnet build StableRestorer.csproj -c Release

# 发布自包含单文件 exe（目标机器无需 .NET）
dotnet publish StableRestorer.csproj -c Release -r win-x64
```

- 发布前先确认没有残留的 `stablerestorer.exe` 进程在运行，否则 exe 被占用，
  打包步骤会报 `Access to the path ... is denied`。
- 发布产物必须包含 `realm-wrappers.dll`。

---

## 11. 测试建议

没有单元测试项目。改动后建议按下面的顺序自查：

```powershell
# 1) 只读体检验证读取层
stablerestorer scan --lazer <lazer数据目录>
#    期望：hashed files missing = 0；文件夹样例的命名符合 {setId} {artist} - {title}

# 2) 演练 + 输出到独立目录，验证映射与护栏
stablerestorer restore --lazer <lazer数据目录> --out <测试目录> --stable <stable目录> --dry-run

# 3) 与 CLI 输出交叉核对（交互模式的服务对象是同一套引擎）
stablerestorer restore --lazer <lazer数据目录> --out <stable目录> --stable <stable目录>

# 4) 核对报告与磁盘
#    - packages 数 == Songs 下文件夹数
#    - filesPlanned - 被跳过的文件 == Songs 下文件数
#    - 逐包 linked + copied + skipped + missing + failed == files
#    - failures = 0
```

独立验证手段：

```powershell
# 硬链接是否真的建立（同一内容出现在还原目录与 lazer 曲库）
fsutil hardlink list "<还原目录>\Songs\<某文件夹>\<某文件>"

# 内容抽查：随机取文件算 SHA-256，确认能在 lazer 曲库里找到同名哈希文件
Get-FileHash "<还原目录>\Songs\<文件夹>\<文件>" -Algorithm SHA256

# 是否会重复：命中复用的包数应等于 set id 两边都有的数量，且不产生同名新目录
```

### 脚本驱动的注意事项

- **不要用 `| Select-Object -First N` 截断输出**：PowerShell 会提前关闭管道并终止进程，
  JSON 报告可能没写完就退出。要完整输出用 `| Out-String -Width 200`。
- 交互模式用管道喂输入时注意 BOM 与提示行数量，行数对不上会导致输入错位（见 §9）。
- 中文文本文件**不要**用 `Get-Content`/`Set-Content` 往返改写：在非 UTF-8 默认代码页的
  PowerShell 里会把 UTF-8 当 GBK 读，整份文件变乱码。用文件编辑工具直接读写。

---

## 12. 数据侧已知事实（量级参考，具体数值请自行 scan）

这些是理解行为有用的**量级**，不是硬编码常量：

- 大量哈希文件只被少量谱面引用，同时少数几个打击音被上千个谱面共享 ——
  这正是 NTFS 1024 上限会触发、而触发后复制成本很低的原因。
- 会有相当比例的文件因为链接上限而复制，但它们通常都是几十字节的小文件，
  总占用通常在 MB 级。
- `files/` 里存在**不被数据库引用**的孤儿文件（已删除谱面、旧版本残留）。
  数据库里没有它们的文件名，**无法还原**，这不是缺陷。
- 少数谱面集的完整路径会超过 260 字符（长标题 + 深子目录）。在未开启长路径支持的系统上，
  stable 可能读不到这些文件。

---

## 13. 未完成 / 后续方向

- 皮肤（`Skins/`，注意 `Protected = true` 是内置皮肤应跳过）、回放（`Score.Files` → `.osr`）、
  `collection.db`：Realm 模型已声明，扩展 `RestoreEngine` 即可。
- 生成 `osu!.db`：未做。依赖 §8。
- 清理/回滚：未做。因为用的是硬链接，删目录不会影响 lazer 数据，可以安全手工删。
- 交互模式补充"预览将要跳过的谱面集清单"，减少误操作。
- 性能：单线程顺序执行，主要耗时是逐个源文件的 SHA-256 校验。
  同一哈希被多张谱面引用时会重复校验，可以按哈希去重。
