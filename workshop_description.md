大模型直连 API / LLM Plays via API
让大语言模型或其他程序直接读写游戏状态、操作卡牌，不需要截图识别或模拟点击。每个操作都调用游戏自己的方法，行为与手动游玩一致。
Let a large language model or any other program read and change the game state and manipulate cards directly, with no screenshots and no simulated clicks. Every action calls the game's own method, so it behaves exactly as manual play does.

【能做什么 / What It Does】

读局面 / Read the Board
球体、卡牌、坐标、性相、动词状态、计时与卡槽占用。
Spheres, cards, positions, aspects, verb states, timers and slot occupancy.

放卡与开始 / Place and Begin
把卡牌放进卡槽（等同拖拽落卡），点击开始启动动词，可指定配方。
Put a card into a slot (the same path a drag takes) and click begin to start a verb, optionally naming the recipe.

移动卡牌 / Move Cards
跨球体搬运，或只在桌面内指定坐标。
Move a card between spheres, or reposition it on the tabletop.

收卡 / Collect
等同「收下」按钮，排版由游戏自己负责。
The same thing the collect button does, so the game itself lays the cards out.

关窗口 / Close Windows
关掉一个或全部动词窗口，避免一直挡住牌桌。
Close one or every verb window, so they stop covering the board.

暂停与速度 / Pause and Speed
读取当前是否暂停、解除暂停，或设置普通 / 快进等速度。
Read whether the game is paused, release the pause, or set normal / fast speed.

开局与菜单 / Games and Menus
从主菜单开新局或续档（可指定传承），也能离开牌桌回到主菜单。
Start or resume a game from the main menu, optionally choosing a legacy, and leave the tabletop to return to it.

外域 / Otherworlds
进出漫宿等外域，并按游戏原本的方式退出。
Enter and leave the mansus and other otherworlds, and exit them the way the game intends.

【1.0.2 更新 / What's New in 1.0.2】

新增 /action/tidy：把同种牌的多坨合并成一坨，等同游戏里的「整理」按钮。
Added /action/tidy: merge same-card stacks on the tabletop, the same thing the game's tidy button does.

/state 的输出大幅精简了，为的是节省大模型的 token：同一份局面从 188 KB 降到 109 KB，加 ?brief=1 可再降到 94 KB；需要完整球体列表时用 ?full=1。
The /state payload is now much smaller, specifically to save LLM tokens: the same board drops from 188 KB to 109 KB, and to 94 KB with ?brief=1; use ?full=1 when you need the complete sphere list.

修正 isEmpty 字段：它以前对每个球体都报 false，现在如实反映该球体是否装着牌。
Fixed the isEmpty field: it used to report false for every sphere; it now says whether that sphere actually holds cards.

【1.0.1 更新 / What's New in 1.0.1】

启动动词时可以指定配方，不再被「提示类」配方抢占而导致疾病无法治疗。
Starting a verb can name the exact recipe, so a hint-only recipe can no longer block the real one - this is what made illness impossible to cure.

漫宿等外域的球体现已可正常寻址，可以按游戏原本的方式退出漫宿。
The mansus and other otherworld spheres are now addressable, so the mansus can be left exactly the way the game intends.

新增 /action/retire 与 /action/closeworld 两个清理接口作为兜底。
Added /action/retire and /action/closeworld as cleanup fallbacks.

【接口 / The API】
本地地址 http://127.0.0.1:8710/，仅绑定回环地址，不对局域网或外网开放。
Local address http://127.0.0.1:8710/, bound to the loopback interface only.

GET  /state  完整局面，含 playback（是否暂停、当前速度）/ full board, incl. playback (paused, speed)
     ?full=1  连非空球体一起给 / include non-empty spheres too
     ?brief=1  省略卡牌说明文字，省约 15% 体积 / drop card descriptions, ~15% smaller
GET  /screen  当前场景与是否在游戏中 / current scene and whether a game is loaded
GET  /legacies  可开局的传承列表 / legacies a new game could start with
GET  /help  自述路由表 / self-describing route table
GET  /ping  存活检查 / liveness check

POST /action/place  把卡牌放进卡槽 / put a card into a slot
POST /action/start  点击开始，可带 recipe 指定配方 / click begin, optionally naming the recipe
POST /action/move  移动卡牌 / move a card
POST /action/click  卡牌主交互 / primary card interaction
POST /action/collect  收取产出（「收下」按钮）/ collect output (the collect button)
POST /action/conclude  关掉一个动词窗口 / close one verb window
POST /action/closewindows  关掉全部动词窗口 / close every verb window
POST /action/retire  移除一张 API 已无法操作的牌 / retire a card the API can no longer act on
POST /action/closeworld  收起漫宿等外域覆盖层 / dismiss an otherworld overlay
POST /action/tidy  整理牌桌，同种牌合并成一坨 / merge same-card stacks on the tabletop
POST /action/newgame  开新局 / start a new game
POST /action/continue  继续存档 / resume the existing save
POST /action/menu  回到主菜单 / return to the main menu
POST /action/unpause  解除暂停 / release a pause
POST /action/speed  设置游戏速度 / set the game speed

【使用前提 / Requirements】
必须先安装并启用 GHIRBI, the Gatekeeper（创意工坊 id 2901287611）。
You must install and enable GHIRBI, the Gatekeeper (Workshop id 2901287611).

游戏对 DLL 模组有硬闸门：没有它，本模组一行代码都不会执行，且不会有任何报错。也请从 Steam 启动游戏，否则创意工坊模组不会被识别。
The game hard-gates DLL mods: without it none of this mod's code runs, and it fails silently. Launch the game from Steam as well, or Workshop mods are not picked up.

【重要提醒 / Warning】
本模组会让外部程序完全操控你的游戏，动作会真实改动存档。
This mod lets an external program fully control your game, and its actions really do change your save.

接口只监听本机回环地址，同一台机器上任何进程都能调用——请不要在不受信任的环境中启用。
The API listens on the loopback interface only, so any process on the same machine can call it - do not enable it on a machine you do not trust.
