---
name: pawnpilot-player
description: 使用 PawnPilot MCP 观察《深空当铺：可能是偷的》，按当前 legal_actions 执行已授权的普通经营动作。
---

# PawnPilot 单日经营、普通交易与人工检查接管

M4 单日经营使用同一次 Codex 经营会话连续处理顾客。按最新观察推进 PREPARE → OPENING → SERVING → BETWEEN_CUSTOMERS → CLOSING → SETTLEMENT → NEXT_MORNING → DONE；这些是 Agent 的工作阶段，不是必须按顺序点击的按钮。起始日期、累计接待数、写请求、买卖笔数、买入支出、议价次数与夜费上限应来自本轮明确授权；CLI 重启不能重置预算。进入起始日期的次日后，至多正常关闭夜报，立即停止，不调用 start_business。实机状态与已覆盖范围见 `docs/m4-implementation.md`。

查看 `business.details.day`：`remaining_customers` 只来自当前显示的待接待人数，不含当前顾客，null 表示未显示/未确认。没有 receive_next_customer 不等于当天结束。等待到离场/对话/门锁动画安定；只有 `can_close=true`、剩余人数0且当前 legal_actions 提供 close_business 时正常关门。关门成功后原生卷帘锁可保持非零，不要等待它归零或修改它。使用合法 open_end_of_day 通过睡袋打开结算，此动作尚未扣费或跨日。

日结的 `settlement` 包含当前真实显示的费用和服务选中态。保持游戏打开面板后的默认服务开关，不为凑预算开关服务。确认使用最新合法 confirm_end_of_day 的 settlement_ref、真实 amount 与本轮允许的 max_spend；预算不得超过可支付现金，负费用保留符号。费用或服务条款变化时旧引用作废。若费用超过授权、条款不完整或出现未授权选项，暂停说明，不自行推测固定电费。确认后查询原命令，核对日期恰好+1、MORNING、实际现金减少等于确认费用、结算界面退出以及次晨报告。出现额外经济变化或结果未知时不重复日结。close_day_report 只关闭次晨报告，不开门。正常载入早晨时，原生夜报也可能重新打开；stage=morning 且当前 legal_actions 提供 close_day_report 时，同样可以通过 MCP 关闭，核对日期和现金不变。这不表示本次会话已经完成日结。

每次写操作紧邻一次 fresh observe；一次只提交一个命令并按原 ID 查到终态。M4 中 rejected、超时或 unknown 都先查询原 ID 并说明阻塞，不自动换 ID 重试。动画和对话只做有界 wait_for_update，连续三次相同业务状态则停止盲等并说明具体阻挡；当前日期+1、人工检查等待或用户要求暂停时停止新的经营写入。人工确认后的恢复仍遵循下述 M3-C 规则，重新观察再继续当前日，不沿用旧报价。遇到不支持的顾客仅使用当前明确合法的已验证普通对话或 dismiss_customer；没有正常路径时报告需要人工处理，不绕过必要事件。

自动对话、打字或上货期间，即使 prepare 动作暂时出现在 legal_actions，也先用有界等待观察自动进展。准备商品前确认当前顾客已进入 trading，且同一顾客的商品集合在两次新观察中稳定；不要把对话中的报纸或未到齐的商品当成完整来货。

M4 会话审计只对直接由 broker 返回的明确 stale_state 入队前拒绝作区分：同 ID 查询 not_found 后，须有同 broker_epoch 的新观察、且没有已入队或未知状态证据，才记为已拒绝而非未知。仍保留累计消耗，并禁止对原世界/顾客换 ID 重做该动作；只能重新观察后选择其他合法处理方式。孤立 not_found、网络错误、超时和结果未知均不适用。

每次观察先查看 `business.details.inspection_handoff`。`requires_manual_inspection=true` / `status=waiting_for_user` 时，停止所有经营写动作，包括购买、议价、遣走、接待和关闭菜单；只读观察、状态查询和有界等待仍可用。首次看到新的 handoff_ref，直接在当前用户会话提醒：“PawnPilot 检测到当前顾客出售需要人工检查的商品：〔当前可见名称〕。我已暂停自动经营操作。请在游戏中手动检查，并自行决定是否质疑、购买或拒绝。处理结束后，请在本会话回复‘检查完成，继续’。”未知名称用商品引用表示，不猜名称或真假。

只有用户在当前会话明确表示检查已结束并要求继续（例如“检查完成，继续”或“检查完毕，继续”）之后，才可从最新观察复制当前对应的 `resume_inspection_handoff(handoff_ref)` 合法动作并提交。用户可能只检查、已手动买入、质疑、拒绝或遣走；不要要求鉴定结果。确认不能因时间经过、顾客离场、重连或普通实机测试授权而自动产生，不能挪给新的 handoff_ref。恢复动作只确认人工处理结束，不购买、不改变游戏经济，也不解除 broker 的未知命令阻塞。

核对恢复结果的 `last_resumed_handoff_ref`，然后必须重新 `observe`。顾客已离场或商品已自有时，不重复购买；仍待购时，只用新的商品引用、实际报价及合法动作，在现有授权、现金和容量范围内决定交易。若出现新的等待接管，再提醒并等待新的用户确认。同一世界/到访/实际商品实例的已确认批次由代码去重，不用名称去重。接管中的商品/顾客引用记录触发时对象，用户手动操作后不保证还在当前柜台。

正式分类仅覆盖已核实的海明威香烟和六种水容器家族，依据真实identifier而非名称或隐藏真假。不要自行查看未发现的鉴定特征，也不要操作放大镜或用GUI代替用户检查。默认关闭的单次测试触发需要独立实机授权；它仅验证接管流程，不能证明该测试商品需要鉴定。范围及证据见 `docs/m3c-implementation.md`。

M3-C真实CLI已完成测试商品接管、门禁拒绝、用户确认后恢复和重新观察：人工阶段现金550→324，五件商品已自有、顾客已离场，恢复前后现金324不变，Agent未重复购买。此样本不证明指定香烟/水容器实机触发，也不证明恢复后Agent自动购买；不要复用其金额或引用。

仅使用 `health_check`、`observe`、`act`、`get_action_status`、`wait_for_update`。动作按本次快照的精确 `legal_actions` 独立开放，包含已接入的开门、接待、普通对话、商品准备、正常购买及结束交互；注册名称或文档描述本身不代表当前可执行。`wait_for_update` 是有界只读等待。`act.schema_version` 必须等于同次观察的 `state.schema_version`（2 或 3），不能自行升版或混用会话。

开始时先 `health_check`，再 `observe`。只有 broker/bridge 已连接、协议兼容、世界 `ready=true`、`can_act=true` 且 `legal_actions` 包含该动作时才可提交。`act.schema_version` 优先复制最新 `observe.action_schema_version`，它等于同次嵌套 `snapshot.state.schema_version`；null表示没有已连接的游戏快照，不能提交。兼容旧Adapter缺少该字段时，才取嵌套版本。响应外层schema2及business.details.schema_version均不是动作版本。逐字复制同次version字符串。议价面板可能使全局interaction.operable=false，但其专属legal_actions仍可用。`close_menu` 仅用于明确授权的诊断，不能作为自主经营遇到人工暂停的恢复策略。失联快照属于历史数据；`loaded=false` 的现金占位值无效。

v3 `business.details` 必须结合 completeness/status/reason 解释。容器 incomplete/unavailable/unimplemented 的空 item_refs 不能解释成库存为空；未知名称、形状或报价金额不能推测补全。quote行来自当前渲染文本，单行item_ref可为null而整单绑定仍完整；普通displayed_total与wholesale_total必须区分。purchase item_refs/collection_fingerprint绑定实际非owned待购子集，完整back可同时包含owned物品；front_collection_fingerprint独立绑定prepare来源。指纹存在本身不授权购买。`candidates` 与 `legal_actions` 不同，购买要求 purchase_enabled=true、当前正常报价完整、容量 sufficient、金额不超过用户范围和现有现金，并精确匹配 legal_actions；开门、接待和对话只检查各自相关条件，不依赖报价或库存完整。不要拼接多个查询的不同帧，冒充同一决策快照。

M2 已实机完成普通拾荒客scavGeneral的一次正常购买，普通价444、现金550→106、13件入库并自有、顾客离场。报价和商品由观察决定，不可复用这些示例值。普通购买/准备/对话另支持shadyPharma，但尚未单独实机成交。复杂非空子窗口、未知接收条件、批发和特殊业务不支持。cancel_confirmation和独立refuse_trade不可调用；需要正常结束时，仅使用当前legal_actions中的dismiss_customer或等待离场。

M3-A 已实机通过普通单件卖出与默认整理，范围见 `docs/m3a-implementation.md`。`customer.trade_type=sell` 是 NPC 卖货、玩家买入；`buy` 是 NPC 买货、玩家卖出。普通卖出限 `spacerFood` / `spacerFoodStory`；本轮第一位教学食品买家成交29仅是样本，不可硬编码复用。从当前显示的需求、对话和库存名称判断，只选择当前 `prepare_sale_items` 合法条目中的一件自有物品。可选 `request_text` 可能缺失，本轮需求证据来自可见对话，未获得非空兴趣标签字段样本。动作精确绑定 `customer_ref,item_refs,collection_fingerprint`，将它从主库存放入空后柜；完成后重新观察正常报价，再按当前 `sell_visible_offer` 的精确 `offer_ref,item_refs,collection_fingerprint,mode,amount` 提交。成功后核对现金增加与该商品移出；不能用购买报价或批发金额代替卖出报价。原生再检查导致结果未知时查询原命令并停止写入，不盲目重复卖出。

`sort_inventory_default` 仅接受当前合法条目 `container=player`，调用原版固定靠上、小件优先整理一次；无模式参数，不规划格子或拖拽坐标。核对原商品集合、数量、归属和现金保留；原生返回完成时已排好允许布局不变，返回 false 不能算成功。整理和卖出仍需当前作用域的用户授权。

教学食品卖家支持普通流程和正常拒绝，mentor 仅支持当前必要对话，不开放交易/遣走。`customer.trade_panel` 为 `closed` 时，如果当前 `open_trade_panel(customer_ref)` 合法，使用 MCP 打开当前普通顾客的交易面板。对话/动画未结束时有界等待；值为 `buy` / `sell` 表示面板已打开且为对应 NPC 交易方向，不重复打开。`unavailable`、null 或缺失不能推测成关闭。

开面板后核对同一顾客、面板方向与 `trade_type` 一致、`phase=trading`、正常总价可读以及现金不变。空柜台可能显示0，报价仍为 unverified、没有 offer_ref；这不是可执行成交。需要商品准备时继续使用当前合法准备动作。原始 M3-A 实机曾通过 GUI 点击开面板，收尾验收以 `docs/m3a-implementation.md` 的新增记录为准。缺少合法动作时报告具体阻挡，不自行调用 GUI，也不要无限等待同一状态。

读取当前已显示对话和选项文字，不读取未来台词或隐藏客户信息。自动播放中的对话优先等待并重新observe；只有确需跳过当前打字或处于manual-wait且对应动作合法时提交advance_dialogue。选项只能从当前可见、可交互且有文本的option_ref选择；不循环盲点。手动advance/choose尚缺成功实机样本。

M3-B 普通议价使用现有五工具。`business.details.bargain` 缺失/null表示未知；根据当前合法 `open_bargain_panel(customer_ref,mode)` 打开，`mode` 是NPC方向buy/sell。准备商品并取得当前完整正常报价后，才可用 `choose_bargain_option(customer_ref,mode,option_ref)` 选择实际显示且enabled、未selected的选项。`lower_purchase_price` 是争取少付，`raise_purchase_price` 是玩家主动多付；`raise_sale_price` 是争取多收，`lower_sale_price` 是玩家主动少收。百分比表示价格变化方向，不是成功率。选择影响该顾客相应方向的交易条款，不限于一件选中商品。

由你依据当前报价、现金、已知成本/利润、经营目标和已显示提示决定是否议价、让步、直接接受或放弃。不要固定选最大折扣；未显示成本或成功率就按未知判断，不从隐藏预算/RNG补全。`reputation_text` 和 `hint_text` 只包含已经渲染的文本，后者通常需原生悬停才存在；null不表示没有风险。让步可能改变声望收益，只有可见提示能作为本次决策证据。

选择动作的succeeded只证明正常提议已提交；selected也不是接受。读当前顾客对话并等待：`result=responding` 尚在回应，`price_changed`/`unchanged` 只比较同一柜台集合的实际显示总价，unchanged不能单独证明拒绝（可能取整），`customer_left` 表示回应期间已离场。结合可见台词识别接受/拒绝，证据不足就明确未知。不要为获取成功随机结果重复议价。若需要手动推进而议价面板仍开，先用当前合法 `close_bargain_panel` 返回，再按现有对话动作推进。

议价不是成交。返回普通交易面板后重新observe，只按新offer_ref及实际displayed_total决定购买/出售；普通支付`mode=normal`已包含游戏生效的议价调整，不能自行乘百分比或复用旧报价。terms_revision变化使旧报价引用失效，即使总价未变。失败仍留场时可接受当前报价或正常结束；顾客离场则等待接待恢复。成交仍需完整商品绑定、容量、现金/支出授权和原结果核验。

M3-B真实CLI已对普通NPC SELL选择当前可见-10%，得到接受台词和265→238的新正常报价；因capacity_unsupportednestedcontent不可购买，合法遣走，现金550不变。该样本不是实际购买，也不证明卖出方向、拒绝/离场或议价后现金结算已实测。不得复用样本金額/引用。更多覆盖及限制见 `docs/m3b-implementation.md`。

act参数为顶层schema_version、command_id、action、arguments、broker_epoch、expected_world_epoch、expected_revision、expected_version，不套request对象；wait_for_update参数才放在request内。revision等待同时携带broker/world/revision身份，command_terminal按原命令ID等待。每次写操作前立即重新observe，不能把wait/status中的较旧快照直接当作最新决策。

每个动作生成唯一 `command_id` 并保留，原样传入刚观察到的 `broker_epoch`、`world_epoch`、`revision`、`version`（对应 `expected_*` 参数）。每次只提交一个动作，然后查询同一 ID 的状态及 `before/after`。菜单必须通过正常游戏入口打开；MCP 不提供打开菜单操作。

`pending` 表示未交付；`running` 表示已交付、可能已执行；`succeeded` 才表示已验证后置条件。`rejected` 表示未通过门禁。`failed` 不应被当作“肯定没有副作用”；先核对证据。遇到旧版本或状态变化，重新观察。

需要短暂等待时，`wait_for_update` 仅支持 `revision`、`command_terminal`、`operable` 三种条件，最长 30 秒；`max_wait_ms=0` 执行一次最多 50ms 的即时检查。超时可继续观察或重新等待。取消等待不会取消已提交命令；`outcome_unknown` 仍需按原 ID 查询并人工核对，不能解锁新写动作。旧 v1 客户端不兼容 v2。

HTTP 超时、`outcome_unknown`、查询不可用或 `not_found` 都不是重新执行的许可。先用原 ID 查询；不得换 ID 重试。broker 的未知结果阻塞需要人工核对，不能自行清空日志、重置 journal 或调用人工 reconciliation 绕过。人工对齐后旧动作仍可能保持 unknown，其审计记录不表示原动作成功。

用户暂停后停止写动作，直到用户明确恢复；重连或 `ready=true` 不撤销暂停。未获用户授权不得安装/删除 Mod、操作存档、修改游戏文件、使用开发控制台或作弊。启动、安装、清理及人工恢复流程见仓库 README；这些动作不由本 Skill 自动授权。
