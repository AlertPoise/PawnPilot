# M5-B：通用交互、原生输入与效果契约

## 结论与证据边界

M5-B **项目内实现 PASS；Unity 实机验收 PARTIAL，最终修复包复验 PENDING**。本阶段实现可调用的受限通用对话、主对话选项、正常长按遣走、可见 surface 和分层结果契约。没有任意方法调用、任意按钮或坐标动作。

真实 Unity/MCP 在一个新测试槽中完成 12 个写命令：11 个 succeeded，1 个 outcome_unknown。绿色名片顾客已实际出现；新通用长按遣走实际让其离场，现金不变，但首次实测暴露旧物品引用在顾客变化时全部重签，核验器将其误判为库存替换。该问题已修复并加入真实投影回归，**没有重放原命令，没有把旧 unknown 改写成 succeeded，没有清空 journal**。最终版本的特殊遣走完整成功回执、主动推进成功及可见选择提交仍缺实机样本。

## 基线、授权和环境

- 仓库保持 `D:\c\aaaprojects\PawnPilot\PawnPilot` 双层目录。
- 已核验本地 M5-A 分支 `feature/m5a-foundation-research`，HEAD `36dc481d59b7a5a6b02f864b7e400cefd4c2c686`；初始工作树干净。M5-B 分支为 `feature/m5b-generic-interaction`，没有从旧 M4 重建。
- 编码前重新运行 M5-A 基线：593 managed、202 Python 全通过。证据在 `artifacts/m5b/baseline-*.log`。
- 用户后续明确授权自主使用 Computer Use、Steam 和游戏验证，覆盖附件旧的实机禁止条款。仅新建 `save_18` 测试槽，未加载原 `save_0`；未启用开发者模式，未修改游戏 DLL、剧情内存或存档内容。
- 游戏为 0.46D / VERSION 100-Rev6 / Steam build 26337366。静态审核 DLL SHA-256：`6398D44AA1675E1F02B63BC668868F970E0F5D3AE5A50F2ACED8D06CA084E3B5`。对应 MVID：`83045710-adce-4e78-8ff1-b43a9399ec0d`。
- 已阅读 M5-A A1/A2/A5、M2～M4 实现文档、冻结协议、生产候选准入与执行链、Player Skill；原始静态材料不算实机证据。

## 架构与修改范围

`Interaction/NativeInteractionAdapter.cs` 负责 Unity 主线程只读捕获、对象绑定、原生调用和有限长按；`DialogueEffectCatalog.cs` 审核回调形态。`Runtime/InteractionState.cs` 定义 surface/receipt 和纯生命周期；`InteractionPolicy.cs` 执行 DTO 准入及效果核验。继续复用 `CandidateAdmission → ActionGate → ActionDriver`，没有第二套执行队列。

`ProbeBehaviour` 生产输出 v4，先捕获 surface，再生成候选、应用人工检查门禁、确定最终状态并严格校验。既有普通动作保留原验证，仅允许对应 v4 载体。Python `bridge.protocol/server/journal/client`、MCP Pydantic 与 JSON Schema 同步接入；仍只有五个 MCP 工具。

## 对话生命周期与可见信息

phase 区分 absent、typing、waiting_for_advance、awaiting_choice、completed、progressing、unsupported、unknown。status 独立表达 idle、progressing、awaiting_agent、awaiting_user、unsupported、unknown：有合法候选才能 awaiting_agent；Broker `can_act=false` 或 unknown 阻塞仍优先于 surface 状态。

判断依赖当前对话、原生打字/手动等待、自动推进标志、显示中对象、尚未执行的 endAction 队列、到场及即将开口状态。`activeCoroutines.Count` 包含自然结束后未移除的句柄，故不作进展证据。无明确 pending 时，不完整选项或 currentDialog 存在也不假报 progressing。

完成要求同顾客曾观察到真实对话、conversationEpoch 改变、已观察的末节点没有后继/选择、末回调已执行或为空、全部 pending 清空、主/次气泡和选项隐藏，并跨帧稳定。刚到场尚未开口、附着到未见历史的已完成特殊顾客、非末节点中断均保守 unknown。completed 表示主对话 UI 已收尾，不证明整个隐藏剧情结束；仍核验原生可遣走条件。

只输出 TMP 实际渲染的当前文本/已显示前缀，不输出未来台词、隐藏顾客 identifier、概率或随机结果。回调分类会只读检查可达图中的委托元数据与 predicate 存在性，但不执行 predicate/callback，不读取未来文本。捕获异常明确输出 `interaction_capture_failed`。

## 选项、控件和引用

主对话仅支持 `DialogChoiceBubble.type=0`；电话 type=1 不混入。最多 16 个控件，完整性要求当前原生选择集合、实际可见气泡、索引和渲染文本一一对应。隐藏/裁切/缺失/过量或不能绑定的集合保持 partial/unknown；禁用选项仍有只读引用，不可执行，不静默截断为完整。

surface/control 引用包括实例 nonce、world、顾客、对话、阶段、可见文本、实际对象身份、选项及可用性等签名。文本或对象变化会重签；命令另外固定 broker/session/world/revision。原生输入前在主线程重新捕获核验，同一输入仅一次。禁用项缺少旧业务 option_ref 时签发独立只读 ref，不伪造旧业务候选。

选择走当前实际 `OnDialogChoiceClick`，推进走 `OnAdvancePressed`。原生 UI 可用性使用组件、CanvasGroup、固定控件中心的只读 EventSystem 命中，要求最上层命中属于绑定目标；不接收 Agent 坐标。菜单和议价面板沿既有关闭/返回动作；WildNetwork 等新面板明确 unsupported，不添加通用关闭回调。

## 回调契约与顾客收尾

通用路径不按 NPC ID 分派。旧普通 NPC 白名单不再阻挡已审核组件和效果；未知 callback 仍不能执行，旧普通适配器的已审核范围保留。效果目录允许无回调、严格空 IL，以及在当前 MVID 下审核过且捕获对象匹配的两个回调形态：当前顾客可遣走赋值、最多一个自有文档入前柜并更新剧情状态。新机制或新程序集必须重新审核。MVID 检查只读已加载元数据，不新增 files 权限。

特殊顾客遣走要求已证明完成、当前顾客、OPEN、到场、原生按钮 enabled/无遮挡、`IsClientDimissable`、无门锁/逮捕/枪战/打字/暂停、无 inspection handoff，且 `OnDismiss` 与 `tryInterceptDismiss` 均为空。内建清理另要求三个容器完整、前/后柜直接物品全自有，并只读检查原生全物品图不存在会被删除的临时 feature 5/6，防止未覆盖的未购物品删除、修枪零件退回及跨容器变化。普通未购商品的遣走仍可走原审核适配器。

`StoreDismissButton` 同时实现 click/hold。适配器通过绑定的 `Interactable.OnPointerDown/OnPointerUp`，遵守当前 instant/hold 设置和原生 0.4 秒持续时间。每帧检查同一命令、世界、顾客、控件、原生 hold 状态和截止期限。取消先 Exit 再 Up，同步清 hold，防止后续迟到释放触发点击。只有真正输入返回后才调用 `ActionGate.MarkNativeReturned`；异步启动返回不冒充遣走返回。

## 分层结果与经济边界

v4 通用结果的 `interaction_result` 分别记录 input_receipt、verification、effect_contract、observed/unverified effects、现金差和增减物品引用。callback 返回不等于所有剧情效果完成；revision 改变不能单独证明成功。

- presentation：必须实际对话推进且三容器、现金不变。
- document_gift：允许零或一个新增自有前柜物品，原生额外证明它是 DOCUMENT；其余库存/现金不变。零表示有限输入已推进，不能据此声称未来赠物已完成。隐藏 story_state 始终 unverified。
- customer_departure：必须旧顾客已经 absent 或 replaced；允许下一顾客出现。现金和所有可核验物品的实例、位置、归属、数量仍须符合契约。
- 缺证据有界等待；意外现金/物品变化、超时或异常保留 unknown 和实际可观察差额，不重放。Broker/Journal/MCP 再次核验回执及声明效果边界。

实机发现的引用问题由 `BusinessProjection(preserveItemRefsAcrossCustomers: true)` 解决，**仅 v4 生产启用**。同世界、同真实物品、属性不变时保留 ref；世界变化、观察失效、物品离开或属性变化仍失效。顾客、对话、报价/集合依旧重绑定，旧顾客命令仍被拒绝。默认历史 projection 行为不变。

废料选择可以被发现和引用，但其付款、部分接收、清柜台和离场回调未获得本轮完整经济契约，因此不可执行。没有通过 generic choice 绕过普通金额、容量、人工检查或 unknown 门禁。

## 协议和历史兼容

采用独立 [wire v4](../protocol/v4-contract.md)，business schema 仍为 2，surface schema 为 1，MCP 外层 reply schema 仍为 2。三个动作名称不变，通用参数增加 surface_ref/control_ref；两者缺一拒绝。无这两个参数时使用冻结的旧 v3 动作语义。历史 v2/v3 快照、结果、journal 不重标、不新增字段。

ready 且 complete 的 surface 必须与 business 顾客引用一致；dialogue 必须绑定非空的同一对话，customer_completion 必须仍有顾客且无当前对话。kind=none 不能同时声明在场顾客，absent 只能用于 kind=none。矛盾快照直接拒绝，不静默修补；partial/unavailable 保留诊断能力。

首次完整回归发现 nullable surface 被 managed 序列化进旧 wire，已经修复：生产 Json.NET 使用按版本的 ShouldSerialize；managed harness 使用等价按版本转换器，包括 result 内嵌快照。原历史 fixture 未修改，也未放宽旧 Schema。跨层测试由 managed 导出生产形状，经过 JSON Schema、Pydantic、协议、Broker、journal 和真实 MCP stdio 测试。

## 验证记录

最终去重统计：**managed 671 通过、0 失败；Python 219 通过、0 失败；合计 890 项**，比 M5-A 基线增加 95 项。没有把多轮复跑相加。八组 managed 交互 fixture 的所有非空 before/after 均先通过生产 ActionGate 快照校验，再进入 Python Schema、Pydantic、Broker/journal 和 MCP 测试。

正式 Release 构建 0 错误、2 个既有 NU1900 漏洞源联网警告；Skill validator 与 `git diff --check` 通过。证据在 `artifacts/m5b/managed-final.log`、`python-final.log`、`build-final.log`。最终回归串行执行，避免 Python 子进程构建 managed harness 时与另一轮构建争用 DLL；中间一次 CS2012 文件占用不算通过，已由串行全套复跑替代。

覆盖包含生命周期、选项完整性/禁用/过期引用、多控件、重复语义候选、未知 callback、人工检查与日阶段门禁、泛型原生回执可信性、超时/取消不重放、库存及现金异常、普通动作回归、跨顾客物品引用和旧 wire 序列化。UI 目标命中/真实长按以及游戏回调本身的证据来自静态 IL 与下述有限实测，不能把 managed 测试计作 Unity 成功样本。

## Unity 实机：已完成与未完成

Computer Use 负责 Steam 启动、独立槽建立、关闭教程提示、观察和停用/退出；经营写操作由官方 MCP SDK stdio client → 本地 Broker → Unity 生产 Mod 执行，不通过 GUI 代点经营动作。Mod 使用正常 network/reflection-external 权限，开发者模式关。

`save_18`：学徒、无特质、普通难度、正常跳过开场。第 1 天两位顾客正常接待/遣走，无成交；正常关门、默认电费 5、日结至第 2 天并关闭晨报，现金 550→545。第 2 天再次开门，首位顾客可见自述“奥丁·哈拉尔德森”，MCP 观察到 trade_type=other、typing、document_gift 通用推进候选；下一次 fresh observe 已无推进候选，故没有发送 advance 命令。对话自然完成后前柜出现“名片（绿）”，surface=completed，合法动作是带 surface/control 引用的通用 dismiss_customer。

命令 `m5b-f26ee19a83b940d6` 经原生长按遣走后，顾客已离场、名片仍在前柜、现金545不变。结果为 `outcome_unknown/unexpected_item_removed`，input_receipt=returned；原因是首次实现尚未保留跨顾客同实例物品 ref。随后 Broker can_act=false，阻塞得到实机验证。本轮没有继续接待、没有用 GUI 完成其他经营、没有换 ID 或新 journal 绕过。

因此，绿色名片场景的实际组件、赠物、完成观察、特殊顾客合法候选和长按执行路径已获得实机证据；**修复后 succeeded 回执、主动推进和主对话选择执行、废料选项完整捕获尚未完成实机验收**。也未新增买卖、议价、整理或声望实测。

证据留在 `artifacts/m5b/live/`：按命令保存的真实 MCP JSON、action-summary、原 journal、player-end.log、保护文件哈希。测试槽保留，只有第 2 天早晨正常自动保存的落盘状态；不能把离场画面误说成已另行保存该剧情结果。已通过正常菜单停用 Mod 并退出游戏，按安装收据移除本轮 Mod 文件，停止本轮 Broker；最终进程和目录复核见 cleanup-final.json。Steam 保持运行。原 `save_0` 及本轮开始时的第17/18天历史文件已独立核对，三份文件 SHA-256 均保持不变；游戏 DLL 哈希也不变。saves_index/全局设置因新槽和正常设置发生变化，不宣称全目录不变。

## 后续最小验收与接口接续

重新实测前须人工核对并处理本轮 unknown 的审计边界，不能自动 reconcile 特殊遣走、清日志或重放同一选择。保留旧证据后，在明确隔离的验收运行验证：最终包的绿色名片正常完成→唯一通用遣走→returned/confirmed、现金不变、库存同实例保持、旧顾客失效；随后补一个安全主对话主动推进、一个完整主对话多选项提交及 stale ref/禁用/未知效果拒绝样本。废料只观察，不接受桶数。inspection 出现时仍需真人确认。

M5-C 可直接复用 surface.status/reason/completeness、world/revision/ref、五工具等待和 durable command journal；这不是已实现的 Decision Inbox 或长期监督器。M5-E 可扩展效果目录/核验器及选项完整性，不得把当前 document_gift 当作任意库存/付款合同。M5-D 的声望 effect 只是协议预留；电话、外出、任意自定义面板、报警/Aug/武器、废料经济执行、批发/易货完整自动交易均未开放。

仅本地提交，不 push、PR、merge 或改写既有历史。不提交游戏 DLL、IL 原文、存档、token 或原始长日志。
