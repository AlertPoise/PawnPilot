# M0 实机授权与验收步骤

测试包位于项目内 `artifacts/package/pawnpilot_m0`。用户于 2026-10-31 明确批准安装、Computer Use、network 权限确认及独立测试存档；本计划已执行，结果见 [最终报告](m0-feasibility.md)。安装目录和测试槽已清理。权限要求直接来自本次用户任务第五节，不是技能额外施加的确认。

## 操作范围与可恢复性

1. 仅向 `E:\SteamLibrary\steamapps\common\Probably Stolen\Mods\pawnpilot_m0` 新建目录并复制构建出的 DLL、manifest、会话配置。不替换已有目录或其他 Mod；如目标存在则先停下核查。
2. 使用 Computer Use 操作 Steam 与 Probably Stolen：启动、Mods 菜单正常激活、阅读 `network` 权限提示、必要重启、核对画面、ESC 菜单及正常退出。普通 CLI/日志不能替代游戏内激活许可和“现金/菜单与界面一致”的验收证据。
3. 不创建 `dev.txt`、不启用 Mod 开发信任模式、不改游戏二进制、不绕过安全扫描。优先用正常激活结果和 Player.log 记录扫描/加载结果。
4. 激活会由游戏写入 Mod 信任与配置。启动游戏会写日志、设置，Steam 可能同步 Cloud；如授权测试存档，仅通过新建空槽位建立单独测试进度，绝不覆盖现有槽位。无法确认空槽时暂停存档操作。
5. 运行前将现有保存目录的普通文件复制备份到项目 `artifacts/backup/` 并记录哈希，不读取或上传存档内容。测试后禁用 Mod，退出游戏，删除本次新增的 `Mods\pawnpilot_m0`。只有明确确认属于本次新建的测试槽才通过游戏 UI 删除；不得用备份覆盖真实进度。保留备份供用户处理，避免与 Steam Cloud 冲突。
6. 只开关本机 Python 服务，停止后检查端口释放。配置/审批历史如被游戏保留，明确记录，不承诺安装后游戏目录以外完全没有变化。

## 按顺序验证

| 实验 | 操作与证据 | 停止条件 |
|---|---|---|
| 加载 | 正常激活后重启；日志出现 INIT/ENABLED，记录 build、版本、DLL hash 与权限提示 | 被扫描拒绝、初始化失败或版本不一致 |
| 主菜单通信 | Python GET /state 显示 loaded=false；observe 单次请求/响应 | token/协议不匹配或未连接 |
| 状态 | 单独测试槽进入店铺；日志 GAME_LOADED；核对 phase、cash 与 UI | 无隔离测试槽授权或 UI 不一致 |
| 动作 | GUI 正常打开 ESC；读取新状态；close_menu；核对 before/after、真实窗口关闭和原生 Resume 行为 | 前置不成立、结果 uncertain、非预期经营变化 |
| 生命周期 | 停 Python/重启/退出游戏，检查断线、恢复要求、SHUTDOWN 和端口释放 | 未确认结果不得重发动作 |

只把带真实日志/HTTP记录/UI核对的项目记为 PASS；纯托管夹具、IL 核查与编译只记部分完成。

实测修正：当前 `OnGameLoadedLate` 只在正常载入已有槽时触发；新建测试槽后应返回主菜单，再载入这个新槽，不能载入用户原有进度。本次测试为自动分配的 `save_1.es3`，第 1 天、550 现金。
