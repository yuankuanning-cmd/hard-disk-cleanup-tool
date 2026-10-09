# v2.0.0 发布核验

日期：2026-10-08；目标：Windows x64、WPF、自包含单文件。

- 发布：`dotnet publish src/ChatGPTCleanupTool/ChatGPTCleanupTool.csproj -c Release -r win-x64 --self-contained true -o outputs`，成功。
- 完整回归：`dotnet run --project tests/ChatGPTCleanupTool.TestHarness -c Release -- --million --test-recycle`，成功。
- 602 个实际测试文件第二次核对：最后一轮 287 ms，未变占用元数据读取 0 次；新增/修改 2 条、删除 1 条；指纹复用、快照、暂停/继续、正常停止、新服务实例恢复及哈希阶段崩溃恢复均通过。
- 百万条合成 SQLite 记录：打开 25 ms，分页/多次搜索/排序/树图/WPF 明细完整流程 10.38 s；实际加载 1,032 个节点，新增托管内存约 2.1 MiB。此测试验证索引/界面有界内存，不代表百万个真实磁盘文件的首次扫描时间或应用总内存；实际子串搜索仍需扫描数据库。
- 硬链接、目录/文件回收前变化保护、系统/排除保护、真实临时文件和整个目录 Windows Shell 回收站数量增量，均通过。
- USN：模拟日志无变化、同大小同时间修改、删除、失效回退、V2 字节解析通过；本机普通账户实际卷查询返回拒绝访问，未验证真实管理员权限卷增量。
- 从实际单文件 EXE 运行 `--verify-scan-engine`：退出码 0，验证内置原生 SQLite 内存数据库和嵌入完整许可文本，无用户文件操作。
- 常规隐藏启动：标题“磁盘空间助手”，InputIdle=True、Responding=True；只停止本次测试启动的进程。
- 主界面/空间树 WPF 渲染和新按钮编译核对通过，预览位于 `outputs/scan-engine-preview.png`，内容来自隔离测试夹具。

最终已部署：`D:\SoftWare\DiskCleanupAssistant.exe`，旧 v1.1.0 在同名路径被覆盖替换，没有删除用户索引、排除项、日志或设置。

- 文件版本：2.0.0.0
- 大小：66,147,593 字节
- SHA-256：`32E1DADB28DC2729663E069AB4C50F5DBBFEDCC2A7CD311AFFB2D6C7123FB845`
- 已核对部署文件与 `outputs/DiskCleanupAssistant.exe` 完全一致。
