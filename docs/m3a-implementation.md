# M3-A 基础卖出、顾客交互与默认整理

## 收尾：交易面板自主开启

本次在 `bd813e03325368fb011c5c60e214425b84602604` 上继续，保留原分支。用户要求先判断必要性：上一轮 `m3a-flow-2b` revision41中，MCP能读库存，但客户 phase=unknown、正常报价为空，原因 `quote_surface_unavailable`，且没有合法遣走；普通买入准备仍合法。UI点击后 revision42变为trading并开放遣走。故这是部分普通场景的自主操作缺口，不是单纯画面未显示，也不意味着所有原有交易都失效。

重新读取当前同哈希 DLL，正常入口为 `StoreClientMono.OnClick → NegociationUIManager.ToggleUI → OpenUI`。开分支执行原生 HideConfirmPanel、SetActive、焦点更新、RefreshNow及声音；后续按当前客户 ResolveMode / Rebuild 生成交易内容。新增 `open_trade_panel(customer_ref)` 调用该 open-only 分支，避免 toggle 误关面板；执行前确认当前顾客引用、普通 SELL/BUY 白名单、到达且未移动、必要对话结束、原生角色交互可用、面板确实关闭及相关阻挡。

观察新增兼容可选 `customer.trade_panel`：closed / buy / sell / unavailable；buy/sell是已构建面板的 NPC 方向，不以客户意向冒充面板模式。未知旧记录可缺失或为null，schema1保持原契约。只读反射 mode字段，不从观察调用刷新或隐藏价格 getter。

动作仍经过现有 ActionDriver、世界/版本校验、单写、去重、原ID查询及后置核验。成功必须看到同一顾客、正确已开模式、trading、正常总价可读、现金不变及版本前进；面板已开时动作不合法。空交易集合仍可读取画面正常总价0，但不会生成可执行空集合报价。没有搬运、购买或卖出来制造这项测试。

自动化为 **377 managed + 149 Python = 526通过、0失败**，保留普通买入、卖出、拒绝、整理及既有恢复回归。初次完整Python回归发现测试模拟器显式序列化可选字段null的兼容问题，修复后149项完整重跑通过。新增测试覆盖SELL/BUY、已开不重复、错误顾客、到达/对话/确认阻挡、错误模式、缺失报价、现金异常和旧快照兼容。记录位于 `artifacts/m3a-panel/`；本小范围收尾由主Agent复核，未新增子Agent。

本轮实机授权已单独取得：最多2槽，每槽4位顾客/16次写入、最多2位普通顾客的面板开启；允许初始提示/设置与清理，禁止CU点击顾客开面板，禁止商品搬运、买卖、整理、日结和跨日。

### 收尾实机结果：PASS（普通 SELL 面板自主开启）

使用真实 Codex CLI `gpt-5.6-luna` 与 PawnPilot MCP，五个工具均有实际调用。仅创建 save_13，开门、接待 mentor 和食品卖家，必要对话自动播放期间使用 `wait_for_update`；Computer Use 仅完成启动、初始设置、观察和清理，没有点击顾客或打开交易面板。共4次 MCP 写入，各自按原ID查询到 succeeded / postcondition_observed：

| command_id | 动作 | 结果 |
|---|---|---|
| panel-1-start | start_business | 自主开门 |
| panel-1-receive | receive_next_customer | 接待导师并等待普通对话 |
| panel-1-receive-2 | receive_next_customer | 接待普通食品卖家 |
| panel-1b-open-1 | open_trade_panel | 当前 customer-51 面板自主开启 |

第一次 CLI 达到有界等待次数后停止，第二次只读确认对话结束后续跑，未重放前三次命令。开面板前 revision39：customer-51、arrived=true、trade_type=sell、trade_panel=closed、phase=unknown、displayed_total=null；开后 revision40：仍为 customer-51、trade_panel=sell、phase=trading、quote.mode=normal、displayed_total=0，cash550→550。后续 legal_actions 保留 prepare_purchase_items 并新增 dismiss_customer，open_trade_panel 消失且候选原因明确为 trade_panel_already_open。画面观察与这些状态一致。

本轮禁止搬运商品，因此0是空后柜的正常显示总价，quote.status仍为unverified、offer_ref=null；不宣称验证了正金额的商品绑定报价或成交。主库存8件及前后柜集合未因开面板改变，没有买入、卖出、整理或跨日。普通 BUY 的开面板支持和不误开顾客/不重复等边界由自动化覆盖，本轮未取得 BUY 实机样本；原始买入/卖出/拒绝/整理功能的回归通过，但未在本轮重新实机成交。

原始证据：`artifacts/m3a-panel/codex/panel-1.jsonl`、`panel-1b.jsonl`；精确 before/after 为 `live/panel-result.json`，写动作核对为 `live/mcp-write-audit.json`。Release 构建0错误，仅有离线 NuGet 漏洞源 NU1900 警告；自动化日志为 `managed.log` 和 `python-recheck.log`，未删除早期失败记录。协议、README、Agent Skill 与专项 `verify-codex.py --trade-panel` 已同步。

### 收尾清理与提交

通过游戏 UI 删除唯一测试槽save_13，列表只剩原第7天394点进度；停用Mod后正常退出，安装脚本按收据校验并移除三文件及空目录，停止broker，确认18765不再监听。20份原始根目录文件备份、测试槽副本、journal和原始证据保留于本机，不提交敏感配置、存档或游戏DLL。`live/cleanup-audit.json` 记录原save_0及day5/day6两份历史备份全部SHA-256不变，游戏程序集仍为上述同一哈希；原进度没有加载或删除。

允许发生变化的配置/索引/日志中，实际变化为SaveFile.es3、saves_index.es3、Player.log、Player-prev.log和一份旧GameLog；原日志轮转也可能移除旧日志，备份保留，不回滚整个目录。测试槽和安装目录均已不存在，游戏与broker已退出。

沿用 `feature/m3a-basic-sell`，收尾提交标题为 `feat:补齐普通顾客交易面板自主开启`；提交包含本节、README、Skill、实现和回归测试，推送至同名远端分支，不合并main、不force push。提交SHA在交付消息及Git记录中给出。

## 原始 M3-A 验收记录

2026-11-01：**PASS（下述已验证的基础范围）**。真实 Codex CLI 经 PawnPilot MCP 完成一笔普通卖出，现金550→579；正常拒绝、成交后遣走及游戏原生固定整理通过。手动推进/选项未出现必要样本，明确未覆盖；不将自动播放算作这两项成功。原有普通买入自动化回归保持通过。

## 基线与架构

从干净的 `feature/m2cd-basic-trade` / `51c03f218c6332a4ceddc9c232bc5c9e01b88217` 开始；核对远端同一 HEAD 后创建 `feature/m3a-basic-sell`，保留双层目录，没有 reset 工作或清空 journal。重新核实 Steam build `26337366`、游戏0.46D / VERSION100-Rev6、程序集 SHA-256：

`6398d44aa1675e1f02b63bc668868f970e0f5d3ae5a50f2aced8d06ca084e3b5`

沿用 netstandard2.1、安装 ID `pawnpilot_m0`、普通 network / reflection-external 权限、localhost broker 与五个 MCP 工具。没有开发者信任模式、模型 API、新桥接框架或任意方法接口。

M2 基线重跑 **317 managed + 135 Python = 452通过、0失败**。首次 Python 沙箱运行出现16个 Windows stdio 管道权限错误；在宿主权限下完整重跑135通过，未将受阻测试算通过。

## 原生入口与实现

证据为本轮针对当前 DLL 的只读 Mono.Cecil 调查；原始 IL 留在忽略目录 `artifacts/m3a/research/`，不提交游戏 DLL 或大段反编译内容。

| 功能 | 原生入口及边界 |
|---|---|
| 方向 | NPC `ClientIntent.BUY` → `ResolveMode` → `BuildBuying`，玩家卖出；SELL仍表示玩家买入 |
| 需求 | 当前已显示对话；兼容可选 `request_text` 尝试读取 `infoHost` 渲染的兴趣 Tag/Text，不导出隐藏蓝图、预算或真伪 |
| 名称 | 已可见直接库存成员的 `GetDisplayName(false)`；不构建鉴定 tooltip 或调用价格 getter |
| 准备 | 单件自有主库存商品 → 空后柜，使用原版快捷转移 `GraphUtils.TryAcceptOnce(backInvWindow,item,-1)`，保留容量及移除/接收/报价回调 |
| 报价 | `BuildBuying` 正常总价及已核实行闭包，绑定顾客接受的 owned 后柜子集；首版限定恰好一件 |
| 成交 | 单次 `OnSellAllClick` → 原生预算检查 / `SellMultiple` → `TryReinspectBeforeSale` / `CanSellThisItem` → `SellItem` → `SoldItem.TrackItem` |
| 整理 | 原菜单 `buttonSortSmallStart` 对应 `InventorySortHelper.Sort(player,false,false)`，固定靠上、小件优先，无模式参数 |

主要实现为 `GameSaleActions` / `SaleVerifier`、`GameInventoryActions` / `InventoryVerifier`，扩展现有 Reader、Projection、ActionPolicy 与 ProbeBehaviour。没有直接写现金、归属、原生库存成员、位置或顾客完成标志。

普通 BUY 范围为 `spacerFood` / `spacerFoodStory`；本轮成交是第一位教学食品买家，走正常食品价格与卖出结算。普通 SELL 保留 scavGeneral、shadyPharma，并接入 `food_scav_story` 正常路径；mentor 仅开放当前必要对话，不开放交易/遣走。教学食品客户遣走只允许本版已核实的原生回调；不扩展第二位混合交易买家或特殊业务。

实机定位并修复默认库存移除回调应为 `EmporiumEntry` 的 `b__184_20`，不能把后柜回调当作主库存回调；教学遣走运行时嵌套类型名为 `StoreClientListStory+<>c`。移除了会改变库存排序的容量探测调用，准备仅由原生转移完成。整理原生返回 false 表示未完成，不能误记成功；true 且集合/数量/归属/现金不变时允许已排好、布局无变化。原生完成计数使无布局变化的成功调用也产生新 revision。

## 契约与后置核验

保持 wire v3 / business details schema2，wire v2、schema1 和历史 journal 保持可读。新增兼容可选 `customer.request_text`，schema2允许 `trade_type=buy`。顶层 `item_refs/collection_fingerprint/offer_ref` 按方向绑定实际子集；`eligibility.purchase_enabled` 仍只用于买入。

| 新动作 | 参数及结果核验 |
|---|---|
| `prepare_sale_items` | `customer_ref,item_refs,collection_fingerprint`；逐件生成独立合法选项。核对目标从 player 到 back、完整集合差分、现金/数量/归属保留及新报价引用 |
| `sell_visible_offer` | `customer_ref,item_refs,collection_fingerprint,offer_ref,mode=normal,amount`；联合核对真实对象身份、现金增加、后柜移除、sold 接收与归属变化、无关成员保留 |
| `sort_inventory_default` | 仅 `container=player`；当前无顾客、受支持主库存、无拖拽或已知阻挡。核对原生完成、全部商品身份/数量/归属及现金保留 |

legal_actions 上限64，同一动作可有不同商品参数，精确重复条目仍拒绝。继续使用 `Accept → Advance → Acknowledge`、精确世界/版本、单写、去重与未知不重放。原生再检查改变报价但未结算、部分效果或核验不足，不能凭 UI 变化报成功，也不自动重复卖出。

## 真实 Codex MCP 验收

使用非 Astra `gpt-5.6-luna` Codex CLI，原始 JSONL 位于 `artifacts/m3a/codex/`。五工具 `health_check/observe/act/get_action_status/wait_for_update` 均实际调用。全部15次写入均按新鲜 legal_actions 提交，并使用原 command ID 查询到 `succeeded / postcondition_observed`；没有未知重放或直接 HTTP 代替验收。

| 测试槽 | 实际范围 | 写入/经济操作 |
|---|---|---|
| save_11，02:08:10 | 跳过教学的新槽；接待2位供应客、正常遣走，修复后在同槽初始早晨核验整理 | 6次写入，1次整理，0买入、0卖出 |
| save_12，02:18:25 | 默认教学新槽；mentor、食品卖家、第一位食品买家，共3位 | 9次写入，1次整理、1笔卖出，0买入 |

第一槽当天没有普通 BUY 样本，未跨日。第二槽自动教学产生的两份自有笔记保留前柜，未把它们算作卖出所得。没有关门、日结或跨日；没有为库存额外购买。

### A1 普通卖出：PASS

`m3a-flow-2d.jsonl` 中，Codex 读取买家当前显示的“我想买些食物。”，依据库存名称，在合法候选肉类/果汁中选择一件“泥炭”合成肉，`item-100`，数量1。

| command ID | 实际结果 |
|---|---|
| `m3a-flow-2d-receive-1` | 接待食品 BUY 客户 |
| `m3a-flow-2d-prepare-sale-1` | 主库存8→7、后柜0→1，目标仍自有，现金550；更新普通报价29及 `offer-110` |
| `m3a-flow-2d-sell-1` | 正常价29成交，现金550→579；目标离开后柜、进入原生 sold 集合且不再自有，未售商品保留 |
| `m3a-flow-2d-dismiss-1` | 正常遣走，最终顾客 absent，现金579、主库存7，`receive_next_customer` 再次合法 |

收入为29，不把收入等同利润。两个前柜自有笔记及其他库存保留。结算由运行时真实对象证据联合验证，不只检查现金或菜单关闭。这些引用只属于该会话，不可复用。

### A2 对话与正常拒绝：PASS；手动推进/选项未覆盖

第一槽两次 `dismiss_customer`、第二槽 `m3a-flow-2c-001` 对食品卖家的拒绝均成功；食品卖家5件未买商品由原生清理，两份自有笔记保留。卖出后的遣走也成功，可继续接待。

M2 stale_state 来自打字内容及自动推进改变观察版本，执行前精确检查拒绝旧状态；本轮保持保护、优先有界等待自动播放。没有必要的手动等待或可选对话样本，因此 `advance_dialogue` / `choose_dialogue_option` **未取得本轮成功实机样本**，没有为样本制造特殊剧情。

本轮由已授权 Computer Use 关闭新槽提示、处理默认对话设置（打字/自动播放保持开启），并点击食品卖家打开正常交易面板。商品准备、卖出、整理、正常遣走均由 MCP 完成；不宣称从新建槽到成交全程无需 GUI 协助。当前 MCP 没有通用打开交易面板动作。

### A3 原生整理：PASS

`m3a-sort-1-001`（`m3a-sort-1.jsonl`）和 `m3a-flow-2-sort-1` 各执行一次固定默认整理。主库存分别14件、8件，实际原生返回完成且布局变化，集合/数量/归属及现金550保留；第二槽随后完成卖出。没有格子规划、自定义坐标或额外模式。

### 运行限制与证据

`m3a-flow-2` 完成4次写入且均查询终态后，CLI 在无变化状态重复只读等待、超过提示中的等待次数；主 Agent 停止该 CLI，不能将该整次进程宣称正常完成。核对已有终态后用剩余预算继续。`m3a-flow-1b`、`m3a-flow-2b` 无写入。脚本增加 `--max-customers`、`--max-writes`、`--skip-sort` 与三次无变化等待后停止提示；这些是客户端指令，累计授权预算由调用者跟踪，不是服务器强制预算服务。

逐次审计在 `artifacts/m3a/live/mcp-write-audit.json`。没有通过停止客户端撤销或重放可能生效的命令。

## A4 自动化与独立审查：PASS

最终 **358 managed + 145 Python = 503通过、0失败**。覆盖买入回归、方向及可见需求、单件准备与过期引用、报价绑定、现金/物品完整结算、部分效果未知、正常拒绝/对话策略、固定整理、wire v2/v3兼容与既有去重/恢复。

```powershell
dotnet build src/PawnPilot.Probe/PawnPilot.Probe.csproj -c Release --no-restore
dotnet run --project tests/PawnPilot.CoreTests/PawnPilot.CoreTests.csproj -c Release --no-restore
.venv\Scripts\python.exe -m unittest discover -s tests -p 'test_*.py'
```

构建0错误；NU1900为离线漏洞源检查警告。记录：`artifacts/m3a/build-live-fixes.log`、`managed-live-fixes.log`、`python-final.log`。managed 测试不包含 Unity，实机证据另列。本轮使用5个非 Astra Luna 子 Agent，包含独立审查；主 Agent 复核协议、原生回调和实机结果后集成。没有为回归复制历史13件买入。

## 授权、备份与清理

用户批准完整范围：Steam/游戏窗口、先备份再安装三文件、普通 Mod 权限，最多2个独立新槽，每槽最多6位顾客/24次 MCP 写入、1笔卖出/1次整理；必要时每槽最多1笔买入且支出不超过550；不操作原进度、不特殊交易/议价/日结/跨日。实际分别2/3位顾客、6/9次写入，零买入，均在额度内。授权包含修复重装复测及结束清理。

首次查看为主菜单，没有加载原进度。原文件与配置备份在 `artifacts/m3a/live/backup-before/`，本轮前后哈希为 `before-files.json` / `after-files.json`，清理审计为 `cleanup-audit.json`。测试槽删除前另存副本；当日交易没有通过睡觉跨日保存，最终列表仍显示新槽初始550，实际579成交由实时 MCP/运行时证据证明。

通过游戏 UI 删除 save_12、save_11，核对列表仅剩原第7天进度；通过 UI 停用 Mod、正常退出。安装脚本核验路径、恰好三个文件和最新收据哈希后，仅删除本轮 DLL/manifest/session 和空目录。broker 已停止，127.0.0.1:18765不再监听；journal、session、备份、日志及原始证据保留在本机忽略目录。

| 原文件 | 清理后 SHA-256，与本轮开始相同 |
|---|---|
| save_0.es3 | `06130A952E024DD113747EB563459F0395886F96C8092BC229767B6FA13B95F7` |
| save_0_day5_20261031-060251.es3 | `DA891F60E6FB70EC6F1553148C2D5CD3C547F5394AD93E897E32583DE662B1BE` |
| save_0_day6_20261031-112207.es3 | `488D48FDF1C695996D6B1DD5DB84A11FC0C1D19167033B91AA692330E0408F94` |
| Assembly-CSharp.dll | `6398D44AA1675E1F02B63BC668868F970E0F5D3AE5A50F2ACED8D06CA084E3B5` |

原存档未加载/覆盖/删除。全局 `SaveFile.es3`、`saves_index.es3` 及部分日志按正常创建/删除、设置、信任记录和日志轮替变化，未声称整个配置目录字节不变，也未盲目覆盖恢复这些全局文件。轮替的旧日志保留在初始备份。

## 尚未覆盖与交付

- 本轮需求证据来自当前对话；可选兴趣标签 `request_text` 未获得非空实机样本，可能缺失，不应依赖它单独决策。
- 单件、主库存至空后柜、已支持食品 BUY 路径；未覆盖多件卖出、复杂物品/子容器、其他买家、预算不足确认或再次检查改价后的完整交易。
- 手动对话/选项尚无成功实机样本；打开面板等准备仍可能需要已授权界面协助。
- 议价、批发、特殊交易、鉴定、日结及跨日未实现，不列为本阶段失败。

交付分支 `feature/m3a-basic-sell`，提交信息采用英文类型加中文说明；只提交源码、测试、协议、Skill及本报告，不含本机原始证据、游戏 DLL、存档、密钥或 journal。不 merge main、不 force push、不发布 Release。最终提交 SHA 与远端核验结果在交付消息报告，避免报告自引用提交哈希。
