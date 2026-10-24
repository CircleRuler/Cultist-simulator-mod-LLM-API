# LLM_API — 让外部程序（大模型 / agent）操作《密教模拟器》

在游戏进程内挂一个本地 HTTP API，使任何外部程序都能**读取当前局面**、**把卡牌放进卡牌框**、**点击开始**、**移动卡牌位置**。

- 游戏版本：2026.1.g.2（Steam，AppID 718670），Unity 2022.3.62f3，Mono
- 模组 ID：`LLM_API`（目录名 / DLL 名 / 入口类名三者一致，符合官方加载约定）
- 监听地址：`http://127.0.0.1:8710/`（仅绑定回环地址，不对外网开放）

## ⚠️ 前置条件：必须启用 GHIRBI, the Gatekeeper

**没装这个模组，本项目的 DLL 一行都不会被执行。** 这是最容易卡住的一步，所以放在最前面。

游戏对 DLL 模组有硬闸门。`SecretHistories.Main.dll` 里 `ModManager.LoadModDLLs()` 的第一条指令就是：

```csharp
call Safety::IsDLLAllowed
brtrue → 继续加载所有 DLL
        (为假则直接 ret，静默跳过全部 DLL 模组)
```

判定逻辑（`ModManager/Safety` 类反编译）：

```csharp
bool IsGatekeeper(Mod mod) {
    return mod.Name == "GHIRBI"
        && mod.Description == "WARNING! Enabling this permits execution of third-party code by DLL mods. "
                            + "Use only trusted DLL mods; and still at your own risk.";
}
bool IsDLLAllowed() => GetEnabledModsInLoadOrder().Any(IsGatekeeper);
```

闸门关闭时游戏会走 `DisableAllDLLMods()`，把每个 `HasValidDLLFolder` 的模组**逐个强制停用**，并在日志里写：

> `Disabling DLL mod '…', as DLL modding isn't allowed. In order unlock it, install and enable GHIBRI mod - it can be found on Steam Workshop.`

### 安装步骤

1. 订阅 [GHIRBI, the Gatekeeper（创意工坊 2901287611）](https://steamcommunity.com/sharedfiles/filedetails/?id=2901287611)，等 Steam 下载完（`D:\SteamLibrary\steamapps\workshop\content\718670\2901287611\`）
2. **先启动 Steam 客户端，再从 Steam 启动游戏** —— 见下面的「Steam 必须运行」
3. 确认 `mods.txt` 里有两行（GHIRBI 的 ID + 本模组 ID）：

   ```
   2901287611
   LLM_API
   ```

   `install.ps1` 会在文件不存在时播种，但**不会**替你打开 GHIRBI；这一步可以直接写文件，也可以在游戏内模组菜单里勾选。
4. **重启游戏**（DLL 只在启动时加载一次）

### ⚠️ Steam 必须运行，否则连模组菜单都是空的

这是最容易误判的一点。`CatalogueMods()` 的**第一条指令**就依赖 Steamworks：

```csharp
call Watchman::Get                       // StorefrontServicesProvider
brfalse → ret                            // 为 null 直接返回，一个目录都不建立
...
CatalogueModsInFolders(GetSubscribedItems().Select(m => m.Folder), ...)
```

**Steam 没运行时 storefront provider 为 null，整个模组目录根本不会建立。** 后果是：

- 游戏内模组菜单看不到创意工坊模组（你会在那里"找不到 GHIRBI"）
- `mods.txt` 里所有 ID 都被判为"不在目录里"，日志刷出：

  ```
  Problem getting enabled mod lists: LLM_API
   was found in the enabled loading order list, but not in the catalogue
  ```

这条报错**不代表 `mods.txt` 写错了**（我一开始就是这样误判的）——它通常意味着 Steam 没跑、目录没建立起来。

### 为什么"没启用"就等于"没加载"

`LoadModDLLs()` 只看一件事：

```csharp
call Safety::IsDLLAllowed    // = GetEnabledModsInLoadOrder().Any(IsGatekeeper)
brtrue → 继续，遍历启用列表挨个 TryLoadDllsForMod
        (为假则直接 ret)
```

是 `Any`，**不是顺序敏感判断**——GHIRBI 出现在启用列表的任何位置都能开门，`mods.txt` 里排前面只是好习惯，不是硬要求。

反过来，GHIRBI 一旦没在启用列表里，整个 DLL 加载环节直接返回，**你的模组连自己的日志目录都不会创建**（`Initialise()` 根本没被调用）。所以"日志是空的"本身就是闸门没开的特征。

### 启用状态存在哪里

`_enabledModsLoadOrder` 就是 `mods.txt` 的内容（`GetEnabledModsLoadOrderFromFile().ToList()`），而 `CatalogueMod` 用 `Enabled = _enabledModsLoadOrder.Contains(Id)` 决定启用。

也就是说：**`mods.txt` 的行 == 模组菜单里勾选的项目**，两者是同一份状态。所以既有两种做法：

- 在游戏内模组菜单里勾选（游戏随后把结果写回文件）
- 直接编辑 `mods.txt`（**前提是 Steam 在运行**，否则目录为空、ID 匹配不上）

模组 ID 的来源：`CatalogueMod` 用 `Path.GetFileName(modFolder)` 作 ID，所以创意工坊模组的 ID 就是它的文件夹名（GHIRBI = `2901287611`）。

**⚠️ `mods.txt` 只在启动时读一次。** 改它必须在启动游戏**之前**完成。实测教训：我在游戏已启动之后 12 秒才写入，那一轮会话读到的仍是旧内容，白等一轮——日志时间戳与文件 mtime 对不齐时，优先怀疑这一点。

### 这个闸门是什么性质

按作者 Alexis Kennedy 的[官方说明](https://weatherfactory.biz/ghirbi-the-gatekeeper/)，GHIRBI **内部没有任何逻辑**，唯一作用是显示一条"你确定要运行第三方代码吗"的提示，定位是 *"basically the same as making the user tick a box"*——起因是 2022 年 Cities: Skylines 出过模组恶意代码事件。它是一次**知情确认**，不是技术性安全边界。

## 为什么这样做是可行的

`MODDING_README.txt:238-252` 说明游戏**官方支持自定义 DLL 加载**。因此不需要 BepInEx、不需要注入、不需要内存修改。

### ⚠️ DLL 的落盘位置与文件名是硬约束（踩过）

这是本项目最坑的一点，**做错了没有任何日志提示**——游戏不报错、不加载、`Initialise()` 一次都不调用。

`ModManager.TryLoadDllsForMod()` 的 IL：

```csharp
string path = Path.Combine(mod.ModRootFolder, "dll", mod.GetNameAlphanumericsOnly() + ".dll");
if (!File.Exists(path))
    path = Path.Combine(mod.ModRootFolder, "dll", "main.dll");   // 兼容旧写法
if (!File.Exists(path)) return;                                   // ← 静默放弃
TryLoadAssembly(path);

// GetNameAlphanumericsOnly():
Regex.Replace(mod.Name, "[^a-zA-Z0-9_]+", "")                     // 用的是 Name，不是 Id
```

由此得出四条硬约束：

1. **DLL 必须放在 `<模组目录>/dll/` 子目录**，不是模组根目录
2. **DLL 文件名必须等于 `synopsis.json` 的 `name` 去掉所有非字母数字字符**
3. **入口类必须位于全局命名空间**（见下），且类名等于 `name` 去特殊字符
4. `synopsis.json` 的 `name` 应当就直接写成模组 id（`LLM_API`）；好看的展示名放 `description`

### ⚠️ 入口类必须在全局命名空间（第二个坑）

`TryInitialiseAssembly()` 的 IL：

```csharp
string className = GetNameAlphanumericsOnly();   // "LLM_API"
Type type = LoadedAssembly.GetType(className);   // 注意：不带命名空间
```

`Assembly.GetType("LLM_API")` **只匹配类型的全名，不会去搜索命名空间**。因此：

```csharp
namespace LLM_API { class LLM_API { } }   // 全名 LLM_API.LLM_API → GetType 返回 null
class LLM_API { }                            // 全局命名空间 → GetType 命中 ✅
```

写进命名空间里时，游戏日志只会记一句 `Can't find a class named LLM_API`，模组照旧不运行。所以本项目把入口类放在全局命名空间，其余辅助类型放在 `CsGameApi` 命名空间（入口文件里加 `using CsGameApi;` 即可无限定使用）。

`MODDING_README.txt` 只说了 "must contain a class with the same name as the mod"，
**没有提命名空间**——这是它最容易误导人的地方。

### 三条约束合起来的样子

```
mods/LLM_API/                            ← Mod.Id（文件夹名）
  synopsis.json        name = "LLM_API"
  dll/LLM_API.dll                        ← 文件名 = name 去特殊字符
                       class LLM_API     ← 全局命名空间，Assembly.GetType("LLM_API")
                       public static void Initialise()
```

`install.ps1` 现在按这个结构安装，并会删掉放错位置的根级 DLL 副本。

### ⚠️ synopsis.json 不能含 `\uXXXX` 转义（踩过，会崩游戏）

游戏解析 `synopsis.json` 用的是自己那个极简的 `SimpleJsonImporter`，**它不支持 Unicode 转义序列**。而 Windows PowerShell 5.1 的 `ConvertTo-Json` 会把撇号转义成 `\u0027`，于是：

```
03:37: Failed to fully parse JSON:
Unknown escaped sequence: '\u'
   at SimpleJsonImporter.Import(...)
   at ModManager.CatalogueMod(...)
>>>>>>>> 03:37: CRASH!!! UH O! LOADING UH O SCENE!!!
```

注意触发的**不一定是中文**——一句英文里的 `'` 就够了。之前一直没出问题，只是因为旧的 synopsis 恰好全是 ASCII 且没有撇号。

`install.ps1` 的处理：序列化后把每个 `\uXXXX` 还原成真实字符，用**无 BOM** 的 UTF-8 写入，然后做两道自检（残留转义 → 抛错；`ConvertFrom-Json` 失败 → 抛错）。**任何解析问题都在安装阶段暴露，而不是留到玩家那里变成 UHO。**

### ⚠️ 声明依赖 GHIRBI：可以做到，但写错会让游戏崩溃

`synopsis.json` 支持 `dependencies` 字段：

```json
"dependencies": ["2901287611"]
```

语义（来自 `PopulateFromSynopsis` 与 `CatalogueMods` 的 IL）：

```csharp
// 语法：\w+ —— 因此依赖名不能含点号
Regex("^\\s*(\\w+)(?:\\s*(<=|<|>=|>|==)\\s*([\\d.]+))?\\s*$")
Dependency.ModId = <第一捕获组>            // 拿它去 _cataloguedMods 里查
Version          = <第三捕获组>             // 可省略 → 得到一个 null 的 System.Version

// 校验通过后才比较版本；VersionOperator 走 switch
```

**依赖名必须是 GHIRBI 的目录 id `2901287611`，不是 `GHIRBI`。** `CatalogueMod` 用 `Path.GetFileName(modFolder)` 当 `Id`，GHIRBI 来自创意工坊、文件夹名就是它的发布 id，`GHIRBI` 只是显示名。

#### 我实测踩到的坑：光写 id 不带版本会让游戏崩

```json
"dependencies": ["2901287611"]        // ← 这个会让游戏进入崩溃场景
```

复现日志：

```
>>>> 02:54: Dependency '2901287611' for 'LLM_API' has incompatible version
>>>>>>>> 02:54: CRASH!!! UH O! LOADING UH O SCENE!!!
  at SecretHistories.Services.StageHand.LoadInfoScene ()
  at SecretHistories.Infrastructure.Modding.ModManager.CatalogueMods ()
```

**关键教训：依赖判定失败不是"只是一条警告"。** 我最初只读了 `LogWarning` 那一支，就下了"不致命"的结论——**错了**。`CatalogueMods` 在这个分支会调用 `StageHand.LoadInfoScene()` 载入 **"UH O" 崩溃场景**，玩家的这一局就没了。而模组本身其实**照常加载成功**（同一份日志里有 `Successfully invoked Initialise()`），所以这是个纯粹的副作用损失。

原因分析：`switch` 的 case 排布（IL 里 `switch [IL_023F, IL_0251, IL_0263, IL_0275, IL_0287]`）与
`DependencyOperator` 的枚举顺序一一对应：

```
LessThan=0 → IL_023F    LessThanOrEqual=1 → IL_0251   GreaterThan=2 → IL_0263
GreaterThanOrEqual=3 → IL_0275                          Equal=4 → IL_0287
```

只写 `"2901287611"` 时正则的操作符捕获组为空，而 `Enum.Parse` 无法把空串转成枚举——于是**落到了 `LessThan`**（第一项），拿 GHIRBI 的 `1.0` 去比一个不存在的版本，判定不兼容。**换句话说，不带操作符的依赖声明等于"要求版本小于某值"，几乎必然失败。**

这也给出了可用的写法：显式带上操作符与版本号，例如

```json
"dependencies": ["2901287611 >= 1.0"]
```

**因此本项目的默认行为是不写 `dependencies`。** GHIRBI 本来就是 DLL 闸门的硬前置（没它模组一行都不跑），这个事实已经写进模组 README、并在 API 的 503 错误信息里说明，再声明一遍收益很小，而代价可能是玩家掉进崩溃场景。

要自己试验的话：`install.ps1 -GatekeeperId '2901287611 >= 1.0'`，但**务必先在一局无关紧要的存档里验证**。

> 另外注意：Steam 创意工坊页面右侧那份"必需物品"清单，是在**工坊上传页面单独设置**的，不从这个字段读。这个字段只影响游戏自己的校验。

### 自查工具

`tools/LoadCheck.cs` 复刻了游戏的加载流程（路径推算 → `LoadFrom` → `GetType` → 找 `Initialise`），
**不启动游戏**就能验证一遍：

```powershell
mono.exe tools\bin\LoadCheck.exe "<LocalLow>\...\mods" LLM_API
```

输出 `RESULT: the loader would find and load this mod.` 才算布局正确。

我最初把 `name` 写成 `"CS Game API (external control for AI agents)"`，于是加载器去找
`dll\CSGameAPIexternalcontrolforAIagents.dll`，而实际文件叫 `LLM_API.dll` → `File.Exists` 为假 → 直接返回。

入口类的查找也用同一个函数（`TryInitialiseAssembly` → `Assembly.GetType(GetNameAlphanumericsOnly())`），
所以 `name` = `LLM_API` 时，**模组目录名、DLL 文件名、类名三者自动一致**：

```
mods/LLM_API/                      ← Mod.Id（文件夹名）
  synopsis.json      name = LLM_API
  dll/LLM_API.dll                  ← 文件名 = name 去特殊字符
                     class LLM_API ← Assembly.GetType("LLM_API")
```

`install.ps1` 现在按这个结构安装，并会删掉放错位置的根级 DLL 副本。

### 加载流程

```
/[modfolder]/dll 里放 <modid>.dll
 → 内含与模组同名的类
 → 该类有 public static void Initialise()
```

游戏在主程序初始化时加载它，之后"DLL 里你可以做任何事"。本模组就在 `Initialise()` 里起了一个 HTTP 服务，并挂一个 `MonoBehaviour` 做**主线程命令泵**——因为 Unity 的游戏对象只能在主线程碰。

```
┌──────────────┐   HTTP/JSON    ┌──────────────────────────────┐
│ 大模型 / agent │ ─────────────▶ │ LLM_API (游戏进程内)        │
│ curl / Python │ ◀───────────── │  后台线程：HttpListener        │
└──────────────┘                │      ↓ ConcurrentQueue        │
                                │  Update() 主线程泵             │
                                │      ↓                        │
                                │  FucineRoot / HornedAxe 真实 API│
                                └──────────────────────────────┘
```

## 卡牌的坐标系：FucinePath

游戏内部用 `FucinePath` 字符串给每个容器寻址，API 直接沿用这套坐标，不做任何翻译。真实存档里的例子：

```
~/tabletop                              桌面
~/tabletop!work_0                       「作业」这个动词（Situation）
~/tabletop!work_0/work                  该动词的「作业」卡牌框（ThresholdSphere）
~/tabletop!work_0/notessphere           该动词的提示栏
```

`!work_0` 是动词的实例 id，`work` 是卡槽的 spec id。**所有动作端点都用这种路径寻址。**

## 安装

```powershell
# 位于 F:\WorkSpace\cs_gameapi
powershell -ExecutionPolicy Bypass -File .\build.ps1   -Source .\src\LLM_API -Name LLM_API
powershell -ExecutionPolicy Bypass -File .\install.ps1 -Name LLM_API
```

`install.ps1` 会把 DLL 复制到 `<LocalLow>\Weather Factory\Cultist Simulator\mods\LLM_API\` 并登记到 `mods.txt`。改完配置需重启游戏（DLL 在启动时加载，热重载不生效）。

## HTTP 接口

| 方法 | 路径 | 请求体 | 作用 |
|---|---|---|---|
| GET | `/state` | — | 完整局面：球体、卡牌、坐标、动词、卡槽 |
| GET | `/ping` | — | 存活检查 |
| GET | `/help` | — | 服务器自述路由表 |
| POST | `/action/place` | `{"token": "...", "slot": "..."}` | 把卡牌放进卡牌框 |
| POST | `/action/start` | `{"token": "..."}` | 点击「开始」（动词启动） |
| POST | `/action/move` | `{"token": "...", "to": "..."}` 或 `{"token":"...","x":0,"y":0}` | 移动卡牌（坐标见下） |
| POST | `/action/click` | `{"token": "..."}` | 卡牌的主交互（动词开/关窗口） |

### 关于移动卡牌的坐标（`x` / `y`）

`/action/move` 有三种用法，**可以组合**：

| 传参 | 行为 |
|---|---|
| 只传 `to` | 把牌移到目标球体，落点由该球体自己决定（桌面会用编排器找空位散开） |
| 只传 `x`/`y` | 在当前球体内挪到该坐标，走行程动画（`via: itinerary(x,y)`），**落点精确** |
| `to` + `x`/`y` | 移到目标球体，坐标交给该球体的行程使用 |

**坐标是球体局部坐标，不是屏幕像素。** 以桌面为例，原点在中心，可见范围大致 `x ∈ [-900, 900]`、`y ∈ [-600, 600]`。实测 `x=300, y=-200` 落点就是 `{300, -200}`。

**一个坑：`x`/`y` 只在"自己管理布局的球体"上生效。** 实测移到 `~/tabletop!work_0/notessphere` 这类子球体时，坐标被忽略、牌落在 `{0,0}`。所以精确坐标只在桌面这类球体上有意义；往卡槽里放牌请用 `/action/place`（它走的是性相匹配，不是坐标）。

移动是**动画过程**，途中牌会在 `~/enroute`，之后才落到目的地。要判断是否落定，看 `/state` 的 `tokensInTransit` 是否为空，或轮询该牌的 `spherePath`。响应里的 `via` 字段会说明这次走的是哪条路径。
| GET | `/legacies` | — | 可开局的传承列表，以及 `newgame` 会默认选哪个 |
| POST | `/action/newgame` | `{"legacy": "..."}`（可省略） | **从主菜单直接开新局** |
| POST | `/action/continue` | — | **从主菜单直接继续存档** |
| POST | `/action/menu` | — | **离开牌桌回主菜单**（配合 `newgame` 即可重开一局） |
| POST | `/action/unpause` | — | **解除暂停并恢复正常速度** |
| POST | `/action/speed` | `{"speed": "Normal"}` | 按 `GameSpeed` 名设速度（`Normal` / `Fast` / `Paused` …） |
| POST | `/action/conclude` | `{"token": "..."}` | **关掉动词窗口**（窗口右上角的 X） |
| POST | `/action/collect` | `{"token": "..."}`（可省略） | **一键收取**输出框里的牌到桌面，并合并同类卡 |
| POST | `/action/tidy` | — | **整理牌桌**：同种牌的多坨合并成一坨（游戏的「整理」按钮） |
| POST | `/action/retire` | `{"token": "..."}` | 移除一张 API 已无法操作的残留牌 |
| POST | `/action/closeworld` | — | 收起漫宿等外域覆盖层（兜底） |
| POST | `/action/closewindows` | `{"except": "..."}`（可省略） | 一次关掉所有动词窗口 |

### ⚠️ 开过的动词窗口要记得关，否则它会一直挡着

`/action/start` 会把动词窗口打开，而**打开的窗口会盖住牌桌、并且压住时钟**。正常玩的时候玩家会点右上角的 X 关掉，对应的游戏方法就是：

```csharp
Situation.Conclude()      // 游戏自己的"全部收取"热键对打开的动词调用的也是它
```

新增了 **`POST /action/conclude {"token":"!work_0"}`**。

**注意它是"入队"而不是"立即生效"，而且有前置条件。** `Situation.Conclude()` 的真实实现是：

```csharp
if (!State.IsConcludable) return;          // ← 前提：动词必须已经跑完
AddCommand(new EvictOutputCommand());      //    顺带把输出框里的牌清出去
AddCommand(new ConcludeCommand());         //    关窗
Continue();                                //    入队，要等后面某一拍才执行
```

**两个时机陷阱，实测都踩过：**

| 情况 | 结果 |
|---|---|
| 动词仍在 `Ongoing` 时调用 | ❌ `IsConcludable` 为 false，**一个命令都不入队、静默返回**——窗口永远开着 |
| 动词已 `Complete` 时调用 | ✅ 命令入队，**约 20 秒后**窗口才从 `openWindows` 消失 |

所以这个端点会先检查前提并如实报告：

- 不可结束时 → `success:false`，`concludable:false`，并说明"等它 Complete 再调"
- 可结束时 → `queued:true`，**不要**拿调用后的瞬间状态当结论，要**轮询 `/state` 的 `openWindows`** 确认目标消失

`/state` 里每个动词都带 `concludable` 字段，调用方直接据此判断时机，不必自己猜。

**另一个独立现象**：关掉窗口和解除暂停是两件事。实测见过 `openWindows=[]` 但 `paused=true`——**没有窗口也可能处于暂停**，所以看到计时器不动时，看 `playback.paused` 而不是 `openWindows`。

**给其他大模型的提醒**：开窗之后请主动关一次。驱动脚本 `play/play-session.ps1` 已内置：等动词 `concludable` 后自动 `conclude`，并在后续轮次确认它真的从 `openWindows` 消失。

### ⚠️ 收取的卡会自动摆放并合并，不再叠成一堆

早先的 `move` 用不带落点的移动，结果所有收集来的牌都落在**同一个坐标**，叠成看不清的一团。现在分两步走游戏自己的机制：

```csharp
Sphere.TryAcceptToken(token, context)                                  // 1. 球体收下（换父节点）
TabletopChoreographer.PlaceTokenAtFreeLocalPosition(Token, Context)    // 2. 在网格上找空位摆放
TabletopChoreographer.GroupAllStacks()                                 // 3. "叠放同类卡"按钮
```

**两步缺一不可**：`PlaceTokenAtFreeLocalPosition` 只设 `localPosition`，它**不会把牌从别的球体挪过来**——只调它，牌会留在原地，看起来"成功"了却没动。而只调 `TryAcceptToken`，牌虽然进了桌面，却全落在同一个默认坐标。

`/action/move` 与 `/action/collect` 的响应都带 `via` 字段说明走的哪条路（`TryAcceptToken+PlaceTokenAtFreeLocalPosition` / `itinerary` / `itinerary(x,y)`）以及 `groupedStacks`。**指定 `x`/`y` 时仍然按你给的坐标放**，自动摆放只用于没指定坐标的情况。

### ⚠️ 时钟可能停住——`/state` 的 `playback` 块

**这是实测踩到的最严重的一个坑。** 时钟只在游戏未暂停时推进，而暂停**完全不可见**：一次纯 API 驱动的对局里，所有动词计时器同时冻结，`/state` 却照常返回，没有任何字段说明原因——调用方会以为只是"慢"，然后一直等下去。

`/state` 现在带一个 `playback` 块：

```json
"playback": {
  "gameSpeed": "Paused", "gameSpeedValue": 0, "paused": true,
  "metapaused": false, "forcePaused": false,
  "playerInputDisabled": false,
  "hint": "the game clock is not advancing because the effective speed is 0. ..."
}
```

判据就是 `GameSpeedState.GetEffectiveGameSpeed()` —— **0 即暂停**。另有 `openWindows` 数组列出当前打开的动词窗口（开着窗口会压住牌桌）。

恢复用 `POST /action/unpause`，它会依次尝试：解除 force-pause、`Heart.Unmetapause()`、`EnablePlayerInputAndUnmetapause()`，最后像「继续」按钮那样推一个 `Normal` 速度，并**回报实际生效的速度**（而不是只回报请求）。

#### 实测：`continue` 进入旧档后游戏是暂停的

这是那个静默停摆的真正原因——**不是任何 API 操作造成的**：

```
POST /action/continue → 进入牌桌后 playback = {"gameSpeed":"Paused","paused":true}
POST /action/newgame  → 进入牌桌后 playback = {"gameSpeed":"Normal","paused":false}
```

暂停时的读数是 `gameSpeedValue:0` 并附带 `hint`。**调用方用 `continue` 进档后应当立即 `unpause`**，否则会一直等一个不动的钟。

同时确认了 `/action/start` **不会**暂停游戏：

```
place !legacydetectivejob_4 → work 槽   200 accepted
start !work_0                           200 recipe=workdetectivejob_start
t0  !work_0=9.1          paused=False
t15 !work_0=0.0(Complete) !time_12=54.0 !dream_14=24.0   paused=False
```

`GameSpeed` 的合法取值（由游戏枚举给出）：`Paused` / `Normal` / `Fast` / `VeryFast` / `VeryVeryFast` / `DeferToNextLowestCommand`。

### 重开一局：`menu` + `newgame`

```
POST /action/menu    → 200  via GameGateway.LeaveGame
                     → 3 秒后 scene=S3Menu，tabletopHasCards=False
POST /action/newgame {"legacy":"detective"}
                     → 3 秒后新局就位，tokens=7，playback=Normal
```

在这之前，牌桌是**只进不出**的：`newgame` 需要菜单场景，而回到菜单没有任何接口。现在这条路通了。

响应统一为 JSON，失败时带 `"success": false` 和 `"error"`，HTTP 状态码同步（400 参数错、404 找不到、409 状态不对、504 主线程超时）。

**`/action/start` 会如实报告"有没有真的启动"**：`TryStart` 在当前配方不可合成时是个空操作，早期版本对此也返回 `success: true`，害得驱动器无限重试同一个按钮。现在带 `changed` 字段：

```json
// 真的启动了
{"success": true,  "changed": true,  "stateBefore": "Unstarted", "stateAfter": "Ongoing",
 "recipeAfter": "workhealth_noinjurychance"}

// 没启动，如实说明原因
{"success": false, "changed": false, "stateAfter": "Unstarted",
 "reason": "TryStart did not begin the verb: the current recipe (nullrecipe) is not craftable with what is in its slots"}
```

### 真实游戏内验证结果

已在 Cultist Simulator 2026.1.g.2 的实际存档上逐项跑通（`verify-live.ps1`）：

| 端点 | 结果 |
|---|---|
| `GET /state` | ✅ 34 球体 / 13 卡牌，含真实数量（`funds` qty=11）、活计时器（`study_16` `t=30.0`）、卡槽占用 |
| `POST /action/place` | ✅ 把 `!health_6`（`ability=1`）放进 `~/tabletop!work_0/work`（要求 `ability`）→ `accepted: true`，卡牌与卡槽状态双向确认 |
| `POST /action/start` | ✅ `!dream_9`：`Unstarted` → `Ongoing`，游戏自行推导配方 `workhealth_noinjurychance`、`timeRemaining=60.0`，并给动词加了提示牌 |
| `POST /action/move` | ✅ 成功；详见图下说明 |
| `POST /action/click` | ✅ 返回真实卡牌（非 `NullElementStack`） |
| `GET /legacies` | ✅ 返回 **29 个传承**，默认 `aspirant`（有志青年）标注正确 |
| `POST /action/newgame` | ✅ **从主菜单零点击开局**：`{"legacy":"detective"}` → 3 秒后牌桌建好，出现警探专属卡 `!legacydetectivejob_4`「督察的职位」 |
| `POST /action/continue` | ✅ **从主菜单续档**：`via: MenuScreenController.ContinueGame` → 界面切到 `S4Tabletop`，续上上一局 |
| `GET /screen` | ✅ 菜单 `S3Menu` / 牌桌 `S4Tabletop` 识别正确 |
| 错误处理 | ✅ 未知 token → 404、对非动词 `start` → 409、未知球体路径 → 404 |

**放卡会不会被接受，取决于卡牌性相与卡槽要求。** 把「资金」放进「作业」槽会被游戏正确拒绝（`accepted: false`），因为作业槽要求 `ability/benefactor/desire/ritual/job` 之一，而资金只有 `funds/ingredient`。这不是 API 的故障——`/state` 里每个卡槽都带 `required` 与 `forbidden`，调用方应据此配对。

**卡牌移动会经过游戏中转球体。** 移动时卡牌先进入 `~/enroute`，等动画播完才落到终点。若在动画中途读 `/state`，会看到卡牌位于 `~/enroute`——所以响应里单列了 `tokensInTransit`，调用方应把该状态视为"在途"，等待其离开后再判断落点。

## AI 驱动器（`play/`）

这个模组是写给 AI 用的，所以除了客户端还带了一个**参考驱动循环**——它演示了一个外部 agent 该怎么玩：

| 文件 | 作用 |
|---|---|
| `play/lib.ps1` | 共享库：HTTP、读局面、**按游戏自己的性相规则找合法配对**、找输出牌 |
| `play/play-session.ps1` | 会话驱动：每轮 读局面 → 必要时解除暂停 → 收取输出 → 落一张合法牌并启动 → 报告异常 |

```powershell
powershell -ExecutionPolicy Bypass -File play\play-session.ps1 -Minutes 10
powershell -ExecutionPolicy Bypass -File play\play-session.ps1 -Minutes 3 -DryRun      # 只看不动
powershell -ExecutionPolicy Bypass -File play\play-session.ps1 -NewGame detective      # 先重开一局
```

它每轮做四件事，正好对应 agent 所需的全部能力：

1. **读局面**，并打印 `cards / verbs / running / paused / speed`
2. **时钟若暂停就解除**——`continue` 进档后游戏是暂停的，不处理会一直等
3. **收取输出帧里的牌**（`OutputSphere` → 桌面，这就是"收卡"）
4. **落一张合法牌并点击开始**，用 `required`/`forbidden` 配对

`lib.ps1` 里所有函数都不依赖脚本上下文：同样的代码在文件里和命令行里都能跑。

**已知限制**：`start` 的无操作情况现在会被如实标出（见下），但驱动器**不会**凭空造出牌来填一个它填不了的卡槽——例如 `~/enroute!xxx/shadows wants [inducesdespair]` 这类由恐惧触发的槽，没有产出对应性相卡牌的途径时，驱动只能报告"无牌可配"。

### 一键真机验证`verify-live.ps1` 会在**正在运行的游戏**上把上述断言跑一遍：

```powershell
powershell -ExecutionPolicy Bypass -File verify-live.ps1
```

它用原始 socket 而不是 `Invoke-RestMethod`——后者会把最关键的 JSON 错误正文吞掉。

脚本会先按游戏自己的规则（卡牌性相 vs 卡槽 `required`/`forbidden`）寻找一个合法配对；如果当前局面没有可用空槽，会把 `place`/`start`/`move` 标为 **SKIP** 并打印诊断（例如"槽要求 `lore|waywood`，而桌面只有 `funds`/`passion`"），而不是误报失败。**放卡是会被游戏正确拒绝的**，所以在没有合法配对时强行测试毫无意义。

最后一次运行结果：

```
=== 0. liveness ===         PASS ×2
=== 1. read state ===       PASS ×3   (35 球体 / 13 卡牌)
=== 6. /state shape ===     PASS ×2   (rootSpherePath=~, tokensInTransit)
=== 7. error handling ===   PASS ×7   (404 / 409 / 400)
ALL LIVE CHECKS PASSED (passed=13 failed=0 skipped=3)
```

### 从主菜单开局 / 继续

模组可以完全免点击地进入游戏。对应的是菜单按钮自己调用的方法：

| 端点 | 调用的游戏方法 |
|---|---|
| `GET /legacies` | `Compendium.GetEntitiesAsList<Legacy>()` |
| `POST /action/newgame`（不带 legacy） | `MenuScreenController.BeginGameWithDefaultLegacy()` → 取 `Compendium` 里第一个传承 |
| `POST /action/newgame`（带 legacy） | `MenuScreenController.BeginNewSaveWithSpecifiedLegacy(legacyId)` |
| `POST /action/continue` | `MenuScreenController.ContinueGame()` → `StageHand.LoadGameOnTabletop(new DefaultGamePersistenceProvider())` |

```bash
python client/cs_api.py legacies
python client/cs_api.py continue --wait          # 继续存档，等牌桌就绪
python client/cs_api.py newgame --legacy adept --wait
```

两个注意点：

- **加载是异步的。** 这些端点只负责"发起"，返回时牌桌还没建好。要用 `/state` 轮询到返回 200（客户端 `--wait` 就是干这个）。
- **只能在主菜单用。** 已有一局在进行时返回 **409**，避免覆盖当前局面。

`Compendium` 只提供**泛型**的 `GetEntitiesAsList<T>()`，没有无泛型重载——这也是实现时踩到的点：按无泛型写会拿到空列表。

### 对应关系：每个端点调用的游戏方法

| 端点 | 游戏内部真实方法 |
|---|---|
| `/state` | `FucineRoot.Get().GetSpheres()`、`Sphere.Tokens`、`Token.Payload`、`Token.Location`、`Situation.CurrentRecipeId` / `TimeRemaining` |
| `/action/place` | `Sphere.TryAcceptToken(Token, Context)` —— 玩家拖拽落卡走的就是它 |
| `/action/start` | `Situation.TryStart()` —— 就是那个按钮的回调 |
| `/action/move` | `Sphere.GetItineraryFor(Token)` → `TokenItinerary.Depart()`，失败则回退 `TryAcceptToken` |
| `/action/click` | `Situation.OpenAt()` / `Close()`，或 `Token.Select()` |

## 客户端

`client/cs_api.py` 只要标准库：

```bash
python client/cs_api.py ping
python client/cs_api.py state --brief          # 给人/模型看的浓缩局面
python client/cs_api.py slots                  # 列出所有卡牌框及占用情况
python client/cs_api.py place --token '!introjob_2' --slot '~/tabletop!work_0/work'
python client/cs_api.py start --token '!work_0'
python client/cs_api.py move  --token '!introjob_2' --to '~/tabletop'
python client/cs_api.py watch --interval 2     # 局面变化时打印
```

任何语言都能用，等价 curl：

```bash
curl http://127.0.0.1:8710/state
curl -X POST http://127.0.0.1:8710/action/place \
     -H "Content-Type: application/json" \
     -d "{\"token\":\"!introjob_2\",\"slot\":\"~/tabletop!work_0/work\"}"
```

### `/state` 返回结构（节选）

```json
{
  "gameVersion": "2026.1.g.2",
  "sphereCount": 24,
  "tokenCount": 31,
  "spheres": [
    { "path": "~/tabletop", "type": "TabletopSphere", "specId": "tabletop", "tokenCount": 9 },
    { "path": "~/tabletop!work_0/work", "type": "ThresholdSphere", "specId": "work",
      "required": { "ability": 1, "job": 1 }, "tokenIds": [] }
  ],
  "tokens": [
    { "id": "!introjob_2", "payloadType": "ElementStack", "entityId": "introjob",
      "label": "一份工作邀请", "quantity": 1, "spherePath": "~/tabletop",
      "position": { "x": -150.0, "y": 230.0, "z": 0.0 },
      "aspects": { "job": 2 }, "draggable": true },
    { "id": "!work_0", "payloadType": "Situation", "verbId": "work",
      "situationState": "Unstarted", "currentRecipeId": "nullrecipe",
      "timeRemaining": 0.0,
      "slots": [ { "path": "~/tabletop!work_0/work", "specId": "work", "empty": true } ] }
  ]
}
```

## 配置

首次启动会在模组目录生成 `config.txt`：

| 键 | 默认 | 说明 |
|---|---|---|
| `port` | 8710 | 监听端口 |
| `enabled` | 1 | 设 0 则加载模组但不启服务 |
| `directory` | `<LocalLow>\Weather Factory\Cultist Simulator\LLM_API` | 日志与自检报告目录 |
| `selftest` | 1 | 启动后自动跑一遍 读→放→开始→移 并写报告 |
| `selftest_slot_path` / `selftest_token_id` | 空 | 自检指定的卡槽 / 卡牌，留空则自动挑 |
| `request_timeout_ms` | 15000 | 请求等待主线程的上限 |

## 怎么启动游戏（自动化场景必须走 Steam 协议）

**在你自己的机器上点了图标就开的，可以跳过本节。** 但如果是由脚本/agent 来启动游戏（自动测试、CI、批量验证），**必须用 Steam 的 URL 协议**：

```powershell
# 前提：Steam 客户端本身在运行
Start-Process 'steam://rungameid/718670'
```

用 Python 或任意语言时，等价于 `steam://rungameid/718670` 这个 shell-open 调用。

### ⚠️ 直接跑 `cultistsimulator.exe` 会黑屏

实测三次全部失败：窗口一直黑、`Player.log` 一行都不写、或进程起来后自己终止。原因分两层：

**第一层：Steamworks 初始化失败。** 游戏启动时要 `SteamAPI_Init()`。如果启动它的是受限沙箱里的子进程，**命名管道打不开**，日志里会出现：

```
Trying to initialise Steam client provider, but SteamManager isn't initialised
```

然后游戏停在加载画面不动。**容易被误判的地方**：沙箱**允许**写文件，所以 `Player.log` 照样有内容、甚至写得出完整关停统计——看起来"跟沙箱无关"。被挡的是**命名管道**，不是文件。

**第二层：进程归属。** 直接 `Start-Process` 派生的进程随父命令结束而被收掉；用 `steam://` 启动的进程归 Steam 托管，能稳定存活。

### 实测对比（2026-10-17）

| 方式 | 结果 |
|---|---|
| `Start-Process 'steam://rungameid/718670'` | ✅ **5 秒后游戏起来、API 立即可用**，三个模组全 `enabled` |
| 直接跑 `cultistsimulator.exe`（即使有完全权限） | ❌ 三次全败：黑屏 / 日志不写 / 起来后自己终止 |

### 两个注意事项

1. **Steam 客户端必须先在运行**，否则 `steam://` 无人接管。
2. **游戏起来后不要 `taskkill`** —— 进程归 Steam 管，强杀会让 Steam 认为游戏异常退出。

### 启动后怎么确认成功

读 `<LocalLow>\Weather Factory\Cultist Simulator\Player.log`，对这几行：

| 日志行 | 含义 |
|---|---|
| `Initialising Steam client provider` | ✅ 正常 |
| `Trying to initialise Steam client provider, but SteamManager isn't initialised` | ❌ 就是本节这个故障 |
| `Received achievements from Steam` | ✅ 真的进到菜单流程了 |
| `Loaded ...\dll\LLM_API.dll for LLM_API` + `Successfully invoked Initialise()` | ✅ 模组真的跑起来了 |

每次启动会把上一份 `Player.log` 轮转成 `Player-prev.log`，两个都看看。

> 自动化测试时如果想**不污染存档**：游戏大约 40 秒后才会 metapause 存档，**25 秒内结束进程**不会改动 `save.json`（可用杀前杀后的文件哈希比对证明）。

## 启动成功的自查清单

启动游戏后，按顺序看这四项，能精确定位卡在哪一步：

| # | 检查 | 通过的样子 | 不通过说明什么 |
|---|---|---|---|
| 1 | `<LocalLow>\Weather Factory\Cultist Simulator\mods.txt` | 含 `2901287611`（GHIRBI）与 `LLM_API` | 说明模组菜单里没勾选；这两个 ID 由游戏自己写 |
| 2 | `Player.log` 里的两行 | `-- Mod id 'GHIRBI' ... enabled` 和 `-- Mod id 'LLM_API' ... enabled` | 任一为 `disabled` → 闸门关闭，DLL 不会加载 |
| 3 | `<LocalLow>\Weather Factory\Cultist Simulator\LLM_API\LLM_API.log` | 存在，且首行 `=== LLM_API 1.0.1 starting ===` | **文件不存在 = `Initialise()` 从未被调用 = DLL 没被加载** |
| 4 | `http://127.0.0.1:8710/ping` | `{"success": true, ...}` | 有日志但无响应 → 看日志里的 `FAILED to start HTTP server` |

第 3 项是最有价值的判据：**日志文件存在与否，直接等价于"我的代码有没有跑起来"**。它之所以可靠，是因为 `Initialise()` 的第一件事就是写这个文件。

注意第 2 项和第 3 项的区别：**模组"已启用"不等于"DLL 已加载"**。GHIRBI 没启用时，`LLM_API` 照样会显示 `enabled`，但 `LoadModDLLs()` 已经提前返回了——所以我们看的是日志文件，不是启用状态。

### 常见失败与对策

| 症状 | 原因 | 对策 |
|---|---|---|
| 模组菜单里找不到 GHIRBI；日志报 `was found in the enabled loading order list, but not in the catalogue` | **Steam 客户端没运行**，`CatalogueMods()` 提前返回，模组目录没建立 | 启动 Steam，再从 Steam 启动游戏 |
| 日志目录不存在，`Player.log` 里 `LLM_API` 是 `enabled` 但 GHIRBI 是 `disabled` | 闸门关闭 | 在游戏内模组菜单启用 GHIRBI（或按上面的格式写 `mods.txt`），重启游戏 |
| 日志有 `FAILED to start HTTP server on port 8710` | 端口被占用 | 改 `config.txt` 的 `port`，或释放该端口 |
| `/state` 的 `tabletop.exists` 为 `false` | 停在主菜单/加载画面 | 先进入一局游戏；**注意 `/state` 在这种状态下仍返回 200，判据是 `tabletop.exists` 而不是状态码** |

## 启动自检

因为游戏**无法从沙箱里驱动**（沙箱会把交互式 GPU 进程杀掉），模组内置了自检：启动约 6 秒后自动依次执行 读状态 → 放卡 → 开始 → 移卡，把每一步结果写进：

```
<LocalLow>\Weather Factory\Cultist Simulator\LLM_API\selftest.json
<LocalLow>\Weather Factory\Cultist Simulator\LLM_API\LLM_API.log
```

## 已知限制与风险

- **只在主界面/牌桌有效**：主菜单、加载画面、结局画面里牌桌尚未建立。**`/state` 此时仍返回 200**，但 `tabletop.exists` 为 `false`——判据是这个字段，不是状态码（早期文档写成"返回 503"，与实测不符，已更正）。
- **不是快照**：读状态期间游戏计时器仍在走，`timeRemaining` 会有毫秒级偏差。
- **`/action/*` 会真实改动存档**：这就是设计目的（让模型真的玩），但意味着调用即产生后果。游戏每 5 分钟自动存档（`AutosaveInterval=5`），出问题可从 `backups\` 回档。
- **`move` 的定位精度**：跨球体走的是游戏自己的 itinerary 动画，落点由游戏决定；`x`/`y` 只在游戏接受时才生效，返回体里的 `viaItinerary` 与 `position` 反映真实结果。
- **内测性质**：调用的部分方法平时只由 UI 触发，绕开 UI 直接调用在极端情况下可能让某个动词状态不一致。模组已把每个动作包在独立 try/catch 里，单个请求失败不会掀翻游戏。
- 端口仅绑定 `127.0.0.1`；同一台机器上的任何进程都能调它，**不要在不受信任的多用户机器上开着**。

## 实测游玩反馈（2026-10-16，Deepseek-V4.1-Flash）

一次约 10 分钟的「富家子弟」`brightyoungthing` 对局（纯 `curl` 驱动，无截图无鼠标，跑通
闲职刷钱 → 拍卖行竞拍 → 俱乐部取安逸 → 击退绝望/幻象 的链路）暴露了五个前文没写到的行为。
面向读者/调用方的完整版见 `MOD_README.md` 第八节，这里给开发视角的摘要与建议：

1. **`place` 的 `accepted:false` 不等于配方不可行。** 蜕衣俱乐部的 `entry` 槽（`required: sulochana|funds`）
   用 `/action/place` 投币**永远**返回 false（`sphere.TryAcceptToken returned false`），但紧接着
   `/action/start` 会成功（`explorecabaret → cabaretpleasure`）——游戏是在**启动配方时**自己扣入场费的。
   → 调用方应以 `start.changed` 为准。建议在 `place` 的响应里区分「被槽的性相规则拒绝」与
   「该配方本来就不走 place」，否则这个 false 会持续误导 agent。
2. **`/action/collect` 会取消进行中的动词。** 它内部是 `SituationCollectAll` + `Conclude`，
   **没有 `IsConcludable` 闸门**。实测第 2 次拍卖在 `auctionbuy` 途中被一次「顺手收一下」打断，
   拍卖作废。→ 建议对 `Ongoing` 的动词跳过、拒绝，或在响应里标出 `cancelledOngoing`。
3. **同一张牌第二次放回同一个槽，可能再也拿不到配方。** 闲职卡 `!legacybytjobmatured_5`
   三次 `place` 全部 `accepted: true`，但第三次 `start` 返回 `changed:false` /
   `recipeBefore: "nullrecipe"`（前两次分别是 `workbytjobstart`、`workbytjob`）。
   → 成因未定（动词冷却？需先降级回 `legacybytjob` 再成熟？）。`start` 的 `reason` 已如实说明，
   调用方不要原地重试同一个按钮。
4. **季节事件成对出现，解药只要「在桌面上」就自动生效。** `seasondespair → despair`
   （`shadows` 要 `inducesdespair`）、`seasonvisions → visions`（`shadows` 要 `fascination`）；
   桌上有安逸/追忆时事件自行判定 `despairdefeated` / `visionsdefeated`，**不需要手动塞进槽**。
   → `shadows` 槽的 `required` 是**恶化方向**的性相，不是解药。这一点值得在文档里点明，
   否则调用方（包括我）会本能地往槽里塞解药。
5. **疾病不处理会逐级降级，是目前唯一「什么都不做会永久变差」的路径。**
   `illhealth`（`sickness`，`warmup=60`）只有一个 `affliction` 槽，且**创建时就把健康抓进去了**，
   没有第二个治疗槽。放任的结果：健康 → 病痛（`affliction/illhealth/winter 2`）→
   衰老（`decrepitude` 顶掉 `health`）。因为 `explore` 槽的 `required` 含 `health`，
   降级会**连带削弱其他动词**。→ 建议单列进「已知限制与风险」。
6. **漫宿里只能带走一张牌，其余会被销毁。** `Otherworld.Hide()` 收地图时会遍历所有球体，
   对**除出口球体外的所有球体**调用 `RetireAllTokens()`。实测林地三张牌只带走一张，另外两张被销毁。
   → 进入漫宿前先把不需要的牌拖回桌面；在漫宿界面里**只留一张**想带走的牌，再放进圆形入口球体。
