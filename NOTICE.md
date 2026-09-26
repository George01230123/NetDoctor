# 第三方说明 / Third-Party Notice

本项目（夕颜若雪网络工具 / NetDoctor）为独立实现。以下说明涉及的第三方项目，
本仓库**不包含其任何文件**，也未修改或重新分发其内容。

---

## 1. Windows 优化的条目定义

`src/Data/Optimizations.xml` 收录了 151 条 Windows 优化项，每条包含：

- 用于判断当前状态的注册表键 / 值 / 目标值
- 应用与还原所需的注册表操作、服务启动类型、生效命令

这些内容是**针对 Windows 自身注册表与服务的功能性技术条目**（例如
`HKCU\Software\Microsoft\Windows\CurrentVersion\Search` 的 `SearchboxTaskbarMode` 应设为 `0`），
属于系统配置事实，并非第三方软件的代码或数据文件。本文件采用本项目自建的 XML 格式，
由本项目独立维护，可直接编辑扩充。

这些条目的整理参考了公开流传的 Windows 优化实践与社区共识，包括
ZyperWin++ 等工具公开的优化列表。
**本仓库不包含 ZyperWin++ 的任何文件**（其优化配置文件、`Bin\` 下的脚本、主程序均未收录）。

若相关作者认为本项目的条目整理方式不妥，请提 Issue，我们会立即调整或移除相应内容。

---

## 2. 硬件检测 → 工具启动器

该功能会扫描用户本机已安装的**图吧工具箱**（https://www.tbtool.cn ）的 `tools` 目录，
并调用其中的原版工具（CPU-Z、AIDA64、CrystalDiskInfo、FurMark 等）。

- 本仓库**不包含**图吧工具箱的任何文件、皮肤、加密数据库（`List\*.edb`、`data\*.edb`）
- 本程序**只做启动**，不复制、不修改、不重新分发这些第三方工具
- 这些工具各自的版权归其各自作者所有，使用需遵守各自的许可协议

如果本机没有安装图吧工具箱，该功能会提示未找到目录，不影响其它功能。

---

## 3. 运行环境依赖

| 依赖 | 用途 | 许可 |
|---|---|---|
| [.NET 10](https://dotnet.microsoft.com/) | 运行时与 SDK | MIT |
| [System.ServiceProcess.ServiceController](https://www.nuget.org/packages/System.ServiceProcess.ServiceController) | 服务枚举与控制 | MIT |

发布为**单文件自包含** exe 时，.NET 运行时会被打包进 exe，因此分发包体积约 47 MB。

---

## 4. 系统激活

「系统工具 → 系统激活」页**不内置任何激活脚本**。
它只会在本机搜索用户自己已有的 `MAS_AIO_CN.cmd` 并调用，不下载、不分发、不包含该脚本。
MAS 的版权归其作者所有（https://github.com/massgravel/Microsoft-Activation-Scripts ）。
