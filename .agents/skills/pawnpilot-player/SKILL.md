---
name: pawnpilot-player
description: 使用 PawnPilot MCP 观察《深空当铺：可能是偷的》并执行已支持的原生关闭菜单动作。适用于已启动且已获授权的本机游戏桥接；不提供自主经营或交易策略。
---

# PawnPilot M2-C / M2-D（PARTIAL）

仅使用 `health_check`、`observe`、`act`、`get_action_status`、`wait_for_update`。当前唯一正式写动作是原生 `close_menu`，新业务动作与购买仍被生产门禁关闭。`wait_for_update` 是有界只读等待。`act.schema_version` 必须等于同次观察的 `state.schema_version`（2 或 3），不能自行升版或混用会话。

开始时先 `health_check`，再 `observe`。只有 broker/bridge 已连接、协议兼容、世界 `ready=true`、`can_act=true` 且 `legal_actions` 包含该动作时才可提交。`close_menu` 仅用于明确授权的诊断，不能作为自主经营遇到人工暂停的恢复策略。失联快照属于历史数据；`loaded=false` 的现金占位值无效。

v3 `business.details` 必须结合 completeness/status/reason 解释。容器 incomplete/unavailable/unimplemented 的空 item_refs 不能解释成库存为空；未知名称、形状或报价金额不能推测补全。quote 行是已渲染候选，未绑定时 item_ref 为 null、offer_ref 为 null。collection_fingerprint 只是当前完整集合候选身份，不代表报价/容量/经济执行已合法。`candidates` 与 `legal_actions` 不同，purchase_enabled=false 或任一 Q/C/V/UI 未验证时不得购买。不要拼接多个查询的不同帧，冒充同一决策快照。

每个动作生成唯一 `command_id` 并保留，原样传入刚观察到的 `broker_epoch`、`world_epoch`、`revision`、`version`（对应 `expected_*` 参数）。每次只提交一个动作，然后查询同一 ID 的状态及 `before/after`。菜单必须通过正常游戏入口打开；MCP 不提供打开菜单操作。

`pending` 表示未交付；`running` 表示已交付、可能已执行；`succeeded` 才表示已验证后置条件。`rejected` 表示未通过门禁。`failed` 不应被当作“肯定没有副作用”；先核对证据。遇到旧版本或状态变化，重新观察。

需要短暂等待时，`wait_for_update` 仅支持 `revision`、`command_terminal`、`operable` 三种条件，最长 30 秒；`max_wait_ms=0` 执行一次最多 50ms 的即时检查。超时可继续观察或重新等待。取消等待不会取消已提交命令；`outcome_unknown` 仍需按原 ID 查询并人工核对，不能解锁新写动作。旧 v1 客户端不兼容 v2。

HTTP 超时、`outcome_unknown`、查询不可用或 `not_found` 都不是重新执行的许可。先用原 ID 查询；不得换 ID 重试。broker 的未知结果阻塞需要人工核对，不能自行清空日志、重置 journal 或调用人工 reconciliation 绕过。人工对齐后旧动作仍可能保持 unknown，其审计记录不表示原动作成功。

用户暂停后停止写动作，直到用户明确恢复；重连或 `ready=true` 不撤销暂停。未获用户授权不得安装/删除 Mod、操作存档、修改游戏文件、使用开发控制台或作弊。启动、安装、清理及人工恢复流程见仓库 README；这些动作不由本 Skill 自动授权。
