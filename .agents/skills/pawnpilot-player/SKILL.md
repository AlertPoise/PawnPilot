---
name: pawnpilot-player
description: 使用 PawnPilot MCP 观察《深空当铺：可能是偷的》，按当前 legal_actions 执行已授权的普通经营动作。
---

# PawnPilot M2-C / M2-D 基础普通买入

仅使用 `health_check`、`observe`、`act`、`get_action_status`、`wait_for_update`。动作按本次快照的精确 `legal_actions` 独立开放，包含已接入的开门、接待、普通对话、商品准备、正常购买及结束交互；注册名称或文档描述本身不代表当前可执行。`wait_for_update` 是有界只读等待。`act.schema_version` 必须等于同次观察的 `state.schema_version`（2 或 3），不能自行升版或混用会话。

开始时先 `health_check`，再 `observe`。只有 broker/bridge 已连接、协议兼容、世界 `ready=true`、`can_act=true` 且 `legal_actions` 包含该动作时才可提交。`close_menu` 仅用于明确授权的诊断，不能作为自主经营遇到人工暂停的恢复策略。失联快照属于历史数据；`loaded=false` 的现金占位值无效。

v3 `business.details` 必须结合 completeness/status/reason 解释。容器 incomplete/unavailable/unimplemented 的空 item_refs 不能解释成库存为空；未知名称、形状或报价金额不能推测补全。quote行来自当前渲染文本，单行item_ref可为null而整单绑定仍完整；普通displayed_total与wholesale_total必须区分。purchase item_refs/collection_fingerprint绑定实际非owned待购子集，完整back可同时包含owned物品；front_collection_fingerprint独立绑定prepare来源。指纹存在本身不授权购买。`candidates` 与 `legal_actions` 不同，购买要求 purchase_enabled=true、当前正常报价完整、容量 sufficient、金额不超过用户范围和现有现金，并精确匹配 legal_actions；开门、接待和对话只检查各自相关条件，不依赖报价或库存完整。不要拼接多个查询的不同帧，冒充同一决策快照。

本轮已实机完成普通拾荒客scavGeneral的一次正常购买，普通价444、现金550→106、13件入库并自有、顾客离场。报价和商品由观察决定，不可复用这些示例值。普通购买/准备/对话另支持shadyPharma，但尚未单独实机成交。复杂非空子窗口、未知接收条件、批发、议价、卖出、特殊业务、日结均不支持。cancel_confirmation和独立refuse_trade不可调用；需要正常结束时，仅使用当前legal_actions中的dismiss_customer或等待离场。

读取当前已显示对话和选项文字，不读取未来台词或隐藏客户信息。自动播放中的对话优先等待并重新observe；只有确需跳过当前打字或处于manual-wait且对应动作合法时提交advance_dialogue。选项只能从当前可见、可交互且有文本的option_ref选择；不循环盲点。手动advance/choose尚缺成功实机样本。

act参数为顶层schema_version、command_id、action、arguments、broker_epoch、expected_world_epoch、expected_revision、expected_version，不套request对象；wait_for_update参数才放在request内。revision等待同时携带broker/world/revision身份，command_terminal按原命令ID等待。每次写操作前立即重新observe，不能把wait/status中的较旧快照直接当作最新决策。

每个动作生成唯一 `command_id` 并保留，原样传入刚观察到的 `broker_epoch`、`world_epoch`、`revision`、`version`（对应 `expected_*` 参数）。每次只提交一个动作，然后查询同一 ID 的状态及 `before/after`。菜单必须通过正常游戏入口打开；MCP 不提供打开菜单操作。

`pending` 表示未交付；`running` 表示已交付、可能已执行；`succeeded` 才表示已验证后置条件。`rejected` 表示未通过门禁。`failed` 不应被当作“肯定没有副作用”；先核对证据。遇到旧版本或状态变化，重新观察。

需要短暂等待时，`wait_for_update` 仅支持 `revision`、`command_terminal`、`operable` 三种条件，最长 30 秒；`max_wait_ms=0` 执行一次最多 50ms 的即时检查。超时可继续观察或重新等待。取消等待不会取消已提交命令；`outcome_unknown` 仍需按原 ID 查询并人工核对，不能解锁新写动作。旧 v1 客户端不兼容 v2。

HTTP 超时、`outcome_unknown`、查询不可用或 `not_found` 都不是重新执行的许可。先用原 ID 查询；不得换 ID 重试。broker 的未知结果阻塞需要人工核对，不能自行清空日志、重置 journal 或调用人工 reconciliation 绕过。人工对齐后旧动作仍可能保持 unknown，其审计记录不表示原动作成功。

用户暂停后停止写动作，直到用户明确恢复；重连或 `ready=true` 不撤销暂停。未获用户授权不得安装/删除 Mod、操作存档、修改游戏文件、使用开发控制台或作弊。启动、安装、清理及人工恢复流程见仓库 README；这些动作不由本 Skill 自动授权。
