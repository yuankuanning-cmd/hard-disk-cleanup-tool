# 磁盘空间助手开发交接

更新时间：2026-10-17

## 最新增量：彩色工作台、容量概览与常用目录（v3.0.0）

- `Themes/ModernTheme.xaml`由App合并：统一颜色资源、圆角控件、表格和状态色标；`NavigationShell/NavigationItem`提供深蓝侧栏。主导航旧0..9索引保持不变，新概览为10，`NavigationPanel`按ZIndex将概览视觉置顶；不要为导航排序改动旧索引依赖。所有页保留原业务入口。
- `Controls/VisualPrimitives.cs`包含轻量原生`DiskGauge`和冻结状态画刷转换器。无第三方UI/图标依赖，无每行动画或阴影；数据表行列虚拟化、正常字重。状态文字与颜色同时出现，不只靠颜色传递风险。
- `Models/StorageOverview.cs`、`Services/StorageOverviewService.cs`只在后台读取本地固定盘容量，不枚举文件、不查网络/移动盘、不运行清理命令；80%/90%提示仅为空间压力，不是磁盘健康。`Controls/OverviewCenter.xaml(.cs)`展示容量卡、环图、快捷入口、最多12个目录收藏和已有快速清理候选合计，未扫描显示“未扫描”。候选值不是可释放量。
- `MainWindow.Overview.cs`集成刷新、导航、收藏持久化与CSV。收藏只存归一化本地绝对路径、忽略大小写去重，加载不探测存在性；明确点击分析才调用`AnalyzeSpaceAsync(root)`，与已有子目录重扫一致，指定路径不套用全局白名单但保留原件/排除。大文件入口只设1024MiB阈值并跳页，不自动扫盘。
- `AppSettings.InterfaceVersion/FavoriteScanPaths`兼容旧偏好；3.0首次打开概览，后续保持选择。`Window_Loaded`移除自动快速扫描，异步读取容量；关闭取消读取，不等待无界卷查询。未加载的测试窗口关闭不写用户设置，书签测试禁用通知并恢复原值，避免夹具污染真实设置。
- `Controls/GridCheckBehavior.cs`仅处理可编辑的`DataGridCheckBoxColumn`单击：走BeginEdit与原绑定，不枚举源、改删除资格或绕过IsEnabled/IsReadOnly。NPS模板内复选框仍走原有实现；测试使用直接路由事件时在grid上RaiseEvent并把Source设为cell，模拟WPF的直接预览事件转发。
- 顶栏白名单折叠，TextChanged同步显示启用范围数；修正Codex筛选栏固定窄列导致文字裁切、TextBox模板重复Padding导致30px路径文本裁切，保留全局模拟与保护规则。`ResultContextMenu`增添容量/收藏对象映射，现11页/23个对象区域；收藏导出路径列表，概览导出卷容量。
- 新测试`OverviewTests`覆盖容量/压力、取消、收藏与安全偏好持久化、导航索引、单击/禁用/只读、冻结状态色、11页呈现与菜单。`--overview-only`只运行该组；完整`--million --test-recycle`包含新组。预览容量为夹具，截图带示例说明，不作本机空间报告。
- 新增打包诊断`--verify-overview-engine`，只检控件、主题资源与模拟容量，不扫描用户盘。版本3.0.0.0，单文件同名更新`D:\SoftWare\DiskCleanupAssistant.exe`，保留其他软件与用户数据；实际测试和发布哈希见`RELEASE-3.0-VERIFICATION.md`。

## 历史增量：NPS多依据识别与整工程回收（v2.8.0）

- 使用者再次明确：回收整个模型工程，不能仅以npsdata为依据。保留单表自动大小与直接勾选，取消孤立模型的单文件删除资格，主列表只展示Recognized完整工程（恢复快照另列）。候选与模型线索默认隐藏，分类可查看，只读。
- `NpsProjectRecognizer.InspectRoot`：本层有效XML清单+model目录/至少两种配套目录；无清单则model+至少两种配套目录+已核对模型schema/NPS专用脚本标记。仅文件后缀、单清单、目录名或备份名不足。多个清单/损坏/权限异常失败关闭。
- `ReadStructure`随model/script/loadflow/fpga_bin目录线索触发，在现有全盘遍历期间加入目录标志，不另开全盘遍历。候选目录去重；model数据库最多核对本层16项，script最多查看64个目录项、读取8个文本文件的前16,384字符，支持py/json/xml/txt，关键词集在源码明确。读取不执行脚本，不跟随reparse/云占位；有XML已验证时不额外读脚本/模型库。
- SQLite原markers表model值0=清单、1=模型线索、2=目录结构，record增加可选Structure字段，保持旧清单JSON可载入。目录与文件标志归并后按最近工程根分配ModelFiles，目录项数而非npsdata文件数决定展示行。追加枚举StructuredProject，不改变旧枚举数值；识别描述写入Evidence及CSV。
- `NpsProjectService.MeasureAsync`仍先统计目录占用；孤立文件有大小但不可回收。工程必须新鲜InspectRoot.Confirmed，且记录清单与当前根本层清单一致；无清单工程不伪造ManifestHash。执行时再次检查组合证据+Git+快照。删除入口仅接受可信WholeProject，移除旧EnsureModelSafe单文件授权逻辑。
- `DevelopmentManifest.HasNestedNpsProject`保留其他清单检测，并核对快照中的嵌套model/script目录是否构成另一个独立结构工程，防止父工程吞掉无清单子工程。系统/安装/用户根、排除/参考、Git、离线/链接、工具恢复快照保护不变。
- 一次确认明确“完整文件夹内模型、脚本、结果、编译产物、备份等全部内容”，不输入文字、不加100项上限。文件夹外的共享资源不追删。全局模拟、STA强制回收、持久审计和取消保持。
- `NpsProjectTests.MultiSourceAsync`新增无npsdata清单工程、最小清单工程、无清单无npsdata的脚本结构工程、schema+结构工程、多模型归并、完整大小、普通布局/共享下载目录误判、独立线索、无清单嵌套保护及识别依据变化。原生回收仅使用随机临时工程，验证整目录而非仅数据库；UI仍测单击、筛选、1201项勾选与菜单。
- 本机原样例路径当前只剩`.simunps-mcp`，不能沿用10月13日21个普通/备份工程或3.3秒的数据。`--nps-sample`改为检查现存的有效清单，不要求旧HN目录存在。测试与发布哈希记录到`RELEASE-2.8-VERIFICATION.md`。版本2.8.0.0，仍单文件同名更新D:\SoftWare，不删除其他软件。

## 历史增量：NPS单表直接勾选清理（v2.7.0，单文件删除已替换）

- 使用者明确要求简化为“每项大小 → 勾选删除”。`NpsCenter`移除PlanGrid、测量按钮、双表计划；ProjectGrid首列为模板内交互CheckBox，可单击而无需进入DataGrid编辑模式。自动大小降序、全选当前筛选结果/取消全部、选中项数和大小汇总；一次普通Yes/No确认（默认No），删除不再要求输入文字。
- `MainWindow.Nps.cs`把发现和全量大小统计作为一个后台操作；打开历史清单也自动更新大小，保存大小字段JsonInclude，不复用过期清理资格。取消保留已测大小，取消项有清楚文字且不可勾选。删除继续遵守全局模拟模式。
- `NpsProjectItem.Cleanup`是仅运行时的绑定，不序列化删除计划。目录测量先于资格判断：恢复/嵌套/Git等受保护项也显示大小，不再虚假显示0 B；不完整大小标“部分”。独立模型采用ModelFile目标，仅回收单个npsdata，绝不推断父文件夹归属。未知模型格式仍明确标示，文件删除由用户选择，不再禁止所有独立模型。
- `MeasureAsync`/`RecycleAsync`删除100项限制，快照单项目条目上限改为int.MaxValue；NPS菜单勾选、全选和删除允许超过通用1000行阈值。发现20k结果/100k标志/512深度上限不变。守卫按ProjectId字典查找，批量绑定用字典，勾选汇总仅更新一次，避免O(n²)。重新扫描或打开历史时替换旧快照，避免本地索引无限追加。
- `DevelopmentManifest.Scan`可选NPS测量模式，SQLite逐项事务，遇访问失败保留已读大小并标不完整。Scan/Validate均采用深度有界枚举器栈，不堆积全部同层文件；原开发环境默认模式保持失败关闭，工作树的共享.git目录不纳入清理快照。NPS使用`GetFileStorageMetadata`的FILE_READ_ATTRIBUTES句柄读取分配/身份，不打开文件内容；扫描根只核对一次祖先，遍历按条目检查reparse，执行前仍完整核对祖先和身份；进度250ms节流。
- 目录仍保留本层清单/标准结构、嵌套工程、Git、系统/安装/整个用户根、排除/参考、reparse/cloud、恢复快照保护；单文件不套用整个ProgramFiles目录限制，但仍禁止Windows/System/保护硬链接/数据库sidecar/恢复项。执行前自动Validate，STA强制回收，记录意图和结果，绝不永久删除或清空回收站。
- NPS右键菜单直接勾选/取消、全选、模拟与删除，无手动测量入口；CSV增加删除范围和状态；统一菜单现覆盖10页/21个对象区域。测试包含121项真实临时文件大小+模拟、1201项UI批选和大选择菜单、实际呈现的CheckBox自动化Toggle和两向绑定、临时工程及单模型真实回收（同目录keep.txt保留）。未删除用户工程。
- 版本2.7.0.0，仍同名替换 `D:\SoftWare\DiskCleanupAssistant.exe`，保留用户规则/设置/历史。最终验证、样例大小统计耗时和发布SHA256见 `RELEASE-2.7-VERIFICATION.md`。

## 历史增量：NPS工程发现与整个旧工程回收（v2.6.0，旧流程已替换）

- 使用者明确优先“回收已备份的整个旧工程”，不是自动清理编译产物。第10页 `Controls/NpsCenter.xaml(.cs)`、`MainWindow.Nps.cs`：全盘/指定范围、可移动盘只发现、分类隐藏恢复快照、工程搜索、选中测量、独立勾选计划、模拟、备份关闭NPS输入确认、持久记录/重开清单、CSV/设置/关闭等待。旧9页序号不变；统一菜单扩至10页/22个对象区域。
- `NpsProjectRecognizer.cs`基于实际三份project.emtproj样例：XML Project/Version/ProjectConfig或LoadFlowConfig，禁DTD/XML外部解析、1MiB上限；npsdata头和model/block表只读核对。WAL/SHM/journal存在或WAL格式不打开SQLite，仅候选提示，防辅助文件写入。文件后缀/未知格式也保留疑似，不冒充所有NPS版本已验证。
- `NpsProjectService.DiscoverAsync`目录枚举使用缓存文件属性，只为标志读取内容，不对整盘全部文件读分配/哈希；最多2路SSD多范围、HDD/未知顺序，BlockingCollection256单SQLite写者/128事务批次。DFS枚举器栈，不保存所有普通路径，包含隐藏目录，不跟随reparse/云离线/恢复项。回收站/卷管理/权限/512深度/100k标志/20k项目限制和取消均报告。可访问范围结束不等于不可访问路径也扫描成功；不宣称USN增量。
- `NpsProjectCatalog.cs`SQLite FULL审计+WAL，匹配标志、最近结果、500条最近记录。根按清单所在目录，模型按最近上级工程归属；无归属模型作为单文件只读，绝不把其父目录当工程删除。备份名仅分类，`.simunps-mcp`恢复目录始终只读；默认隐藏可切换查看。
- 完整测量/校验复用 `DevelopmentManifest`（新增Forget/HasOtherNpsManifest，不改旧Scan接口）：清单需本层、结构model+至少两种配套目录、逐文件身份/长度/时间/硬链接、完整性和嵌套检查。每根最新计划取代旧计划，原件/排除/系统/根/安装/整个用户目录/Git/链接/云/恢复目录拒绝。回收要求输入备份关闭确认、实时进程检测和清单SHA-256/结构/快照校验，串行STA强制回收、意图先记，不强杀、不清空回收站。UI勾选通知只在UI线程发出，后台仅改变资格标志。
- 模型/脚本/结果是用户工程内容，年龄不授权删除，备份由用户自行建立；程序不能验证备份内容完整或所有改名NPS进程。快照元数据不是对整个工程每个文件做内容哈希，不承诺所有并发竞态都被消除；仍需保存并关闭工具。
- `NpsProjectTests.cs`隔离格式/深层隐藏/范围重叠/备份/恢复/独立/未知/DTD/WAL/嵌套/Git/伪造外部清单/实时保护/修改新增/取消上限/持久化与真正临时工程回收。真实样例只读测试 `--nps-sample`索引放临时测试根，不读认证/不改样例；`--nps-only --test-recycle`专项；全量 `--million --test-recycle`含旧回归、十页菜单和NPS渲染。
- 样例只读发现141目录：17普通、4备份、120恢复快照，9,045文件，约1.2秒、跳过0；没有给用户做工程回收。新诊断 `--verify-nps-engine`只检打包控件/SQLite，不扫描用户工程。版本2.6.0.0，发布同名替换D:\SoftWare，哈希见RELEASE-2.6-VERIFICATION.md。

## 历史增量：全页右键菜单（v2.5.0）

- `Controls/ResultContextMenu.cs`：结果对象显式映射，无反射遍历；所有者级指针命中、保留多选、空白处清空菜单目标、菜单键/Shift+F10、Ctrl+C与弹出菜单内复制、公共位置/属性/复制/详情。快照只读SelectedItems，1000行上限、20项详情、1MiB复制限制；不展开百万ItemsSource或计算哈希。忙碌/资格在点击时再检查，防打开后变忙的旧入口。系统属性调用Unicode SHObjectProperties，缺失路径点击时提示，不在菜单打开时探测大量磁盘。
- `MainWindow.ContextMenus.cs`：9页页面背景及19个结果/列表/树图/审计区域绑定。每页刷新/导出；明确区分高亮行与勾选计划。清理/回收/链接/Git/卸载调用原有入口，不引入新的删除实现。记录导出实际记录而非分析列表；公式前缀进行转义。原件/排除同步、软件和开发资格更新。
- 空间页取消旧鼠标专用处理，树/表/树图共用菜单风格与键盘；当前节点下钻、子目录重扫、排除及强制回收保留，历史/陈旧/保护项禁用。原件解除仍由原件页确认；菜单移除排除项增加确认。
- 重复中心新增有界所选副本计划与保留当前副本方法，组内至少留一份、参考/排除强制保留。媒体新增临时组筛选，恢复或重扫退出；异步预览更新显式Dispatcher，避免无同步上下文/快速切换时跨线程访问。
- `ExclusionService.SetExcludedPaths`批量归一/一次持久化；所有排除保存改为flush临时文件+同目录替换，失败还原内存计划，不影响已有永久排除与参考根分工。保留旧单路径API。
- `ContextMenuTests.cs`加入软件专项及完整STA回归，检查全页覆盖、真实鼠标事件路由、键盘菜单键、正确右键主项/多选/空白防误用、禁用项/忙后点击门禁、1000行上限及大选择仍可导出、百万虚拟源禁止枚举、重复保留与参考、媒体组筛选及截图。测试不执行新菜单的真实清理/卸载/还原/Clipboard写入；旧原生回收和Git动作仅在临时夹具内。
- 版本2.5.0.0，增加 `--verify-context-menus` 单EXE诊断，验证公共菜单与shell32属性导出但不创建主窗口、不读用户登记、不打开属性/提权。发布仍同名覆盖 `D:\SoftWare\DiskCleanupAssistant.exe`，具体哈希与验证见 `RELEASE-2.5-VERIFICATION.md`。

## 历史增量：软件占用与卸载残留（v2.4.0）

- 第9页 `Controls/SoftwareCenter.xaml(.cs)` 与 `MainWindow.Software.cs`：默认只读扫描、所有结果不选择、快速登记大小排序、可选实测、筛选/搜索/CSV/设置、证据与持久审计。旧8页序号不变。全局模拟也禁止单独创建真实还原点。
- `Models/SoftwareAnalysis.cs`：软件来源/类型、报告值与逻辑/实际分配分离、读全标志、不可变登记、安全计划、还原点与审计模型；inventory/restore/uninstaller 可注入测试后端。不能把报告大小或登记消失当卷释放量。
- `WindowsSoftwareInventory.cs`：32/64位 HKLM/HKCU 只读卸载登记（无 Win32_Product），当前用户 Appx、Steam 多库 VDF/ACF、Epic默认清单。共享系统组件禁止批量卸载，命令不支持时提示Windows设置。Steam清单身份/路径/链接/4MiB/3000项上限检查；Epic只分析。注册表不写入。
- `SoftwareCatalog.cs`：本地SQLite FULL审计，只有完整来源累计历史缺失，两次缺失后关联疑似残留。常见 AppData/ProgramData/Program Files 和 Software 顶层精确归一名启发式，排除共享厂商，首次无历史不能证明旧残留；结果永远只提示，不存在目录或注册表残留删除API。历史/路径/命令是个人信息；UI只读最多300条审计，历史关联最多3000条，数据库本身不自动淘汰。
- `SoftwareAnalysisService.cs`：深测流式 DFS，1m全局条目、250k单项物理身份、同根复用，逻辑/实际分配及部分原因。快速扫描不枚举内容；新页不是USN/百万软件分页。卸载先校验新鲜不可变登记、可用入口、EXE身份/长度/时间与实时保护；持久执行意图后串行启动，完成后重读登记，未确认/取消/Steam接管停止队列。厂商卸载实际释放不精准归因。停止等待从不强杀厂商/系统进程。
- `SoftwareWindowsCommands.cs`：绝对Windows PowerShell、静态编码命令；Store包名验证且只卸当前用户；MSI按GUID构造 /x /norestart；本地固定卷EXE用CommandLineToArgvW/参数数组，不猜未加引号的空格路径，不支持任意shell/script/rundll32宿主。不追加静默参数，但厂商登记原有参数仍由厂商处理。Steam协议交接，Epic无卸载入口。
- `SoftwareRestorePointService.cs`：单EXE的 `--software-create-restore-point <request>` 提权助手，只接受本用户固定目录/随机nonce/10分钟内/同SID请求；另一账号凭据提权不支持。静态 Checkpoint-Computer 后比对新描述/序号，不更改系统保护或绕过24h限制。新点失败禁止卸载；还原点不能代替个人文件备份。测试宿主不能真实提权，真实助手未对用户系统创建还原点做破坏性验证。
- 测试 `SoftwareTests.cs`：隔离模拟来源/卸载/还原点，临时目录实测，命令解析/注入拒绝、Appx保护、Steam多库与越界/身份、实时排除/版本/EXE变化、来源不完整不累加历史、还原点失败/无序号停止、串行/子进程未确认停止、审计、部分上限、取消及WPF主/独立页面。`--software-inventory-only`只读真实系统登记；`--software-only`专项；完整旧功能回归仍用 `--million --test-recycle`，真实回收/硬链接/Git动作只在隔离夹具内。
- 版本2.4.0.0，打包诊断增加 `--verify-software-engine`，只检验页面、SQLite与嵌入许可，不读用户软件登记或提权。发布为 `outputs\DiskCleanupAssistant.exe` 和 `D:\SoftWare\DiskCleanupAssistant.exe`，同名覆盖旧版，不删其他软件，保留用户规则/索引/审计。验证与哈希见 `RELEASE-2.4-VERIFICATION.md`。

## 历史增量：开发环境中心（v2.3.0）

- 新页面为第8页，保留旧页序号：`Controls/DevelopmentCenter.xaml(.cs)`；主窗口集成在 `MainWindow.Development.cs`。搜索、CSV、保护目录、设置、全局历史与关闭取消等待均已接入。用户级缓存/Docker 默认不勾选，所有结果默认不选择。
- `Models/DevelopmentAnalysis.cs`：安全分类、归属、文件占用、理论回收量、清理资格与逐项审计。未知/未提交/引用中不进入普通回收计划；已归档图片/可视化另有输入备份确认的人工入口，仍强制验证归档/引用/版本库状态。
- `DevelopmentGit.cs` / `DevelopmentProcess.cs`：无 shell、参数数组、有输出/时间上限、可取消；Git 只读检查关闭 fsmonitor 和 optional locks。工作树操作不用 force/prune/删分支；通过主工作树协调正常 remove，避免 Windows 当前目录占用。主、锁定、子模块、忽略内容、未合并、任务引用与 Codex 托管关系都阻止移除。
- `DevelopmentCodexCatalog.cs`：只读 SQLite/字段兼容检查；确切任务编号路径匹配、附件托管关系、活动/固定任务 cwd 与引用检测。流式 Aho-Corasick 多路径匹配，不保存消息正文；10,000任务/附件、单记录32MiB、合计128MiB上限，缺失/变化/超限失败关闭。数据库版本/附件格式不是稳定公开 API，未来升级要保持失败关闭，不默认为没有任务。
- `DevelopmentManifest.cs`：当前扫描文件快照 SQLite、逐项事务、身份/时间/大小/硬链接变化和新增/删除验证；包/构建内嵌版本库和云占位保持关闭。硬链接参考/排除别名在执行前重新核对。完整 SHA-256跨同一Git仓库工作树输出比对，受控10,000文件/2GiB，不依赖名字证明重复。
- 常见默认缓存路径覆盖 NuGet/npm/pnpm/Yarn/pip/Cargo/Gradle/Maven；私人包/源码/共享 store 不自动清理。Docker du + history只读，项目来源与共享缓存字节分开，不能可靠映射时归属未知；没有捆绑 Git/Docker/包管理器。
- `development-manifest.sqlite`每次替换当前快照内容，不是开发中心USN增量扫描；`development-operations.sqlite`执行意图/结果持久审计。回收站操作实际释放0；Git移除不冒充卷实测释放。老Codex年龄评分已取消安全删除授权、默认临时/缓存选择改为false；旧快速清理API保留，任务感知只在新中心完成。
- `DevelopmentTests.cs`覆盖隔离真实Git仓库/工作树、脏/已跟踪/合并状态、归档/重新启用/其他任务引用、未知私人缓存、跨树完整哈希、变化快照、实时保护、部分上限、执行记录、原生回收/Git移除、人工归档确认与WPF渲染。所有Git写入/删除仅在新建测试夹具内；测试清理只在验证的随机临时根内解除Git只读对象属性。
- 发布仍为 `outputs\DiskCleanupAssistant.exe` 与 `D:\SoftWare\DiskCleanupAssistant.exe`，版本2.3.0.0；同名覆盖对应旧版，不删除其他软件。新诊断参数 `--verify-development-engine` 只验证打包页面与原生SQLite，不扫描用户数据。

## 历史增量：相似媒体（v2.2.0）

- `Models/MediaAnalysis.cs`：媒体指纹/来源/结果、选项与部分完成标志；相似结果与完全重复模型严格分开，不存在删除队列。
- `MediaAnalysisService.cs`：按目录有界枚举、白名单及额外参考目录、排除/重解析点/云占位/恢复文件跳过；SSD最多2路图片、HDD/未知顺序、音视频独立单路信号量。最多50,000媒体，取消保留已完成结果；缓存失败继续分析，文件解码前后采样身份签名再次确认。分段汉明多索引筛候选（四段中至少一段的距离不大于完整阈值/4，枚举位翻转掩码后验证完整指纹），避免高维度量树在大量不同图片中退化全树遍历；固定锚点分组防相似传递链，视频至少4/5帧且需画面变化；声音序列对比。不能宣称所有页面已改为百万分页或媒体USN更新。
- `MediaVisualFingerprint.cs`：WIC长边256解码、透明白底合成、3中心区域×8二面体变换pHash、颜色/纹理限制；视频均匀黑边裁去（最多每边15%）；EXIF已支持标签、拍摄日期/常见隐私存在性、同字段组。检查不覆盖完整XMP/人脸/私有元数据，不能保证任意水印编辑或原图识别。
- `WindowsMediaDecoder.cs`：独立Windows Media Foundation SourceReader互操作。COM方法槽位根据Microsoft官方SDK头文件核对；MFStartup/Shutdown及CoInitializeEx计数平衡，样本/连续缓冲区/锁均finally释放；RGB32缩采样五个时间点，PCM16混声道/11025Hz线性重采样前120秒。默认Windows编码支持，格式失败逐项提示。首版没有外部FFmpeg进程或第三方媒体分发依赖。
- `MediaAudioFingerprint.cs`：4096点FFT、32频段相邻能量符号、0.25秒hop，序列汉明匹配及±10秒偏移。8秒以上且非静音/非单调的录音才归组，不保证同曲不同演奏、调速/变调/后半段不同的录音。
- `MediaFingerprintCache.cs`：独立SQLite WAL/NORMAL、禁用连接池、算法版本、身份/长度/时间/首尾采样签名；仅保存成功指纹/元数据标志，损坏条目重新计算；可清空/VACUUM/截断WAL。缓存签名不是安全删除验证，文件路径/日期有隐私性。
- `Controls/MediaCenter.xaml(.cs)`：七号“相似媒体”标签（旧页索引保持不变）、来源/类型/候选/隐私/EXIF筛选、双图异步限量预览取消、建议与保护、失败明细、缓存清空、位置/路径、全局搜索。`MainWindow`增加扫描/关闭等待、CSV导出与设置持久化，复用现有参考规则；不添加媒体删除或EXIF改写功能。
- `MediaAnalysisTests.cs`：真实图片缩放/JPEG压缩/90°旋转/镜像/边缘水印、EXIF日期/作者/GPS/同字段但不同内容；PCM音量、不同音乐/静音、缓存失效/清理、取消/部分结果、范围/保护、文件只读、双图异步预览/渲染。测试用FFmpeg仅造真实MP4/MP3（本机已测），不复制到发行包。`--media-only`或完整 `--million --test-recycle`。
- 发行版本2.2.0.0；继续同名替换 `outputs/DiskCleanupAssistant.exe` 与 `D:\SoftWare\DiskCleanupAssistant.exe`，保留使用者所有规则/历史/索引；发布实测与SHA256见 `RELEASE-2.2-VERIFICATION.md`。

## 历史增量：重复文件中心（v2.1.0）

- `Controls/DuplicateCenter.xaml(.cs)`：比较/选择、原件保护、逐项记录三个视图；完整组过滤，5 种保留策略，目录批选，异步限量预览，CSV 日志导出。主窗口持久化策略/筛选，处理后禁止继续使用过期扫描，关闭等待正在执行的重复操作。
- `Services/ReferenceDirectoryService.cs`：原子保存独立保护规则，无法读取或规则消失时 fail closed。目录会加入重复扫描范围；`ExclusionService` 的非持久参考根同步到其他清理页，不覆盖原有永久排除。解除保护必须在参考目录页操作。
- `DuplicateDecisionService.cs`：原件/排除优先，组内至少一份，backup/工作名称启发式，WPF 图片元数据。完全相同图片的分辨率和质量相同，质量按钮是无损/像素/位深的指标策略，不是感知相似图片/AI 质量评分。
- `DuplicateFileService.cs`：先筛同大小再读占用/物理身份，按物理实体去重后哈希，参考路径优先代表；完整/采样缓存包含身份，不使用缓存执行清理。保留旧 DeleteToRecycleBinAsync API 但转到新执行器；不再使用可能回退永久删除的 VisualBasic 回收接口。
- `DuplicateMutationService.cs`：STA Shell 回收；锁定目标及保留副本，重验双方完整 SHA-256/身份/时间。目标不允许其他读写句柄，避免正在被使用时换名。参考别名通过 FindFirstFileNameW 再检查，参考原件不作硬链接来源。硬链接只在同一本地 NTFS 固定卷，拒绝 ADS/EFS/Offline；审计意图 → 恢复链接 → 新链接 → FileRenameInfoEx(REPLACE|POSIX) 原子换名 → 审计中间状态 → 删除恢复链接 → 旧实体无文件名/关闭句柄核算占用。普通 MoveFileEx 替换不能在旧目标校验句柄仍开着时正常工作，已通过真实测试改用安全换名；不使用会合并 ACL/ADS 的 ReplaceFile。
- 执行失败尝试回滚，任何不确定性保留 `.diskcleanup-backup-*.tmp` 及审计路径；不做自动恢复/自动擦除。系统断电情况下文件系统日志与 SQLite 不是同一个事务，必须看中间审计并手动核对，不宣称跨文件系统事务原子性。
- `DuplicateOperationLogService.cs`：SQLite WAL + synchronous FULL，修改之前必须写审计。逐项记录失败原因、保留/恢复路径、理论及核算释放字节，UI 上限 500。无法建立审计时不修改文件。
- 回收站释放量为 0；理论和硬链接旧实体分配占用与卷可用空间观测分别展示，卷变化存在外部干扰，不精准归因。已有硬链接的占用保守计算；临时恢复链接会在成功后清理。
- `DuplicatePreviewService.cs`：最多 8 项、256px 最长边、图片 200MiB 文件上限、文本 4KiB；跨线程冻结图像，显式 Dispatcher 更新；不执行外部内容。
- 顺带修正快速清理模拟时修剪空目录的问题；真实修剪也跳过参考/排除目录及其祖先。`CleanupFilesAsync` 新可选 protectedPaths 参数保留旧调用兼容。
- `RecoveryFileProtection.cs`：`.diskcleanup-*` 命名空间在快速/Codex/空间回收/空目录清理中受保护，目录整体回收也逐项检查，防止异常恢复链接被另一个清理器处理。
- `DuplicateCenterTests.cs`：真实 NTFS 替换、两种故障注入/回滚及恢复路径、原件/别名/损坏规则/跨页保护、五策略、批选、采样碰撞、双方重哈希、ADS/打开句柄拒绝、持久日志、取消、图片及限量预览，WPF 分组筛选/搜索/主窗口渲染。必须跑 `--million --test-recycle` 回归。
- 单文件版本 2.1.0.0；发布核验见 `RELEASE-2.1-VERIFICATION.md`。位置继续为 `outputs/DiskCleanupAssistant.exe` 与 `D:\SoftWare\DiskCleanupAssistant.exe`；同名替换旧版本，保留用户规则、日志、索引。

## 历史增量：扫描引擎 2.0（v2.0.0，取代下文 v1.1 内存空间索引）

- `Services/IndexedScanEngine.cs`：空间分析的持久化流水线；有界候选/结果通道各 512，单 SQLite 写者分批提交，目录队列落盘。枚举和元数据阶段共享硬盘操作信号量；哈希仅对同大小候选且可选启用。
- `Services/StorageDevicePolicy.cs`：Windows StorageDeviceSeekPenaltyProperty 识别 SSD/HDD，自动 SSD 最多 4 路，机械/未知/混合 1 路；手动并行仍有硬上限。
- `Services/SqliteScanIndex.cs`：SQLite WAL、外键级联删除、目录工作队列、每目录完整提交后才清理未见条目；未变文件轻量更新 `seen`。硬链接按稳定路径归属，目录汇总/扩展名预汇总与可比增长基线保存到数据库。每范围只保留当前文件索引与上一轮目录基线，不保存文件内容。
- `Services/UsnJournalService.cs`：读取现有 USN、V2 严格解析、OpenFileById 解析目录；游标以扫描前头为准，恢复检查点继续保留原扫描头。权限不足/非 NTFS/日志重建轮转/不支持记录/解析失败/20 万条变化或 1 万个变化目录上限回退全量核对，不静默遗漏。
- `Services/ScanSessionControl.cs`：协作暂停屏障，暂停确认后无索引/元数据操作在进行；部分聚合与只读查看安全。正常退出等待取消扫描保存部分结果，崩溃后未完成目录重新枚举，已完成目录的变化由旧 USN 游标下一轮重放。
- `Services/IndexedNodeSource.cs`：SQLite 主数据源，按 128 条分页/每列表 8 页缓存，目录缓存上限 512；WPF 使用 IList 而非全量 IEnumerable materialization。搜索/排序服务端，树图限量查询；快照使用 SQLite BackupDatabase，历史视图与不完整扫描不可清理。
- `MainWindow.Engine.cs`：引擎模式、可选指纹、暂停/继续、停止并保留、快照保存/打开/上次结果/检查点继续、流式 CSV、嵌入组件许可；模式持久化至旧设置文件的新可选字段。
- `SpaceRecycleService`：索引结果流式逐项校验，不再构造全子树 Dictionary；同时验证文件身份。回收成功后旧索引标过期并禁止继续清理，需要重扫，不把陈旧硬链接/回收站统计伪装为当前占用。
- 依赖 `Microsoft.Data.Sqlite 10.0.12` / `SQLitePCLRaw 2.1.12`，原生库与许可文本嵌入单 EXE。不采用 SQLitePCLRaw 2.1.11（已知 SQLite 安全公告）。
- 新工作索引 `%LOCALAPPDATA%\DiskCleanupAssistant\space\indices-v2`；旧 JSON 基线保留，不自动删除用户数据。首次新引擎扫描需建立新基线；多个范围会占用多份索引，尚未自动淘汰索引，不要宣称索引零占用或所有页面已改为 SQLite。
- `ScanEngineTests.cs` 覆盖 602 个真实文件的元数据/哈希复用、变更删除、快照、暂停/继续、停止及新服务恢复、HDD/SSD 策略、模拟 USN 无变化/同时间修改/删除/失效和字节解析、百万行索引 WPF 分页。此机器普通账户真实 USN 返回“拒绝访问”；真实提权卷扫描尚未验证，后备与模拟路径已验证。
- 发布位置：`outputs/DiskCleanupAssistant.exe` 和 `D:\SoftWare\DiskCleanupAssistant.exe`；发布时核对 SHA-256、2.0.0.0 版本、隐藏启动响应与回归结果。下文 v1.1 章节属于历史实现，关于无 USN 和内存树的描述不再代表空间分析现状。

## 历史增量：真实空间树（v1.1.0）

- `Models/DiskTreeNode.cs`：完整目录/文件索引，文件只保存名称和父节点，目录保存完整路径；字符串按需格式化。目录一次后序汇总，包含空目录和阈值以下文件。
- `Services/DiskAnalysisService.cs`：一次枚举建立树，重叠白名单去重，保留盘符根分隔符；排除目录停止遍历，其余条目用哈希查找排除项，不跟随路径祖先或条目中的重解析点。精确模式只为硬链接维护身份索引，占用稳定归属路径排序首名称，避免扫描顺序变化制造假增长；失败占用不伪装为精确值。
- `Controls/DiskTreemap.cs`：独立实现加权二分矩形布局，按扩展名稳定配色；单个绘制控件而非每文件一个控件，层级和色块数量受限，密集小项以合并块表示。
- `Controls/SpaceExplorer.xaml(.cs)`：虚拟化完整目录树、树图、当前目录明细、增长排行、大文件与排除管理；下钻/返回/上级/总览，右键操作。树图不截断底层索引。
- `Services/DiskSnapshotService.cs`：同根目录、排除范围和模式隔离的目录快照，原子替换文件；部分结果不覆盖其旧基线，不参与增长排行。保存目录而非完整文件内容，最多保留最近 16 种范围基线。
- `Services/SpaceRecycleService.cs`：回收前整棵子树验证，保护根目录/系统路径/排除项/变化项；STA Windows Shell `IFileOperation`，显式 `FOFX_RECYCLEONDELETE`，仅本地固定磁盘。
- `MainWindow.Space.cs`：操作确认、排除跨页同步、回收后移除原路径索引并标记缓存统计过期（共享硬链接及回收站新内容必须重扫才能准确确认）。回收日志按“尚未释放空间”记录，释放量为 0，不自动清空回收站。
- 子目录重扫会切换分析视图，原输入和白名单不变；“开始分析”恢复原范围扫描。尚未实现原树局部拼接/USN 增量索引，不要声称已实现本地 MFT 加速。
- 测试新增真实 NTFS 硬链接、目录层级/空目录、小文件、范围重叠、扩展名统计、增长/新增目录、模式隔离、部分结果、变化与排除保护、10 万节点矩形布局、WPF 渲染导航筛选。加 `-- --test-recycle` 时使用临时夹具验证 Shell 回收站数量增量。
- 最新发布目标仍为 `D:\SoftWare\DiskCleanupAssistant.exe`；`outputs` 中界面预览是临时测试夹具，不是用户文件扫描结果。

## 1. 项目定位

本项目是一个 Windows 原生 WPF 磁盘清理与空间分析工具，目标用户偏向普通 Windows 用户和开发者。核心原则是：

- 先扫描、预览和解释，再允许清理。
- 默认只选择可以重新生成的低风险文件。
- 高风险内容必须由用户主动选择并再次确认。
- 不跟随符号链接、目录联接点或其他重解析点。
- 清理前重新验证路径、文件大小和修改时间，避免扫描后文件被替换。

当前程序名为“磁盘空间助手”，发布程序集名为 `DiskCleanupAssistant`。

## 2. 当前完成状态

### 第一阶段

- Codex 临时文件、缓存、可视化、图片、历史会话和项目副本扫描。
- Windows 用户临时文件、崩溃转储、错误报告、DirectX 着色器及缩略图缓存。
- NuGet、pip、npm、Yarn 缓存。
- 开发项目中的 `.vs`、`bin`、`obj`、`TestResults`、`dist`、`.next`、Python 缓存和 `node_modules`。
- 文件级明细、永久排除列表、精确清理计划和 JSON Lines 清理日志。
- 任意磁盘或目录的大文件、一级目录占用分析。

### 第二阶段

- 文件逻辑大小与磁盘实际分配空间分别统计。
- 识别 NTFS 压缩、稀疏文件和硬链接。
- 全局实际占用汇总中，同一硬链接实体只计算一次。
- 重复文件三级确认：文件大小、首尾分段 SHA-256、完整 SHA-256。
- 同一物理文件的硬链接不会被当作可释放的重复副本。
- 重复文件默认全部不选；每组必须至少保留一个文件。
- 删除前重新验证完整哈希、大小和修改时间，然后送入 Windows 回收站。
- Docker 引擎占用和可回收空间检测。
- Docker 安全清理使用 `docker system prune --force`，不添加 `--volumes`。
- Docker 与 WSL VHDX 的逻辑大小和宿主机实际占用检测。
- WSL 当前只做分析，不直接修改 Linux 文件系统或压缩 VHDX。

## 3. 主要文件

| 文件 | 职责 |
|---|---|
| `src/ChatGPTCleanupTool/MainWindow.xaml` | 四个页签的 WPF 界面：快速清理、空间分析、重复文件、Docker/WSL |
| `src/ChatGPTCleanupTool/MainWindow.xaml.cs` | 界面事件、异步任务、确认框和集合绑定；当前仍是 code-behind 结构 |
| `src/ChatGPTCleanupTool/Services/CleanupService.cs` | 通用清理规则、扫描、路径授权、精确清理计划和文件变化复核 |
| `src/ChatGPTCleanupTool/Services/DiskAnalysisService.cs` | 大文件、一级目录、逻辑大小与实际分配空间分析 |
| `src/ChatGPTCleanupTool/Services/DuplicateFileService.cs` | 重复文件三级哈希分析与回收站删除 |
| `src/ChatGPTCleanupTool/Services/WindowsFileStorageService.cs` | 通过 Win32 文件句柄读取 `AllocationSize`、文件标识和硬链接数 |
| `src/ChatGPTCleanupTool/Services/ExternalStorageService.cs` | Docker CLI、Docker VHDX 和 WSL 分发版 VHDX 检测 |
| `src/ChatGPTCleanupTool/Services/ExclusionService.cs` | 永久排除项持久化 |
| `src/ChatGPTCleanupTool/Services/CleanupLogService.cs` | 清理历史日志 |
| `src/ChatGPTCleanupTool/Models/` | 界面绑定和服务结果模型 |
| `tests/ChatGPTCleanupTool.TestHarness/Program.cs` | 随机隔离目录中的回归测试入口 |
| `build.cmd` | 自包含、单文件、Windows x64 发布脚本 |

## 4. 本地数据位置

应用数据默认保存在：

```text
%LOCALAPPDATA%\DiskCleanupAssistant\
```

- `exclusions.json`：永久排除路径。
- `cleanup-history.jsonl`：清理历史。

不要在升级时无提示改变这些文件的含义或自动删除它们。

## 4.1 本次会话新增（Codex 智能建议）

- 支持“Codex 智能建议”页新增更强实用能力：
  - 新增可选“扫描后识别重复文件（按哈希）”；
  - 列表新增“扩展名/所在目录/重复组/重复数”用于快速聚类判断；
  - 新增“保留”复选框（与全局永久排除共用存储），被标记后不会进入待删队列；
  - 新增“一键选中高优先(55+)”按钮，便于先清理分值高的候选；
  - “清理依据”保留了可理解规则（未访问时长、目录特征、文件特征、体积）。
- 运行结果默认仍保留“可清理候选”口径，未打勾“保留”的高置信条目仍可直接勾选删除。
- 本次调试通过 `dotnet build` 与 `dotnet publish -c Release -r win-x64`，生成 exe 位于：
  - `src/ChatGPTCleanupTool/bin/Release/net10.0-windows/win-x64/publish/DiskCleanupAssistant.exe`。

## 4.2 本次会话新增（Codex 智能建议增强）

- 在 Codex 智能建议页新增按钮：`重复组保留最新，选其余`。
- 按钮会按 `重复组` 分组，把每组可删除成员中“最后访问/修改时间”最新的文件保留，不勾选；其他同组文件一键勾选，适合快速处理大量重复文件。
- 删除前新增保护：当用户误将某重复组全部勾选时，系统会自动取消勾选该组最新文件，强制保留每组至少 1 个物理副本。
- 重复组内的自动保护与确认提示会在删除确认文本中提示，避免误删。
- 当前发布包已更新：
  - `outputs\\DiskCleanupAssistant.exe`
  - SHA-256：`AE947AC769F8815412C9EA954A59878BE62B05368886039DB0CEC5C17DE8B485`（2026-08-29 初版构建）

## 4.3 本次会话新增（Codex 自动化清理动作）

- 在“Codex 智能建议”页新增：
  - `选中可重建目录`：按目录提示（`.tmp`、`cache`、`generated_images`、`visualizations`、`archived_sessions`）和“建议清理”口径，快速勾选可疑可重建产物。
  - `选中超高优先(80+)`：只勾选推荐分值 >= 80 的项，适合先清理高确定性条目。
- 这两个动作均会跳过被标记“保留”的条目，保留重复组删除时仍沿用“每组至少保留 1 个”的安全保护。
- 最新一次发布已更新：
  - `outputs\\DiskCleanupAssistant.exe`
  - SHA-256：`0922F8F0258EC14C201545D84AC164D0AAA5B838E0E2ACFA61864985D27D92F4`（2026-08-29）

## 4.4 本次会话新增（性能优先模式）

- 新增顶部全局勾选框：`加速模式`。
- `空间分析`、`重复文件`、`Codex 智能建议` 三类扫描都支持该开关。
- 性能优化点：
  - 开启后跳过 Windows 文件系统元数据查询（分配大小/硬链接/文件身份），以逻辑大小作为近似实际占用。
  - 重复文件扫描跳过除“采样哈希”外的哈希全量阶段，扫描速度显著提升。
  - 重复文件删除在快速模式下会先按组重新做完整哈希分组复核，避免误删，仍保留每组至少保留 1 个文件的安全规则。
- UI 文案同步区分：
  - 提示文本会显示当前为“快速模式（近似）”或“完整扫描”；
  - 重复文件扫描中会显示“快速模式下同组删除前会重验完整哈希”。
- 当前行为约束（重要）：
  - 快速模式下“实际占用/可回收”仅是近似值；
  - 重复文件哈希结论为“采样阶段”结论，删除前会再次校验。

## 4.5 本次会话新增（扫描白名单与 Dry-Run，最终交付）

- 统一接入“扫描范围白名单”到快速清理、空间分析、重复文件和 Codex 智能扫描入口，支持“多个目录按行/分号”输入。
- 新增“模拟清理（Dry-Run）”全局开关，所有清理动作支持不实际删除的预演：快速清理、重复文件删除、Codex 智能删除都会走统一预演提示与结果汇总。
- 新增构建说明补充：发布产物为单文件自包含 exe，分享时仅需分发 `outputs\DiskCleanupAssistant.exe`。
- 当前发布结果（本次提交）：
  - `outputs\DiskCleanupAssistant.exe`
  - 大小：`64877120` 字节
  - SHA-256：`0D08B42CEA0C50FA6B0AF527D40823DD2B4E52B1ACD57A3211D0D172B4E0BF66`

## 4.6 十步综合优化（2026-09-22）

本轮把此前未执行的十步请求合并完成：

1. 扫描进度：快速清理、空间分析、重复文件、Codex 智能扫描会显示阶段、当前路径、文件数、目录数和读取量；进度回调带节流，不会反过来拖慢扫描。
2. 扫描性能：主要扫描器使用单次文件系统枚举直接读取属性，减少逐条重复系统调用；界面结果改为批量集合重置，避免上万条结果逐行通知造成冻结。
3. 增量哈希：新增持久化采样/完整 SHA-256 缓存，键由路径、大小和修改时间共同校验，文件变化后自动失效；缓存限制为 20 万条并淘汰 120 天未使用项。
4. 结果检索：顶部统一检索框可筛选当前各页的名称、路径、类型、状态、建议和历史记录。
5. 设置记忆：自动保存加速/模拟模式、各扫描路径和阈值、白名单、Codex 重复检测和最后选中页。
6. 报告导出：按当前页面导出带 UTF-8 BOM 的 CSV，中文 Excel 可直接打开；包含快速清理明细、Codex、空间、重复文件、Docker/WSL 和历史页。
7. 历史可视化：新增“清理历史”页；快速清理、重复文件、Codex 和 Docker 操作统一入库，模拟操作也会标识。
8. 安全复核：快速清理确认框新增类别、风险、文件数和空间明细；实际执行高风险类别时要求第二次确认。
9. 界面改造：统一输入框、表格、页签、按钮和颜色样式；表格启用斑马纹与行列虚拟化；顶部整合搜索、导出和模式开关。
10. 验证发布：新增进度、哈希缓存、缓存失效、设置、CSV 和历史测试；Release 构建与隔离回归测试通过，单文件启动验证成功。

当时交付（2026-09-22）：

- `D:\SoftWare\DiskCleanupAssistant.exe`
- 大小：`64893008` 字节
- SHA-256：`802D4CB1148CD8E8D8F0FEBD09A3CC8AFF939ADCA68DD12AD45460B9799F2585`

## 5. 构建与测试

项目目标框架为 `net10.0-windows`，WPF，自包含 `win-x64`，单文件发布。

构建：

```cmd
build.cmd
```

脚本优先使用：

```text
%LOCALAPPDATA%\Codex\dotnet-sdk-10\dotnet.exe
```

测试：

```powershell
$dotnet = Join-Path $env:LOCALAPPDATA 'Codex\dotnet-sdk-10\dotnet.exe'
& $dotnet run --project tests\ChatGPTCleanupTool.TestHarness\ChatGPTCleanupTool.TestHarness.csproj -c Release -r win-x64
```

最近一次验证结果：

- Release 编译：0 个警告、0 个错误。
- 隔离回归测试通过。
- WPF 单文件 EXE 启动成功，窗口标题为“磁盘空间助手”。
- 最新输出：`outputs\DiskCleanupAssistant.exe`。
- 最新发布位置：`D:\SoftWare\DiskCleanupAssistant.exe`，版本 `1.1.0.0`（2026-10-08），对应旧版 EXE 已覆盖替换。
- 最近一次发布大小：64,919,700 字节。
- 最近一次 SHA-256：`F80497BE849F82E998CAD4C3BC0C1380A5A287DBA3855017647C8227E4F97C3A`。
- 本轮完整回归通过；10 万索引节点布局约 4.4 ms、351 色块（仅内存布局测试，不代表扫描速度）；真实文件与目录 Shell 回收操作通过。单文件启动验证 `Responding=true`、输入就绪、标题“磁盘空间助手”。

若发现 `outputs\ChatGPTCleanupTool.exe`，它是旧版命名，不是当前推荐版本。

## 6. 安全不变量

后续开发不应破坏以下规则：

1. 不递归进入重解析点。
2. 不允许未授权的任意路径删除；清理计划来自已知规则、重复扫描或用户在完整空间树中明确选择并确认的项目。
3. 快速清理在执行前验证路径、大小和修改时间。
4. 重复文件删除在执行前重新计算完整 SHA-256。
5. 重复文件每组至少保留一个不同物理实体。
6. 重复文件使用回收站，不默认永久删除。
7. Docker 清理默认不处理卷。
8. 不直接删除或编辑 WSL `ext4.vhdx` 内部文件。
9. 不直接处理 WinSxS、Windows Update 组件、注册表或 Prefetch。
10. 所有高风险类别默认不勾选。

## 7. 当前已知限制

- `MainWindow.xaml.cs` 已较大，尚未迁移到 MVVM；继续增加页面前建议拆分 ViewModel 和命令。
- 完整模式仍需逐文件读取 Windows 分配大小/文件标识；整盘精确扫描会明显慢于加速模式。
- 重复文件第一次完整扫描仍需读取候选内容；后续扫描可复用哈希缓存，但尚未建立 USN Journal 级别的磁盘索引。
- 已增加真实 NTFS 硬链接测试；稀疏文件和压缩文件仍需专项自动化测试。
- Docker/WSL 外部集成未做可重复的模拟测试，主要依赖实际环境手动验证。
- Docker CLI 必须已安装且引擎正在运行，清理按钮才可用。
- WSL 仅显示宿主机 VHDX 占用，尚未读取每个分发版内部 `df` 数据，也未实现受控压缩向导。
- Docker 引擎报告的 Size 是 Docker 管理口径；宿主机物理占用应以 Docker VHDX 的“实际占用”行为准。
- 历史页目前展示操作汇总；尚未提供单次操作的文件级展开、趋势图和一键撤销。

## 8. 建议的下一步

推荐按以下顺序继续：

1. 补充稀疏文件、NTFS 压缩文件测试。
2. 将主窗口拆分为 MVVM 服务和页面级 ViewModel。
3. 增加 WSL 内部空间读取，但避免自动启动或修改分发版；压缩功能应做成独立高风险向导。
4. 为 Docker 提供镜像、容器、构建缓存的分项清理，而不是只有统一 prune。
5. 历史页增加文件级详情、空间变化趋势和按操作类型筛选。
6. 对超大目录引入 NTFS USN Journal 增量索引，同时保持普通路径扫描作为兼容后备。

## 9. 工作区与版本控制状态

当前仓库只有初始提交，现有开发内容仍表现为修改文件和未跟踪文件。交接时不要执行 `git reset --hard`、`git clean` 或覆盖式还原；这些文件就是当前有效实现，应先检查并按用户要求决定是否整理提交。

发布目录已被 `.gitignore` 忽略。若旧版 EXE 正在运行，直接覆盖对应文件会导致发布失败；当前新版改用 `DiskCleanupAssistant.exe`，不要为了发布而强制终止用户正在运行的清理程序。
