# M1 实机验证范围

此计划在测试包构建、自动化测试及主 Agent 审查后申请一次批准；M0 的批准不替代本轮批准。

1. 将保存目录 `%USERPROFILE%\AppData\LocalLow\Questing Goose Studio\Probably Stolen` 的现有普通文件复制到项目 `artifacts/m1/backup/before`，记录原有 `save_*.es3` 哈希。备份只保留本机，不读取、上传保存内容。
2. 仅安装 `artifacts/package/pawnpilot_m0` 的 DLL、manifest 和本机 `artifacts/session.json` 到 `E:\SteamLibrary\steamapps\common\Probably Stolen\Mods\pawnpilot_m0`。使用 `scripts/install-mod.ps1`，安装收据保留在忽略目录。已有同名目录或非预期文件会拒绝操作。
3. 使用 Computer Use 技能操作 Steam 和 Probably Stolen 窗口：启动、正常 Mod 激活和 network 权限确认、必要重启、新建独立空槽、加载该测试槽、ESC 菜单、返回主菜单和正常退出。GUI 用于正常激活许可和画面核对；CLI 无法提供这些证据。
4. 新开局直接核对 `loaded/ready`、现金和画面；不通过返回菜单重载来掩盖新开局问题。随后正常加载测试槽，返回菜单验证失效，再次加载并验证旧世界版本拒绝。只使用本次创建的测试存档，不加载或覆盖原存档。
5. 运行独立 broker 和 MCP Adapter，通过真实 Codex CLI 客户端发现四个工具，调用 `health_check`、`observe`，在正常打开的 ESC 菜单中执行单次 `act(close_menu)`，再用 `get_action_status` 查询验证结果。另验证 broker 无在途重启后的连接和既有结果查询；不重放旧动作。
6. 游戏正常操作会更新激活配置、信任记录、日志、保存索引和测试存档；Steam 可能同步 Cloud。出现未知结果、版本不符或非预期游戏变化，停止写动作、查询原命令并保留证据，不用新 ID 重试。
7. 完成后备份测试槽，经 UI 删除本次测试槽，禁用本次 Mod，正常退出游戏。按安装收据逐文件移除本次 Mod；停止服务并核对端口释放、原有存档哈希。不用备份覆盖全局索引或真实进度。信任历史等游戏正常写入如仍保留，会在报告说明。

真实游戏 PASS 必须有日志、HTTP/MCP 结果和界面核对。故障注入的协议夹具测试单独记录，不能替代 Unity 或真实 Codex 客户端验证。

## 执行记录

2026-10-31 用户明确回复“批准上述范围，使用独立测试存档”。已按范围执行，测试槽为 save_2.es3；新建直接就绪、真实 Codex MCP 单次关闭菜单、broker 重启结果查询、测试槽重载及旧版本拒绝均通过。测试槽已删除、Mod 已停用并按收据移除，游戏和 broker 已退出；3 个原有存档文件哈希相同，端口 18765 已释放。详见 [M1 报告](m1-implementation.md)与[证据摘要](m1-evidence.json)。
