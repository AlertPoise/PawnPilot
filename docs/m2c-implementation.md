# M2-C 经营观察

当前状态：**PARTIAL（本轮开发和只读实机验证完成，报价 Gate 尚未通过）**。正式 wire v3 承载 business schema 1；wire v2 的 schema、fixture 和历史 journal 语义保持冻结。旧记录不重标版本，journal 容器版本仍为 2。正式写白名单仍仅 `close_menu`。

## 已实现

- `BusinessCustomerReader` 读取当前到访实例、到达字段和当前对话显示阶段；`BusinessProjection` 发放世界内短期引用。蓝图、未来队列、隐藏预算、对话全文及回调不进入 DTO。
- `BusinessReader` 仅枚举前柜、后柜和玩家主库存的直接成员，不递归闭合容器。每个容器独立返回 complete / incomplete / unavailable / unimplemented 和原因。几何过滤或读取失败不会成为正常空库存。
- 同一对象跨容器保持 item ref；数量、所有权或形状信息变化、消失重现、客户替换、世界变化会使决策引用失效。未验证的名称和形状保持 null / unverified。
- 从已有 TMP 渲染字符缓冲读取可见报价行；不调用价格 getter、刷新、raycast、容量接收或业务方法。明确拒绝复合批发价文本冒充一个普通总价。未绑定行的 item_ref 为 null，quote 未验证且 offer_ref 为 null。
- 非空完整后柜集合可生成候选 collection_fingerprint，绑定世界、顾客、交易类型、顺序、身份、数量、归属、容器与观察到的报价状态/条款。它不证明当前报价有效，也不授权购买。
- 已知菜单、确认、对话、拖动和选择等阻挡；未知全局 UI 顺序保持 unverified。教程/背景等常驻根的 active 状态不单独当作真实 modal。没有固定帧数或 200ms 经济一致性规则。
- `BusinessWire` 严格验证并深拷贝 DTO；限制 64 项、3 容器和总文本预算。五个 MCP 工具保留，observe 返回同一主线程采样的状态。

## Gate 与边界

| Gate | 当前结论 | 尚缺证据 |
|---|---|---|
| Q | PARTIAL | 可见行到真实完整商品集合的可靠绑定、当前刷新完成和普通/批发分支 |
| C | PARTIAL | 受限纯容量模型已编码，真实运行时规则、形状及回调证据尚未接入 |
| UI | PARTIAL | 已知根可阻止动作，但公开字段不能证明完整顶层遮挡与输入顺序 |

`BusinessUiReader` 的几何候选不能冒充全局无遮挡。active 的窗口和行仍可能被未知 UI 遮挡；该状态始终注明 unverified。容器 complete 仅表示直接成员枚举和局部几何/已知遮罩检查通过；当前仍输出这些候选的数量、归属和形状，不能把它们当作已经证明无遮挡的玩家可见事实。全局可见性及最小暴露仍是未解决边界。诊断 reader 仍由 M2AReadOnlyProbe opt-in；正式 reader 与共用可见性 helper 默认编译。最初曾怀疑旧 shape 类型检查不可能成立；主 Agent 复核 Cecil 的 IsInterface/Interfaces 后纠正：GridShape 是接口，GridShapeBuilder 实现该接口，旧 exact-type guard 并非逻辑错误。没有把这个未成立的猜测计作已修复缺陷。

集合 getter 本身返回复制后的 List；本项目随后限制扫描/输出，但公开 API 没有可先读取的原始 count，因此无法限定该 getter 内部复制的分配量。这一主线程开销限制仍保留，不以输出上限冒充采样成本上限。

## 验证记录

基线复现：108 managed + 100 Python = 208 通过、0 失败。沙箱 Python 首跑 14 项因 WinError 5 子进程管道权限失败，正常宿主权限重跑 100/100 通过。基线提交 `1db9927` 保存此前未提交成果。

最终回归：276 managed + 127 Python = **403 通过、0 失败**。覆盖客户/世界/对话/物品引用、完整与部分集合、已知/未知 UI、异常清空、字符预算、严格 v3、深拷贝、v2 保留以及跨帧去重。视口修复后 managed 再跑 276/276 通过。默认与 opt-in 诊断均构建成功；离线 NuGet 漏洞数据查询产生 NU1900 警告，不影响编译。fixture 不计作 Unity 结果。收尾曾有一次 Python 整套运行停在跨进程模拟后，被精确终止；该次不计通过。单独重跑该项通过，随后带线程诊断的整套重跑 127/127 通过（69.555 秒），停滞未再复现、原因未确定。

### 本轮实机结果（2026-10-31 至 11-01）

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

复测还暴露实际集合限制：9 个后柜对象中包含 1 件 owned=true 的对象（画面上的报纸），只有 8 件待售物品。当前 collection fingerprint 仅支持完整、全未归属后柜，所以此处保持 null；不能把完整容器冒充完整可购买集合。报价行仍无物品绑定，复合普通/批发 footer 未被解析成单一金额，offer_ref=null。Q/C/UI 仍为 PARTIAL。

原始证据仅在忽略目录 `artifacts/m2cd/live` 与 `artifacts/m2cd/codex`：`m2cd-quote-1.jsonl`、`m2cd-quote-2.jsonl`、`m2cd-viewport-fixed.jsonl` 包含真实 MCP 工具调用。截图、只读快照、Player.log、安装收据、备份及哈希不进入公开仓库。

### 清理与保护

两个测试槽已通过游戏 UI 删除，Mod 已在 UI 停用，游戏正常退出；安装收据匹配的 DLL、manifest、session 三文件及空目录已删除，本轮 broker 已停止。原有 save_0 及 day5/day6 备份三个文件 SHA-256 全部不变，游戏 Assembly-CSharp.dll SHA-256 仍为 `6398D44AA1675E1F02B63BC668868F970E0F5D3AE5A50F2ACED8D06CA084E3B5`。根目录基线比较发现保存索引与 Player/Player-prev 日志变化；Mod 正常启停也涉及信任记录，未强行覆盖恢复这些运行记录。最终实测默认 DLL SHA-256：`6140F5E371A7B85DFCFBCC0BA0F19601706D26EFE8EB08F4674EF743CC26420C`。

## 本轮已批准的实机范围

依据本轮任务第 4.2 节重新申请；用户已回复“批准上述完整范围”。授权仅覆盖下列只读回归；不扩展为新 MCP 写动作或购买授权。

1. 使用 Computer Use 操作 Steam 与 Probably Stolen。若当前窗口加载原存档，暂停操作并报告；不加载既有进度。
2. 读取并记录游戏程序集及原存档哈希，将原存档备份至项目忽略目录。游戏正常退出后，仅安装本轮默认 Release DLL、manifest 和 session.json 至 Mods/pawnpilot_m0，保存安装收据。正常启用 Mod/network，不开开发模式。
3. 最多新建两个独立空白测试槽。通过正常游戏 UI 开门、接待首位普通顾客、推进对话，并按正常集合搬运按钮准备报价。每槽仅观察一组报价，不购买、不出售、不议价。
4. 真实 Codex MCP 只调用 health_check、observe、get_action_status、wait_for_update，核对顾客/集合/报价状态、现金、截图和只读日志。UI 打开并关闭 ESC；打开已有正常确认框后取消，用来核验阻挡与引用失效。不会由 Agent 自动解除人工暂停。
5. 如需进一步验证，只允许在同一获批范围内正常退出、按收据卸载及重装已构建的读取器修复，保持测试槽和无经济交易上限；不进行故障注入或放开经济门禁。
6. 结束后在游戏 UI 删除本轮测试槽、禁用 Mod、正常退出；按收据删除三文件及空目录，停止本轮 broker。复核原存档/游戏程序集哈希并记录索引、设置、信任记录和日志的正常变化。

需要实机的原因：CLI、纯模型和 IL 无法证明当前 Unity 实例的渲染、实际可见性过滤、刷新时序与正常 Mod 安全扫描。副作用限于新槽、保存索引/设置、正常 Mod 启停/信任记录和日志；无实际经济购买。
