# LLM_API — 外部程序（大模型 / AI agent）操控《密教模拟器》

这个模组在游戏进程内开一个本地 HTTP 接口，让**别的程序**能够读取当前局面并操作卡牌。
它是为大模型/自动化脚本设计的：`curl`、Python、任何语言都能驱动。

- 游戏版本：Cultist Simulator `2026.1.g.2`（Steam AppID 718670，Unity / Mono）
- 接口地址：`http://127.0.0.1:8710/`（**只绑定回环地址，不对局域网/外网开放**）
- 模组 ID：`LLM_API`

---

## 一、这个模组提供什么

四个核心能力，全部通过 HTTP + JSON 调用：

| 能力 | 接口 |
|---|---|
| **读取游戏目前状态** | `GET /state` |
| **把卡牌放到卡牌框** | `POST /action/place` |
| **点击开始**（启动动词） | `POST /action/start` |
| **移动卡牌位置** | `POST /action/move` |

还能**免点击地开始一局新游戏或继续存档**，不必再去点主菜单：

| 能力 | 接口 |
|---|---|
| 列出可开局的传承 | `GET /legacies` |
| 开新局（可指定传承） | `POST /action/newgame` |
| 继续存档 | `POST /action/continue` |

另外附送：

| 接口 | 作用 |
|---|---|
| `GET /state` | 读取完整局面（默认省略"非空球体"条目，见下）|
| `GET /ping` | 存活检查 |
| `GET /help` | 服务器自述路由表 |
| `GET /legacies` | 可开局的传承列表 |
| `POST /action/newgame` | **从主菜单直接开新局**（可指定传承） |
| `POST /action/continue` | **从主菜单直接继续存档** |
| `POST /action/menu` | **离开牌桌回主菜单**（之后可开新局） |
| `POST /action/unpause` | **解除暂停、恢复普通速度** |
| `POST /action/speed` | 设速度：`{"speed":"Normal"}` / `"Fast"` / `"Paused"` |
| `POST /action/conclude` | **关掉动词窗口**（右上角的 X）。**仅当动词已跑完（`concludable`）才有效**，且入队后约 20 秒才从 `openWindows` 消失，请轮询确认 |
| `POST /action/collect` | **一键收取**输出框的牌。**优先让游戏自己收**（等于按「收下」按钮，布局归游戏），动词未跑完时回退为按网格摆放 |
| `POST /action/closewindows` | **一次关掉所有动词窗口**，没有"必须已跑完"的限制。游戏会自己重开一个默认窗口，所以**建议每轮都调** |
| `POST /action/click` | 卡牌主交互（动词开/关窗口等） |
| `POST /action/retire` | **移除一张卡**（Token.Retire()）；用于清理 API 已无法操作的残留牌 |
| `POST /action/closeworld` | **收起漫宿等覆盖层**（`Otherworld.Close()`）。仅作兜底——正常退出漫宿应把漫宿里的卡放进对应的圆形入口球体 |
| `POST /action/tidy` | **整理牌桌**：把同种牌的多坨合并成一坨（等同游戏里的「整理」按钮）。回报合并前后的坨数与仍未合并的牌 |
| `POST /selftest` | 只读自检；带 `{"replay":true}` 可顺便实测三个写操作 |

**退出漫宿（或其他覆盖层）的正确做法**：漫宿里会出现三张牌，把其中一张**放进对应的圆形入口球体**（林地是 `~!mansus/mansusthresholdwood`，纯白之门是 `~!mansus/mansusthresholdwhite`，以此类推）。这一步会触发游戏自己的退出流程，覆盖层随之收起。`/action/closeworld` 只是兜底。实测：往 `~!mansus/mansusthresholdwood` 放一张牌后，游戏自动创建 `wood_NNN` 退出情境并收起地图。
**整理牌桌用 `/action/tidy`**：把同种牌的多坨合并成一坨，等同游戏右下角的「整理」按钮。实测桌面 5 坨「健康」（合计 123 张）→ 1 坨 123 张。

**带独立倒计时的牌不会合并**（例如「活力」：`decays: true`，每坨各有自己的三分钟剩余时间）。这不是接口的缺陷——游戏本身也不合并它们，合并会丢失各自的剩余时间。响应里的 `stillSplit` 会列出合并后仍分裂的牌，据此区分"还没整理"和"本来就合不了"。

**不要用 `/action/collect` 来整理**：`collect` 会 `Conclude` 掉正在进行的动词（等于中途取消）。要整理就单独调 `tidy`。

**移动坐标（x/y）说明**：坐标是球体局部坐标、不是屏幕像素（桌面原点在中心，范围大致 x∈[-900,900]、y∈[-600,600]）；**只在桌面这类自己管理布局的球体上生效**，往子球体（卡槽、笔记球体）移动时会被忽略并落在 {0,0}。往卡槽放牌请用 `/action/place`。移动是动画过程，途中牌会在 `~/enroute`，可用 `/state` 的 `tokensInTransit` 判断是否落定。

`/state` 返回的内容包括：

- 全部**球体**（sphere）及其 FucinePath、类型、卡牌数
- 全部**卡牌**的 id、实体 id、中文名、数量、性相（aspects）、**坐标**
- 全部**动词**（Situation）的 `verbId`、状态（`Unstarted`/`Ongoing`）、当前配方、剩余时间
- 每个动词的**卡牌框**（卡槽）路径、要求性相、是否被占用
- `tokensInTransit`：正在移动中的卡牌（见下面的注意事项）

**它不会自己做决定。** 决策完全在调用方（大模型或脚本）手里，这个模组只负责"看"和"动手"。

---

## 二、装到别的电脑上怎么用

### 前置条件：必须先装 GHIRBI

游戏对 DLL 模组有一道硬闸门。**没有 GHIRBI, the Gatekeeper，这个模组的代码一行都不会被执行**，而且不会有任何报错——弹不出、日志里也没有，看起来就像模组没装。

1. 在 Steam 创意工坊订阅 [GHIRBI, the Gatekeeper（id 2901287611）](https://steamcommunity.com/sharedfiles/filedetails/?id=2901287611)
2. **先启动 Steam 客户端，再从 Steam 启动游戏**（游戏的模组目录依赖 Steamworks；Steam 没运行时连创意工坊模组都列不出来）
3. 在游戏内**模组菜单**里确认 `GHIRBI` 与你这个模组**都是启用状态**

> **如果你要用脚本启动游戏**（自动化测试、agent 自己拉起游戏），不要直接运行 `cultistsimulator.exe`——那样会黑屏，
> 因为受沙箱限制的子进程打不开 Steamworks 的命名管道。改用 Steam 的 URL 协议：
>
> ```powershell
> Start-Process 'steam://rungameid/718670'
> ```
>
> 这样进程由 Steam 托管，也能稳定存活。**游戏起来后不要强杀进程**，Steam 会当成异常退出。

### 安装步骤

把整个 `LLM_API` 文件夹放进游戏的模组目录：

```
Windows:  %USERPROFILE%\AppData\LocalLow\Weather Factory\Cultist Simulator\mods\
          （游戏内 Options → BROWSE FILES 可直接打开这个目录）
```

放好后的结构必须是：

```
mods\LLM_API\
    synopsis.json
    README.md              ← 就是本文
    config.txt             ← 首次启动后自动生成
    dll\
        LLM_API.dll
```

> ⚠️ **`dll` 子目录和文件名都不能改。** 游戏的加载器写死了去找
> `<模组目录>\dll\<synopsis.json 里的 name>.dll`，找不到就**静默放弃**，不报错。
> `synopsis.json` 里的 `name` 也必须保持 `LLM_API`。
>
> 本模组**刻意不声明 `dependencies`**：实测发现，声明一个游戏判定为"版本不兼容"的依赖
> 会让游戏载入 "UH O" 崩溃场景（`ModManager.CatalogueMods()` → `StageHand.LoadInfoScene()`），
> 而 GHIRBI 本来就是 DLL 闸门的硬前置，所以这个声明收益很小、风险很大。

### 启用

在游戏内**模组菜单**里勾选启用本模组，然后**重启游戏**（DLL 只在启动时加载一次，热重载不生效）。

### 确认它活着

启动游戏后，看这个文件是否出现：

```
%USERPROFILE%\AppData\LocalLow\Weather Factory\Cultist Simulator\LLM_API\LLM_API.log
```

- **文件存在** → 模组已成功加载，首行会是 `=== LLM_API 1.0.1 starting ===`
- **文件不存在** → 游戏的 DLL 闸门没开。回去检查 GHIRBI 是否已启用

这个判据比"模组菜单里显示 enabled"可靠得多：GHIRBI 没启用时，本模组照样显示 `enabled`，但游戏在加载 DLL 之前就返回了。

### 调用

```bash
curl http://127.0.0.1:8710/ping
curl http://127.0.0.1:8710/state
curl -X POST http://127.0.0.1:8710/action/place \
     -H "Content-Type: application/json" \
     -d "{\"token\":\"!introjob_2\",\"slot\":\"~/tabletop!work_0/work\"}"
```

从主菜单零点击开局或续档：

```bash
curl http://127.0.0.1:8710/legacies
curl -X POST http://127.0.0.1:8710/action/continue
curl -X POST http://127.0.0.1:8710/action/newgame \
     -H "Content-Type: application/json" -d "{\"legacy\":\"detective\"}"
```

这些都只是**发起**，加载是异步的——接着轮询 `GET /state` 直到返回 200，牌桌就绪。
`GET /screen` 可以告诉你当前是不是在主菜单（`menuSceneActive`）或已在牌桌（`tabletopHasCards`）。

必须**先进入一局游戏**才能用卡牌相关接口。

### 让 AI agent 自己玩：`play/` 里的参考驱动

模组随附一套 PowerShell 驱动脚本（在仓库的 `play/` 目录），演示一个外部 agent 该怎么玩：

| 文件 | 作用 |
|---|---|
| `lib.ps1` | 共享库：HTTP、读局面、**按游戏自己的性相规则找合法配对**、找输出牌 |
| `play-session.ps1` | 会话驱动：每轮 读局面 → 必要时解除暂停 → 关掉残留窗口 → 收取输出 → 落一张合法牌并启动 |
| `verify-fixes.ps1` / `verify-collect.ps1` / `verify-closewindows.ps1` | 真机验证脚本，用可量化判据检查摆放、收卡、关窗 |

```powershell
powershell -ExecutionPolicy Bypass -File play\play-session.ps1 -Minutes 10
powershell -ExecutionPolicy Bypass -File play\play-session.ps1 -Minutes 3 -DryRun   # 只看不动
```

**驱动一个 agent 时最容易踩的三个坑**（都是实测踩出来的）：

1. **游戏可能处于暂停，而 `/state` 照样正常返回**——计时器却完全不动。务必读 `playback.paused`，必要时调 `/action/unpause`。只看数字变化会误以为"游戏很慢"。
2. **`continue` 进旧档后游戏通常是暂停的**，`newgame` 开局则不暂停。
3. **动词窗口会盖住牌桌、而且游戏跑完一个作业会自己重开一个**，所以关窗要**每轮都做**（`/action/closewindows`），不能只关一次。

---

## 三、接口细节

所有容器地址都是游戏内部的 **FucinePath** 字符串，直接照抄 `/state` 里的 `path`/`spherePath` 字段即可：

```
~/tabletop                          桌面
~/tabletop!work_0                   「作业」这个动词
~/tabletop!work_0/work              该动词的「作业」卡牌框
~/tabletop!work_0/notessphere       该动词的提示栏
```

- 前缀 `~` 是根；`!work_0` 是动词实例 id；`work` 是卡槽类型 id

### `GET /state`

```json
{
  "gameVersion": "2026.1.g.2",
  "rootSpherePath": "~",
  "sphereCount": 35,
  "tokenCount": 13,
  "spheres": [
    { "path": "~/tabletop", "type": "TabletopSphere", "specId": "tabletop", "tokenCount": 7 },
    { "path": "~/tabletop!work_0/work", "type": "ThresholdSphere", "specId": "work",
      "label": "作业", "required": { "ability": 1, "job": 1 }, "tokenIds": [] }
  ],
  "tokens": [
    { "id": "!funds_12", "payloadType": "ElementStack", "entityId": "funds",
      "label": "资金", "quantity": 11, "spherePath": "~/tabletop",
      "position": { "x": -375.0, "y": 0.0, "z": 0.0 },
      "aspects": { "funds": 11, "ingredient": 11 }, "draggable": true },
    { "id": "!work_0", "payloadType": "Situation", "verbId": "work",
      "situationState": "Unstarted", "currentRecipeId": "nullrecipe",
      "timeRemaining": 0.0,
      "slots": [ { "path": "~/tabletop!work_0/work", "specId": "work",
                   "required": {...}, "empty": true, "tokenIds": [] } ] }
  ],
  "tokensInTransit": []
}
```

### `GET /state` 的体积与 `?full=1`

实测一份 **115 张牌**的局面（同一份数据，只改输出方式）：

| 版本 | 字节 | 说明 |
|---|---|---|
| 早期（带缩进 + 全部球体） | 188,732 | |
| **现在默认** `GET /state` | **109,227** | 紧凑 JSON + 省略非空球体 |
| `GET /state?full=1` | 117,126 | 紧凑 JSON + 全部球体 |
| `GET /state?brief=1` | **93,500** | 再省略卡牌 `description`（省 50.5%）|

**省下的 42.1% 里，最大的一项是"去掉缩进"，不是球体过滤。** 拆开看：

| 改动 | 省下 |
|---|---|
| 紧凑 JSON（不缩进） | ~69 KB（36.8%） |
| 省略非空球体条目 | 7.9 KB（4.2%） |
| `state` 只留类名 + 空 `illuminations` 不输出 | 约 3 KB |

**默认省略的只是"非空球体"条目**（已经装了牌的球体）。省掉的是**重复信息**：每张牌在 `tokens[]` 里都带 `spherePath`，所以桌上有什么牌、有哪些动词框、每个框里有什么，**不带球体数据一样看得全**。
保留的是**空球体**——它们携带 `required` / `forbidden` 性相，是决定"该往哪个槽放什么牌"所必需的。

```
GET /state           默认：tokens 全量 + 空球体（35 个）+ tabletop + playback
GET /state?full=1    完整：连非空球体一起给（那 16 个）
GET /state?brief=1   精简：不输出卡牌的 description（牌名、性相、坐标、卡槽全都保留）
```

`brief` 只去掉**卡牌说明文字**（ flavorful text，占约 10 KB）。**牌名、性相、坐标、卡槽、动词状态、计时一概保留**，所以 `brief` 模式仍然可以正常放卡、开始、收卡——它只影响"你能不能读懂这张牌的背景故事"，不影响任何操作判断。

默认响应里会多两个字段说明这件事：`spheresOmitted`（省略了几个）和 `spheresNote`。
`sphereCount` 仍然是**真实总数**（51），不会因为过滤而变小。

> 注意：`full=1` 现在只比默认多 7.9 KB——球体条目在紧凑格式下本来就很小。**"要不要 `full=1`"对体积影响不大，真正省体积的是紧凑输出本身。**

### 动作接口

| 接口 | 请求体 | 说明 |
|---|---|---|
| `POST /action/place` | `{"token":"!id","slot":"~/path"}` | 点就是把卡牌拖进卡槽 |
| `POST /action/start` | `{"token":"!动词id"}` | 等于按下动词的开始按钮 |
| `POST /action/move` | `{"token":"!id","to":"~/path"}` | 跨球体移动 |
| `POST /action/move` | `{"token":"!id","x":100,"y":-50}` | 只改坐标（留在原球体） |
| `POST /action/click` | `{"token":"!id"}` | 主交互 |

响应统一是 JSON。成功时 `"success": true`；失败时 `"success": false` 并带 `"error"`，同时 HTTP 状态码有意义：

| 状态码 | 含义 |
|---|---|
| `400` | 参数缺失或 JSON 格式错 |
| `404` | 找不到该 token 或该球体路径 |
| `405` | 方法或路径不对 |
| `409` | 状态不允许（例如对一个非动词调用 `start`） |
| `503` | 游戏服务还没就绪（例如场景组件尚未创建） |
| `504` | 请求等待游戏主线程超时 |

---

## 四、使用上必须知道的几件事

**1. 收卡请用 `collect`，不要用 `move` 自己搬。**
把动词产出框里的牌搬到桌面，**用 `POST /action/collect`**，不要用 `POST /action/move` 一张张搬。

`collect` 内部优先走游戏自己的「收下」按钮链路（`Situation.Conclude` → `EvictOutputCommand` →
`SituationCollectAll`），**排版由游戏负责**：卡会落在你拖动过的那个小 C 形投放框旁边、按格子排开、
同名牌自动叠起来。用 `move` 自己搬则完全跳过这套排版——你给什么坐标它就放哪里，给同一个坐标就重叠。

`move` 适合的场景是「我要把这张牌精确摆在某个位置」或「跨球体搬运」，**不适合当作「收卡」的手段**。

**2. 从漫宿（或其他外域）出来，要把卡放进圆形入口球体，不要用 `move`。**
漫宿界面里有三张牌（**每次进入内容随机，不要写死牌 id**）。正常退出方式是：
**把其中一张牌放进对应的圆形入口球体**——林地 `~!mansus/mansusthresholdwood`，纯白之门
`~!mansus/mansusthresholdwhite`，鹿之门 `…thresholdstag`，蛛之门 `…thresholdspider`，
孔雀之门 `…thresholdpeacock`，三尖之门 `…thresholdtricuspid`。

```jsonc
// 正确：放进入口球体，游戏自己跑完退出流程
POST /action/place {"token":"<漫宿里任意一张牌>","slot":"~!mansus/mansusthresholdwood"}
→ {"success":true, "to":"~/enroute!wood_422/egress"}
   // 游戏随即创建 wood_NNN 退出情境，覆盖层收起（metapaused 变回 false）
```

**不要**用 `move` 把牌扔回 `~/tabletop`、或扔进 `~!mansus/wood0` 这类产出球体——那会跳过
「入口球体 → 配方 → EvictToken」整条链路，代表"正在进入漫宿"的 `Ingress` 永远收不到完成信号，
地图就会一直挡在画面上。

**1.0.1 已修**：`~!mansus/*` 这些球体以前在任何接口里都解析成 `~/tabletop`（表现为
`404 no sphere at path ... (the catalogue resolved it to '~/tabletop')`），现在可以正常寻址，
所以上面的正确做法才走得通。

万一还是卡住了，两个兜底手段（正常流程不需要）：`POST /action/closeworld` 直接收起覆盖层
（会回报 `IsOpen` 是否真的变了）；`POST /action/retire {"token":"…"}` 移除一张 API 已无法操作的
残留牌。另外 `place` / `collect` 结束时会**自动清扫**停在桌面上的孤儿 `Ingress`，回报在
`retiredOrphanIngress` 字段里。

**3. 放卡会不会被接受，取决于性相。**
把「资金」放进要求 `ability`（能力）的作业槽，游戏会**正确拒绝**，返回 `"accepted": false`。
这不是接口故障——`/state` 里每个卡槽都带 `required` 与 `forbidden`，调用方应当据此配对：

> 卡牌必须**具备 required 里任意一项**，且**不具备 forbidden 里任何一项**。

**4. 移动中的卡牌会经过 `~/enroute`。**
卡牌移动时要播动画，中途会停在中转球体 `~/enroute`。此时读 `/state` 会看到卡牌"在 `~/enroute`"。
`/state` 因此单列了 `tokensInTransit` 数组——**看到 id 出现在这里就说明它还在飞，别当作落点**，等它离开再判断。

**5. 动词的状态与计时。**
`situationState` 可能是 `Unstarted` / `Ongoing` / 结束态。`timeRemaining` 是剩余秒数。
游戏时间是持续推进的：两次 `/state` 之间计时会减少，这不是 bug。

**6. 动作会真实改动存档。**
这就是设计目的。游戏每 5 分钟自动存档，出问题可从
`%USERPROFILE%\AppData\LocalLow\Weather Factory\Cultist Simulator\backups\` 回档。

**7. 只在牌桌上有效。**
主菜单、加载画面、结局画面里牌桌尚未建立。这种时候 `/state` **仍然返回 200**，但
`tabletop.exists` 是 `false`、`tabletop.tokenCount` 为 0——**请用这个字段判断，不要靠状态码**。
卡牌相关接口在这种状态下会失败。`GET /screen` 可以进一步区分当前在哪个场景。

**8. 安全边界。**
服务只监听 `127.0.0.1`，同一台机器上的任何进程都能调它——**不要在不受信任的多用户机器上开着**。
想关掉就把 `config.txt` 里的 `enabled` 设为 `0`，或者干脆在游戏内停用本模组。

---

## 五、配置

首次启动会在模组目录生成 `config.txt`：

```ini
port=8710                     # 监听端口
enabled=1                     # 设 0 则加载模组但不启服务
directory=...\LLM_API      # 日志与自检报告目录
selftest=1                    # 启动后跑一次只读自检
# selftest_slot_path=~/tabletop!work_0/work
# selftest_token_id=!introjob_2
request_timeout_ms=15000      # 请求等待游戏主线程的上限
```

改完需要**重启游戏**。日志在 `directory` 指向的目录下的 `LLM_API.log`。

---

## 六、常见问题

| 现象 | 原因与对策 |
|---|---|
| 没有 `LLM_API.log`，接口也连不上 | **GHIRBI 没启用**（或 Steam 没运行）。在游戏内模组菜单启用 GHIRBI，重启游戏 |
| 模组菜单里找不到创意工坊模组；日志有 `... but not in the catalogue` | **Steam 客户端没运行**。启动 Steam 后再从 Steam 启动游戏 |
| `curl` 报连接被拒绝 | 游戏没运行，或没进入一局游戏，或端口被占用（改 `config.txt` 的 `port`） |
| 停在主菜单/加载画面 | `/state` 仍返回 200，但 `tabletop.exists` 为 `false`。用这个字段判断，先进一局 |
| `place` 返回 `accepted: false` | 该卡牌性相不满足卡槽的 `required`，或踩了 `forbidden`。换一张牌 |
| `/state` 里卡牌在 `~/enroute` | 它在移动动画中。等它落到 `tokensInTransit` 之外的球体 |
| 计时器完全不动、但 `/state` 正常 | 游戏处于暂停。读 `playback.paused`，或调 `/action/unpause` |
| 动词窗口一直挡着牌桌 | 调 `/action/closewindows`，**每轮都要调**——游戏会自己重开一个 |
| 改了 DLL 没生效 | DLL 只在游戏启动时加载一次，也只在启动时能被替换（运行中文件被锁）。重启游戏 |
| 创意工坊上传后多出 `serapeum_catalogue_number` | **Steam 自己生成的目录编号，不要删改**。它不属于模组内容，改动会让上传对不上号 |

---

## 七、技术备注（给想改代码的人）

- 入口是 DLL 里**全局命名空间**下的 `public class LLM_API`，含 `public static void Initialise()`。
  这三处（目录名、DLL 文件名、类名）必须一致，且类**不能放进任何命名空间**——
  游戏用 `Assembly.GetType("LLM_API")` 查找，而它只匹配类型全名、不搜索命名空间。
- HTTP 服务跑在后台线程；所有触碰游戏对象的操作都会被排进队列，由 `Update()` 在
  **Unity 主线程**执行（这是 Unity 的硬性要求）。
- 动作调用的是游戏自己的方法：`Sphere.TryAcceptToken()`、`Situation.TryStart()`、
  `Sphere.GetItineraryFor()` → `TokenItinerary.Depart()`，所以天使、支配权、配方触发、
  编年史记录等行为与玩家手动操作完全一致。

---

## 八、Deepseek-V4.1-Flash 游玩反馈

> 本节由 **Deepseek-V4.1-Flash** 于 2026-10-16 实测补充。当天它以纯 `curl` 驱动（无截图、无鼠标）
> 打完一局「富家子弟」`brightyoungthing`，约 10 分钟游戏内时间，跑通了
> 闲职刷钱 → 拍卖行竞拍得书 → 俱乐部取安逸 → 击退绝望/幻象事件 的完整链路。
> 下面五条是前七节没有覆盖到的实测行为，供后续写 agent 的人参考。

### 8.1 ⚠️ `place` 返回 `accepted:false`，不代表这个配方做不出来

蜕衣俱乐部的 `entry` 槽 `required=['sulochana','funds']`。把 `!funds_x` 用 `/action/place` 投进去
**永远返回** `"accepted": false`，附 `"reason": "sphere.TryAcceptToken returned false"`，卡牌退回桌面。
但紧接着 `/action/start` 会**成功**：

```json
{"success": true, "changed": true,
 "recipeBefore": "explorecabaret", "recipeAfter": "cabaretpleasure",
 "timeRemaining": 15.0}
```

也就是说，游戏是在**启动配方时**自己把入场费扣掉的，`TryAcceptToken` 从来就没打算收这张牌。

**对策**：判断「这张牌能不能用」，要看 `/action/start` 返回的 `changed` 字段，
不要被 `/action/place` 的 `accepted:false` 劝退——那可能是游戏的正常路径，不是拒绝。

### 8.2 ⚠️ 不要对「进行中」的动词调用 `/action/collect`

`/action/collect` 内部走的是 `SituationCollectAll` + `Conclude`，**没有 `IsConcludable` 那道闸门**。
对 `Ongoing` 的动词调它，会把还在跑的动词一并关掉，等于中途取消。

实测：第 2 次探索拍卖行，`auctionbuy` 跑到一半被自己的 `collect` 打断，拍卖作废、地点卡被弹回桌面。

**对策**：只在动词 `situationState == "Complete"`（或 `concludable == true`）时收产出。
想「顺手收一下」就无脑调 `/action/collect`，会误伤正在计时的动词。

### 8.3 ⚠️ 同一张牌第二次放回同一个槽，可能再也拿不到配方

富家子弟的世袭闲职卡 `!legacybytjobmatured_5` 放进 `~/tabletop!work_0/work`：

| 第几次 | `place` | `start` 的 `recipeBefore` | 结果 |
|---|---|---|---|
| 1 | `accepted: true` | `workbytjobstart` | ✅ 10 秒，资金 +3 |
| 2 | `accepted: true` | `workbytjob` | ✅ 30 秒，资金 +3 |
| 3 | `accepted: true` | `nullrecipe` | ❌ `changed: false`，卡牌被弹回桌面 |

`place` 每次都报成功，但第三次 `TryStart` 找不到配方。
不确定是游戏对同一动词设了冷却，还是要求卡牌先降级回 `legacybytjob` 再重新成熟。

**对策**：`start` 返回 `changed: false` 且 `recipeAfter: "nullrecipe"` 时，不要原地重试——
换一张牌，或换一个动词。反复点同一个按钮不会有任何变化。

### 8.4 ⚠️ 季节事件是成对的，解药只要「在桌面上」就会自动生效

「时间」动词的 `situationstoragesphere` 里会依次冒出季节标记，每个季节派生一个事件动词：

| 季节标记 | 事件动词 | `shadows` 槽要求 | 解药 |
|---|---|---|---|
| `seasondespair` | `despair`（`despairrisk`） | `inducesdespair` | 安逸 `contentment` |
| `seasonvisions` | `visions`（`visionsrisk`） | `fascination` | 追忆 `fleeting` |
| `seasonsickness` | `illhealth`（`sickness`） | —— | 见 8.5 |

**关键**：解药**不需要手动塞进槽里**。只要它躺在桌面上，事件到点会自己判定成功：

```
despair_68   → recipe: despairdefeated   （桌上有安逸）
visions_96   → recipe: visionsdefeated   （桌上有追忆）
```

而 `shadows` 槽的 `required` 写的是 `inducesdespair` / `fascination`——那是**「恶化」方向的性相**，
不是解药。往这个槽里塞解药是错的，也塞不进去。

**对策**：季节事件不要试图「手动喂牌」，保持桌面上有对应的解药即可；
解药会被消耗掉，所以下一轮事件前要再取一张。

### 8.5 ⚠️ 疾病不处理会一路恶化：健康 → 病痛 → 衰老

`illhealth` 动词（配方 `sickness`，`warmup=60`）只有一个 `affliction` 槽，
**创建时就已经把「健康」抓进去了，没有第二个治疗槽**：

```
~/tabletop!illhealth_91/affliction   ThresholdSphere
    required = {fatigue, follower_wound, health}
    tokenIds = ["!health_3"]          ← 健康已被占据，无空位
```

没有可用的治疗途径时，它只能等自己结算，后果是**逐级降级**：

| 阶段 | 卡牌 | 性相 |
|---|---|---|
| 正常 | 健康 | `health, ability, heart 2, ingredient` |
| 生病后 | 病痛 | `affliction, illhealth, ingredient, winter 2` |
| 再恶化 | 衰老 | `decrepitude, ability, remnant, winter` |

注意「衰老」里 `decrepitude` 顶掉了 `health`，而 `health` 是「探索」槽认的性相之一
（`explore` 槽 `required` 含 `health`），所以降级会**连带削弱其他动词的可用性**。

**对策**：疾病窗口期内就要处理（`affliction` 槽接受 `fatigue` / `follower_wound` / `health`，
可以考虑用别的可牺牲卡牌顶替，或提前备好治疗手段）。放着不管的代价是永久性的能力损失。

### 8.6 ⚠️ 漫宿里只能带走一张牌，其余会被销毁

漫宿（Mansus）是「一次性抉择」场景。把牌放进入口后，游戏会：

1. 创建对应出口的 `egress` 球体；
2. 触发 `Otherworld.Hide()` 收地图；
3. `Hide()` 内部遍历漫宿所有球体，对**除出口球体以外的所有球体**调用 `RetireAllTokens()`——也就是全部销毁。

这意味着你带进漫宿的多张牌里，**只有最后放进出口的那一张能活着回到桌面**，其余一律消失。

实测：林地（wood）里原有「传闻」「脑中的嗡鸣」「4级秘史碎片」三张牌，只把「传闻」放进 `mansusthresholdwood`，结算后只剩「传闻」被收入 `wood_NNN/egress`，另外两张被销毁。
---

**本节由 AI 实测产生，不是设计文档。** 如果你在别的游戏版本上得到不同结果，
以 `/state` 的实际返回为准——这个模组的判据永远是字段，不是文档。
