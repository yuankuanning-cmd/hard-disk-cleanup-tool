# v2.4 软件占用与卸载残留验证

日期：2026-10-08。Windows x64、.NET 10 WPF，自包含压缩单EXE。不新增第三方包，不捆绑BCUninstaller；使用Windows系统接口，运行时才生成使用者本地软件历史与审计。

## 功能及限制

新增第9页软件占用：桌面登记、当前用户Store、Steam多库/Epic清单、默认快速报告大小与可选逻辑/实际分配实测、未登记/缺少入口与历史疑似残留、模拟计划、UAC新还原点前置、串行交互卸载、执行意图/返回码/失败审计、筛选搜索CSV设置。残留永远只提示，没有删除目录或注册表的入口。共享/受保护组件不能批量卸载。

残留关联要求两次完整来源扫描缺失，并且只检查常见顶层目录/键的名字关联；不是穷尽检测，首次没有历史不能证明旧AppData无用。Steam接管后停止队列，Epic仅分析，其他平台/Windows可选功能不宣称完整支持。还原点新建受系统保护、管理员权限、同账户提权与系统频率限制；失败立即停止，不绕过24小时限制，不把已有点当新点。还原点不替代个人数据备份，厂商卸载不能保证保留配置或存档。

## 本轮测试

- 软件专项隔离后端：本地临时安装目录逻辑/实际分配、默认不选、系统/框架/残留保护、安全命令解析、脚本宿主与包名注入拒绝、MSI交互参数、Steam多库清单/越界/身份校验、EXE变化/升级/实时排除阻止旧计划、部分测量与取消。
- 模拟不调用还原点/卸载；还原点失败或无新序号不启动卸载。假卸载串行、退出后复核登记；启动器登记仍在时停止后续队列。意图、还原点和结果持久审计；残留文件保持不变。**还原点和卸载均使用可注入假后端，不卸载用户软件，不对本机创建真实还原点。**
- 真实系统仅做只读清单验证：桌面软件115、当前用户商店应用150、游戏0、完整来源7，无读取警告。首次查询2.991秒，版本字符串修正后复测2.318秒；不是大型目录实际测量的耗时。此机器无Steam/Epic游戏，平台解析使用隔离真实格式清单测试，不冒充实际安装平台验证。
- WPF独立/主窗口编译渲染、默认快速模式、搜索/分类/缺少入口筛选、证据与虚拟化表格；旧页索引不变。界面测试截图包含的是夹具数据，不是用户软件清单。
- 完整旧功能回归：通用清理、真实空间树、重复中心临时真实NTFS/回收、相似媒体真实MP4/MP3解码、开发环境临时Git工作树、SQLite增量/快照/暂停检查点与百万行按需加载。实际普通账户USN仍拒绝访问，模拟与回退验证不等于真实提权USN验证。

命令：完整 `dotnet run --project tests/ChatGPTCleanupTool.TestHarness -c Release -- --million --test-recycle`；专项 `--software-only`；真实只读 `--software-inventory-only`。

完整回归退出0；软件专项最后复测0.768秒并通过WPF验证。开发环境隔离专项41.17秒。百万行空间索引打开18ms，分页/搜索/排序/树图/WPF全流程4.08秒，实际加载1,032个节点，托管内存增长2.1MiB；这是既有空间页性能，不代表软件安装目录实测速度。测试夹具完成后正常清理。

发布核验：版本2.4.0.0，66,282,971字节。SHA-256：`F214D6B3CE15952C5044838A728C5C23E977EBAD1AC4AF857623E8C47855710E`。outputs与D:\SoftWare两份一致，D:\SoftWare中本软件只保留这一个同名EXE。

四项发布EXE打包诊断（scan/media/development/software）退出码全部0，D:\SoftWare最终副本的software诊断复测也是0。普通隐藏启动进入输入空闲且Responding=true；只结束本轮新建的启动探测进程，未强制结束使用者旧版窗口。还原点助手以缺失请求文件做非提权拒绝测试，按预期退出1，没有调用系统还原。

首次发布已成功生成新版，但D:\SoftWare旧版正在运行导致覆盖失败。使用者确认自行关闭后，用原生文件复制同名替换，并重新核对哈希/版本/诊断；没有强行关闭旧版、没有遗留旧版副本，也没有删改其他软件、用户项目或清空规则/历史。git diff --check仍报告README两处既有Markdown硬换行双空格，未将已有改动认领为本轮代码问题。

## 依据

[BCUninstaller项目](https://github.com/BCUninstaller/Bulk-Crap-Uninstaller)只作为功能组织参考，独立实现不复制代码。[Windows卸载登记](https://learn.microsoft.com/en-us/windows/win32/msi/uninstall-registry-key)、[Get-AppxPackage](https://learn.microsoft.com/en-us/powershell/module/appx/get-appxpackage?view=windowsserver2025-ps)、[Remove-AppxPackage](https://learn.microsoft.com/en-us/powershell/module/appx/remove-appxpackage?view=windowsserver2025-ps)、[Checkpoint-Computer](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.management/checkpoint-computer?view=powershell-5.1)、[Get-ComputerRestorePoint](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.management/get-computerrestorepoint?view=powershell-5.1)。
