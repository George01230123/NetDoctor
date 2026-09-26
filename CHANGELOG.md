# 更新日志

本项目遵循 [语义化版本](https://semver.org/lang/zh-CN/)。
版本号格式：`主版本.次版本.修订号`。

---

## [1.4.0] - 2026-09-26

### 新增

- **原生接口库 `NetDoctorNative.dll`（x86）** —— 给 32 位易语言宿主 `LoadLibrary` 调用
  - 用 **NativeAOT** 编译为真正的原生 DLL，**宿主无需安装 .NET 运行时**
  - 15 个 `__stdcall` 导出函数，统一返回 **UTF-8 JSON**，内存由 DLL 自管
  - 与主程序**共用同一份 `src/Core` 源码**，不复制代码
  - 新增 `native-test` —— x86 调用测试宿主，用 `LoadLibrary` + `GetProcAddress` +
    stdcall 委托完全模拟易语言调用方式，**32 项断言全部通过**
  - 导出：`ND_Ping` `ND_Version` `ND_DiagNetwork` `ND_FixNetwork` `ND_FixDeep`
    `ND_DnsBenchmark` `ND_DnsRestore` `ND_CleanScan` `ND_CleanRun` `ND_Hardware`
    `ND_OptList` `ND_OptApply` `ND_Services` `ND_Startup` `ND_Free`
- 新增文档 [`docs/原生接口.md`](docs/原生接口.md) —— DLL 接口设计文档
- 新增文档 [`docs/易语言接入.md`](docs/易语言接入.md) —— 调用示例与避坑清单
- README 增加 5 张界面预览截图

### 改进

- **Core 层彻底移除 WinForms 依赖**，使主程序与原生 DLL 能共用同一份引擎代码
  - `NetworkRepair.DeepFix` 的重启询问改为**回调注入**：界面层传 `MessageBox` 回调，
    原生场景传 `null` 即不弹窗、不自动重启
  - 抽出 `src/Core/Interop.cs` 集中管理 P/Invoke 声明
- `publish.ps1` 支持同时发布主程序与原生 DLL，并自动运行原生调用测试
- `publish.ps1` 新增 `-Target` / `-NoDeploy` / `-SkipNative` / `-SkipTest` 参数
- CI 增加原生 DLL 构建与测试步骤，两个产物一并附加到 Release

### 修复

- **NativeAOT 下 `JsonSerializer.Serialize` 直接崩溃宿主**
  （反射被裁剪，抛 `JsonSerializerIsReflectionDisabled`）——
  改为手写 JSON 转义，中文直接输出、控制字符转 `\uXXXX`
- **`EmbeddedScripts` 定位宿主目录不可靠** —— 原来用 `AppContext.BaseDirectory`，
  在 NativeAOT 与被其它程序托管时会指向错误位置，导致日志与快照丢失；
  改用 `GetModuleFileNameW(GetModuleHandleW(null))` 取宿主进程可执行文件的真实目录
- **`publish.ps1` 在默认目标目录不存在时会报错退出** ——
  从源码 clone 的人没有 `E:\挂\xiyanruoxue`，脚本会失败；现在自动降级为只产出到 `publish\`

---

## [1.3.0] - 2026-09-26

### 新增

- **硬件检测页**
  - 硬件信息：数据全部来自 WMI/CIM + 注册表，本机采集零上传。
    覆盖系统/主板/BIOS/UUID、CPU（核心线程缓存负载）、内存
    （DDR 代际自动识别 + **单双通道判断**）、显卡（**显存**）、
    硬盘（**SMART 健康度/温度/通电小时/磨损度**）、分区、
    显示器（**EDID 面板型号/生产年月/物理尺寸**）、网卡、声卡、电池、温度传感器
  - 工具启动器：扫描本机已安装的图吧工具箱 `tools` 目录，
    按分类列出其中的原版工具并直接启动，**不复制不修改**任何第三方程序
  - 性能测试：CPU 跑分（SHA256 单/多线程吞吐 + 浮点，给出多线程加速比）、
    磁盘顺序读写测速（临时文件自动删除）
  - 屏幕测试：全屏纯色查坏点/漏光、灰阶渐变查色带、三级网格查几何失真与摩尔纹、
    多字号文字清晰度测试
- 新增 `docs/使用说明.md` 完整中文手册

### 修复

- **显卡显存读取失败** —— `MatchingDeviceId` 含 `SUBSYS` 段导致 `EndsWith` 匹配不上，
  改为按 `VEN_/DEV_` 正则前缀匹配（WMI 的 `AdapterRAM` 是 32 位，8 GB 显卡会被截断成 4 GB）
- **内嵌脚本未随单文件发布** —— 单文件发布下不能依赖"随包内容文件"，
  改为编译进程序集、首次运行释放到宿主目录旁的 `.runtime\`
- **两个内嵌脚本互相覆盖** —— 资源名 `NetDoctor.Data.sysitems.ps1` 用 `Split('.')` 取末段
  只能得到 `"ps1"`，改为取末两段
- **DataGridView 自带滚动条在深色主题下渲染异常**（出现白块）—— 改为自管 `VScrollBar`
- **工具启动器列宽被压成看不清** —— `AutoSizeColumnsMode.Fill` 按总宽压缩中文列，
  改为固定宽度并按比例重排

---

## [1.2.0] - 2026-09-26

### 新增

- **系统工具页**，五个子标签
  - 垃圾清理：24 个清理目标，先**实测占用**再决定是否清理；
    回收站与 .NET NativeImages 缓存默认不勾选
  - 应用管理：枚举全部 Appx 应用并显示**真实占用**，按大小倒序，批量卸载后逐个复核
  - 系统服务：读 `Start` 注册表值显示**真实启动类型**，内置 20 个可安全禁用的服务推荐
  - 启动项：注册表 Run/RunOnce + 启动文件夹 + 计划任务，
    禁用使用 Windows 官方 **StartupApproved** 机制，**不改动原始注册表项**
  - 系统激活：不内置脚本，只搜索调用用户本机已有的 `MAS_AIO_CN.cmd`

### 修复

- 进度条在任务结束后未清零，会留下一条实心色块

---

## [1.1.0] - 2026-09-26

### 新增

- **Windows 优化页**：151 项优化，7 个分类
  （性能 45 / 隐私 34 / 外观与资源管理器 29 / 系统 14 / Edge 12 / 更新 7 / 安全 10）
  - 每行实时读取注册表显示当前状态与当前值
  - 支持四种操作：注册表读写（含 Wow64 视图）、服务启动类型
    （禁用时同步停止服务）、生效命令、关联变更通知
  - 「只选推荐项」自动排除降低系统防护的条目，界面标红并默认不勾
  - 记录**优化前原值快照**，支持单项还原与全部还原
- 优化定义采用项目自建格式 `src/Data/Optimizations.xml`，可直接编辑扩充

### 修复

- **DWORD 符号问题导致"已优化"被误判** —— 注册表 `0xFFFFFFFF` 用 `int` 读出来是 `-1`，
  与配置里的 `4294967295` 判为不等；现在统一归一化为无符号十进制

---

## [1.0.0] - 2026-09-26

首个版本。

### 功能

- **网络检测**：分层定位断网原因（协议栈 → 网关 → 外网 → DNS → HTTP），
  逐个实测本机 DNS 服务器，**代理探测**可识别"系统代理指向本机但端口不通"这一
  最常见的假断网原因；hosts / Winsock / 服务 / 防火墙 / 默认路由 / 网卡节能检查。只读。
- **断网修复**：一键修复、10 个可单独勾选的修复项、按症状快速处置、深度修复。
  **改前自动写状态快照**。
- **网络优化**：12 个公共 DNS 并发测速择优切换；TCP 参数调优；
  关闭网卡节能；按连接数查看占用网速的进程。
- **运行日志**：全部操作记录到界面与 `logs\` 目录。

[1.4.0]: https://github.com/xiyanruoxue/NetDoctor/releases/tag/v1.4.0
[1.3.0]: https://github.com/xiyanruoxue/NetDoctor/releases/tag/v1.3.0
[1.2.0]: https://github.com/xiyanruoxue/NetDoctor/releases/tag/v1.2.0
[1.1.0]: https://github.com/xiyanruoxue/NetDoctor/releases/tag/v1.1.0
[1.0.0]: https://github.com/xiyanruoxue/NetDoctor/releases/tag/v1.0.0
