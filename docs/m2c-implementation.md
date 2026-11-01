# M2-C 经营观察

当前状态：**PASS（本轮基础普通买入闭环）**。wire v3承载新business schema 2，历史schema 1、wire v2及journal原版本继续兼容，journal容器版本仍为2。真实MCP已完成开门、接待、集合搬运、普通报价读取、一次购买及离场核验；详细动作级范围与验证缺口见[M2-D](m2d-implementation.md)。

## 已实现

- `BusinessCustomerReader` 读取当前到访实例、到达字段、可见对话文本、typing/manual-wait 状态和当前可见选项；schema 2 对选项发放短期引用。蓝图、未来队列、隐藏预算和回调不进入 DTO。历史 schema 1 仍可验证/读取。
- `BusinessReader` 仅枚举前柜、后柜和玩家主库存的直接成员，不递归闭合容器。每个容器独立返回 complete / incomplete / unavailable / unimplemented 和原因。几何过滤或读取失败不会成为正常空库存。
- 同一对象跨容器保持 item ref；数量、所有权或形状信息变化、消失重现、客户替换、世界变化会使决策引用失效。未验证的名称和形状保持 null / unverified。
- 从 TMP 当前渲染字符缓冲读取可见报价行和 footer。schema 2 将普通 `displayed_total` 与批发 `wholesale_total` 分开；旧 schema 1 的严格行绑定规则不变。`VisibleQuoteBinding` 通过已启用的 normal reflection-external reader 读取两种已核实 row callback closure 的 public item 字段，将本次报价精确绑定到当前实际 purchase subset；不能逐行确证的 `row.item_ref` 可为 null，不影响整单集合绑定。实机普通/批发总价119/93、298/224及最终444/338均与当前报价相符。
- schema 2 区分完整后柜与实际待购子集：完整 back 保留所有直接对象（包含 owned），顶层 `item_refs`/`collection_fingerprint` 绑定 OnBuyAllClick 的实际非 owned 子集；`front_collection_fingerprint` 独立绑定 prepare 的前柜集合。引用、顺序、数量、归属和世界/顾客均参与失效。指纹本身不授权购买。
- 容量读取按目标库存实际 occupied mask 处理既有物品（可为非矩形）；新接收项仍限简单、无嵌套的已支持 profile。最终范围允许已核实的默认GameItem onSlotAdd、现有features，以及content/examine两种窗口均无childElement的空窗口；不外推未知自定义回调、非空复杂子窗口或其他未支持接收条件。真实purchase已成功验证13件商品接收。
- 已知菜单、确认、对话、拖动和选择等阻挡；未知全局 UI 顺序保持 unverified。教程/背景等常驻根的 active 状态不单独当作真实 modal。没有固定帧数或 200ms 经济一致性规则。
- `BusinessWire` 严格验证并深拷贝 DTO；限制 64 项、3 容器和总文本预算。五个 MCP 工具保留，observe 返回同一主线程采样的状态。

## 当前实现边界

`BusinessUiReader` 对未知的全局 UI/raycast 顺序仍报告 unverified；业务动作按已知目标控件和动作自己的前置条件检查，不用全局一致性作为通用阻断。容器 complete 表示直接成员枚举、局部几何与已知遮罩检查通过，不等价于整个场景无遮挡。诊断 reader 仍由 M2AReadOnlyProbe opt-in；正式 reader 与共用可见性 helper 默认编译。报价项引用不保证每行都有 `item_ref`；整笔 purchase subset 则按两种确认过的 callback closure item 身份精确绑定。

集合 getter 本身返回复制后的 List；本项目随后限制扫描/输出，但公开 API 没有可先读取的原始 count，因此无法限定该 getter 内部复制的分配量。这一主线程开销限制仍保留，不以输出上限冒充采样成本上限。

## 验证记录

基线复现：108 managed + 100 Python = 208 通过、0 失败。沙箱 Python 首跑 14 项因 WinError 5 子进程管道权限失败，正常宿主权限重跑 100/100 通过。基线提交 `1db9927` 保存此前未提交成果。

上一轮回归：276 managed + 127 Python = 403 通过。**当前M2-C/D回归为317 managed + 135 Python = 452通过、0失败**。覆盖 schema2 对话/报价字段、混合归属集合、callback 整单绑定、purchase adapter preflight/身份核验、逐动作门禁、executor 和 v1/v2/v3 兼容。自动化不等于经济实机成交；fixture 不计作 Unity 结果。

### 上一轮只读实机结果（历史，2026-10-31 至 11-01）

用户明确批准本节计划后执行，创建 `save_6`、`save_7` 两个独立学徒空白槽，未加载原进度。正常启用 network，开发模式关闭。游戏日志确认默认包通过扫描，running=True，trust=Match / Updated，BRIDGE_READY / CONNECTED；wire v3 实际交换成功。

| 核对项 | 实测结果 |
|---|---|
| 第一组报价 | 正常 UI 集合搬运；7 行价格合计 448，批发价 372；真实 Codex MCP 读到同样的名称/价格文本，cash=550 |
| 第二组报价 | 8 行价格合计 192，批发价 150；真实 Codex MCP 只读核对成功，cash=550 |
| 视口修复复测 | 复用第二槽；前柜 9 个对象完整移动到后柜，item-36 至 item-44 保持；玩家 14 个引用不变；cash 550→550 |
| 复测报价 | 8 行价格合计 275，批发价 223；MCP 报告三容器直接成员与局部几何检查 complete、front 空、back 9、player 14；这不证明全局无遮挡，quote/UI/capacity 保持 unverified |
| ESC | interaction=blocked / escape_menu，报价清空、物品决策引用清空；未调用 MCP close_menu |
| 确认后取消 | **未完成**。误把当前铃铛当作确认框入口，实际直接遣走顾客并自动叫下一位；没有确认框。已记录偏差并停止该尝试，未把它计作取消通过 |
| 实际经济交易 | 无买入、无卖出、无故障注入；三个真实 Codex 会话都未调用 act |

实机发现并修复：PixelWindow.rectTransform 和 GameGridInventory.rectTransform 是零面积逻辑 handler，原来的正面积/包含检查错误地拒绝了可见库存。复核 IL 后改为窗口 active/IsVisible、childElement 精确链接和库存公开 inner.rectTransform 的实际 viewport；物品 rectTransform 返回 middle；当前程序集 ValidateSelect 的 IL_00ae/00bc 按 shape 宽高乘 gridSize，IL_0118/0123 写入 middle.sizeDelta，因此沿用其局部包含检查。这不证明任意布局、旋转和全局遮挡均已覆盖。没有移除 Canvas、alpha、遮罩、屏幕边界检查，也未提升全局 UI 一致性。定位失败原因保留固定 token。另修复诊断 CLI 在 GBK 终端输出 TMP 私用货币字符时的编码异常，改为可无损反序列化的 JSON Unicode 转义。诊断写命令改为显式选择核对快照的 v2/v3，缺失或不支持的版本在发送前拒绝，新增三项 CLI 回归。

在当时的 M2-C 基线版本，复测还暴露实际集合限制：9 个后柜对象中包含 1 件 owned=true 的对象（画面上的报纸），旧 collection fingerprint 仅支持完整、全未归属后柜，所以该快照的 fingerprint 为 null；报价行无物品绑定，复合普通/批发 footer 未解析成单一金额，offer_ref=null。后续 schema 2 已把完整 back 与非 owned purchase subset 分开；这条记录保留为旧版本实测，不代表当前 schema 2 行为。

原始证据仅在忽略目录 `artifacts/m2cd/live` 与 `artifacts/m2cd/codex`：`m2cd-quote-1.jsonl`、`m2cd-quote-2.jsonl`、`m2cd-viewport-fixed.jsonl` 包含真实 MCP 工具调用。截图、只读快照、Player.log、安装收据、备份及哈希不进入公开仓库。

### 当前基础买入验收（2026-11-01）

在本轮独立save_8/save_9测试槽中，正常启用network与reflection-external，开发模式关闭。真实Codex CLI经五个PawnPilot MCP工具执行，Computer Use仅负责启动、测试槽、查看证据及清理。

最终FLOW-4中start/receive/prepare均succeeded；MCP读取当前可见普通对话并等待其自动结束。搬运front14→0、back0→14，原player14不变、cash550不变；back包含一件自有报纸，实际购买子集13件。报价明确为普通444、批发338，容量sufficient。

C-1购买命令finish-c-1-C-7f8f3f7e-7a0d-4e12-9f2d-4b0c8a5d1e63经真实MCP提交并查询到succeeded / purchase_settled：revision12→13、现金550→106、back14→1、player14→27。13件非owned商品通过内部对象身份核验入库并owned，公开引用因ownership变化更新；原14件玩家库存和自有报纸保留。随后MCP等待到revision18，customer absent且receive_next_customer可用，没有第二次购买。

较早prepare样本普通/批发报价119/93、298/224、340/259、226/181等；dismiss及再次receive也有MCP成功证据。手动advance三次因stale_state在执行前被拒，choose未实机；成功流程中的自动对话不算这两个动作验收。shadyPharma支持代码已接入，但仅scavGeneral实机成交。基础买入闭环PASS，不扩展为所有动作/顾客通过。

修复的具体接线问题包括：场景内world-space铃铛/搬运控件定位；可见报价闭包的商品身份关联；owned报纸与purchase subset分离；默认接收回调、features与空窗口误阻断；所有权变化后的结算身份关联；MCP ObserveReply遗漏purchase名称。最后一项在实际购买前暴露，修复后保持同一游戏状态重新观察并成功购买，未用旧命令盲重试。

完整范围、前后证据及限制见[M2-D](m2d-implementation.md)。真实调用在本机忽略目录artifacts/m2cd/codex/finish-flow-4.jsonl与finish-c-1.jsonl；purchase-summary.json、Player日志及截图保留本机。故障回归来自自动化，没有实机经济故障注入。

### 清理与保护

本轮两个测试槽save_8/save_9均通过游戏UI删除；Mod停用、游戏正常退出，按收据移除匹配的DLL、manifest、session三个文件与空目录。broker已停止，18765不再监听。未加载原进度，save_0及day5/day6备份哈希均不变，Assembly-CSharp.dll哈希不变。本轮存档根目录变化为游戏日志、Player日志轮转和saves_index，未盲目覆盖全局文件。证据在artifacts/m2cd/finish/live/cleanup-summary.json及before/after-files.json；不提交备份、密钥、日志或截图。上一轮save_6/save_7清理记录仅为历史，未充当本轮清理证据。

## 上一轮 M2-C 只读实机授权范围（历史）

依据本轮任务第 4.2 节重新申请；用户已回复“批准上述完整范围”。授权仅覆盖下列只读回归；不扩展为新 MCP 写动作或购买授权。

1. 使用 Computer Use 操作 Steam 与 Probably Stolen。若当前窗口加载原存档，暂停操作并报告；不加载既有进度。
2. 读取并记录游戏程序集及原存档哈希，将原存档备份至项目忽略目录。游戏正常退出后，仅安装本轮默认 Release DLL、manifest 和 session.json 至 Mods/pawnpilot_m0，保存安装收据。正常启用 Mod/network，不开开发模式。
3. 最多新建两个独立空白测试槽。通过正常游戏 UI 开门、接待首位普通顾客、推进对话，并按正常集合搬运按钮准备报价。每槽仅观察一组报价，不购买、不出售、不议价。
4. 真实 Codex MCP 只调用 health_check、observe、get_action_status、wait_for_update，核对顾客/集合/报价状态、现金、截图和只读日志。UI 打开并关闭 ESC；打开已有正常确认框后取消，用来核验阻挡与引用失效。不会由 Agent 自动解除人工暂停。
5. 如需进一步验证，只允许在同一获批范围内正常退出、按收据卸载及重装已构建的读取器修复，保持测试槽和无经济交易上限；不进行故障注入或放开经济门禁。
6. 结束后在游戏 UI 删除本轮测试槽、禁用 Mod、正常退出；按收据删除三文件及空目录，停止本轮 broker。复核原存档/游戏程序集哈希并记录索引、设置、信任记录和日志的正常变化。

需要实机的原因：CLI、纯模型和 IL 无法证明当前 Unity 实例的渲染、实际可见性过滤、刷新时序与正常 Mod 安全扫描。副作用限于新槽、保存索引/设置、正常 Mod 启停/信任记录和日志；无实际经济购买。
