# 贡献指南

感谢有兴趣参与！这个项目是一个 Windows 系统工具，改动会**真实修改用户的注册表、服务和文件**，
所以对正确性和安全性的要求比一般项目高。

---

## 环境准备

- Windows 10 / 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- 建议在**虚拟机**里测试改动（Hyper-V / VMware / VirtualBox 都行）
  - 优化、清理、服务、启动项这些功能在真机上试错代价很高
  - 建议先建快照再测

```powershell
git clone https://github.com/George01230123/NetDoctor.git
cd NetDoctor
dotnet build src/NetDoctor.csproj -c Release
```

---

## 提交前必做

```powershell
# 1. 编译无警告
dotnet build src/NetDoctor.csproj -c Release

# 2. 自测全绿（退出码必须为 0）
dotnet build selftest/SelfTest.csproj -c Release
.\selftest\bin\Release\net10.0-windows\NetDoctorSelfTest.exe
echo $LASTEXITCODE   # 应该是 0
```

自测会真实读取本机网卡、服务、启动项、Appx、硬件与磁盘速度，所以**必须在 Windows 真机或虚拟机里跑**，
CI 上只当作提示（不阻断）。

---

## 代码约定

### 分层

```
Core/   引擎层 —— 只做事情，不碰界面。所有对外操作都走这里
UI/     界面层 —— 只负责展示和调用 Core
```

不要在 `UI/` 里直接写注册表操作或命令拼接，放到 `Core/` 对应的类里。

### 异步

界面**绝不允许**在 UI 线程上同步执行外部命令。
所有命令走 `Cmd.RunAsync`，所有耗时逻辑 `await`：

```csharp
// ✗ 会卡死界面
var r = Cmd.Run("netsh", "int tcp show global");

// ✓
var r = await Cmd.Netsh("int tcp show global", 20000);
```

（这个坑真实发生过：早期版本在构造函数里同步跑 `netsh`，导致启动直接卡住。）

### 日志

所有对系统有影响的操作都要记日志，用 `Log.Info / Ok / Warn / Err / Step`。
失败的路径尤其要写清楚，用户就是靠日志反馈问题的。

```csharp
Log.Step("重置 Winsock 目录");
bool ok = await Log.RunLogged("重置 Winsock 目录", "netsh", "winsock reset", 60000);
if (ok) Log.Warn("需重启电脑才会完全生效");
```

### 破坏性操作的三条铁律

1. **写之前先快照**。参考 `Backup.Snapshot()` 和 `WindowsOptimizer.SnapshotValues()`。
2. **给还原路径**。优化项必须在 `Optimizations.xml` 里同时写 `<Apply>` 和 `<Revert>`。
3. **执行前弹确认**，把影响范围列清楚（参考 `WindowsView.ApplyChecked` 里列出风险项的做法）。

### 编码

- 所有源文件 **UTF-8**，不要加 BOM
- **`.ps1` 脚本必须加 UTF-8 BOM** —— Windows PowerShell 5.1 对无 BOM 的 UTF-8 按 ANSI 解码，
  中文会变乱码并直接引发语法错误（这个坑也踩过）
- 界面文案用简体中文，与现有风格保持一致

---

## 新增一条 Windows 优化项

编辑 `src/Data/Optimizations.xml`：

```xml
<Item name="显示的名称" key="HKEY_CURRENT_USER\完整\注册表\路径" value="值名" target="目标值">
  <Apply>
    <RegWrite key="..." value="..." type="REG_DWORD" data="0"/>
  </Apply>
  <Revert>
    <RegWrite key="..." value="..." type="REG_DWORD" data="1"/>
  </Revert>
</Item>
```

- `key` / `value` / `target` 决定状态检测：读到 `value` 等于 `target` 就显示「已优化」
- `<Apply>` 和 `<Revert>` **都必须写**，否则自测会失败（有断言检查）
- 支持的标签：`RegWrite`、`RegDelete`、`SetServiceStart`（`name` + `start`）、`ExplorerNotify`（`cmd` / `notify`）
- 可选属性：`wow64="true"`（走 32 位视图）、`skipError="true"`（失败不报错）
- 会降低系统防护的项，命名里带上「防火墙」「内存完整」「虚拟化安全」等关键词，
  界面会自动标红（见 `OptItem.HighRisk`）

改完跑自测，会检查条目数、每项是否都有 Apply 和 Revert、以及状态检测是否抛异常。

---

## 提交信息

用 `<类型>: <说明>` 的格式，说明用中文写清楚**为什么**改：

```
fix: 修复显存读取失败（MatchingDeviceId 含 SUBSYS 段导致 EndsWith 匹配不上）
feat: 硬件检测增加电池循环次数
docs: 使用说明改为通用表述
build: 关闭 SourceLink 版本后缀
```

---

## 不要提交的东西

`.gitignore` 已排除，确认一下别用 `git add -f` 绕过：

- `bin/` `obj/` `publish/` —— 构建产物
- `*.exe` `*.dll` `*.pdb` —— 二进制
- `logs/` `backup/` `.runtime/` —— 运行时生成
- 任何第三方软件的文件（见 [NOTICE.md](NOTICE.md)）

---

## 请不要提交这类功能

出于合规与安全考虑，以下方向的 PR 不会被接受：

- 破解、绕过他人系统管控（机房管理、家长控制、企业策略等）的功能
- 关闭/绕过杀毒软件、系统安全机制的功能
- 键盘记录、屏幕窃取、远程控制他人设备的功能
- 内置或重新分发他人的激活脚本、破解工具

修复和优化自己的机器没问题，越过这条线就不行。

---

## 反馈问题

用 [Issue 模板](.github/ISSUE_TEMPLATE/) 提，**务必附上 `logs/` 里的日志**，
只说"不好使"没法定位。日志里的用户名、内网 IP、MAC 记得先打码。
