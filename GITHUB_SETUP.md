# GitHub 发布步骤

仓库已经在本机初始化好（`main` 分支，含 `v1.3.0` tag），只差推到远程。

---

## 一、先在 GitHub 网页上建空仓库

1. 打开 https://github.com/new
2. **Repository name** 填 `NetDoctor`
3. **Description** 建议填：
   `一体化 Windows 系统工具：网络诊断修复、Windows 优化(151项)、垃圾清理、硬件检测`
4. 可见性按需选择（Public 公开 / Private 私有）
5. ⚠ **不要**勾选 "Add a README file"、"Add .gitignore"、"Choose a license"
   —— 仓库里已经有这些文件了，勾了会造成冲突
6. 点 **Create repository**

建完后 GitHub 会给你一个地址，形如：

```
https://github.com/<你的用户名>/NetDoctor.git
```

---

## 二、配置远程并推送

把下面的 `<你的用户名>` 换成你的 GitHub 用户名，然后逐条执行：

```powershell
cd "D:\huanjin\NetDoctor"

# 1. 配置远程仓库
git remote add origin https://github.com/<你的用户名>/NetDoctor.git

# 2. 确认分支名（已经叫 main，这步是保险）
git branch -M main

# 3. 推送代码
git push -u origin main

# 4. 推送 tag —— 这一步会触发 CI 自动构建并发 Release
git push origin v1.3.0
```

> 第一次推可能会弹出浏览器让你登录 GitHub，按提示授权即可。
> 如果之前配过 SSH，也可以把地址换成 `git@github.com:<你的用户名>/NetDoctor.git`。

---

## 三、检查自动构建

推完 tag 后：

1. 打开仓库 → **Actions** 标签页
2. 应该看到一个 `build` 工作流在跑（约 3~6 分钟）
3. 跑完后去 **Releases** 标签页，应该已经有 `v1.3.0`，附件是 `夕颜若雪网络工具.exe`

工作流做三件事：

- 在 `windows-latest` 上构建主程序与自测工程
- 跑一次自测（CI 上允许失败，因为部分检测依赖真实硬件/网络）
- 发布单文件自包含 exe，并在 Release 说明里写入大小与 SHA256

---

## 四、可选：后续更新流程

```powershell
# 改完代码，跑一遍自测确认没坏
dotnet build selftest/SelfTest.csproj -c Release
.\selftest\bin\Release\net10.0-windows\NetDoctorSelfTest.exe

# 提交
git add -A
git commit -m "feat: 新增 XXX 功能"
git push

# 发新版本（改一下 src/NetDoctor.csproj 里的 Version / FileVersion / AssemblyVersion）
git tag -a v1.4.0 -m "v1.4.0"
git push origin v1.4.0
```

---

## 五、建议补充的仓库设置

| 位置 | 建议 |
|---|---|
| **About** → Description | 填上面那句简介 |
| **About** → Topics | `windows` `system-optimizer` `network-tools` `hardware-info` `dotnet` `winforms` `csharp` |
| **Settings** → Features → Issues | 打开（Issue 模板已就位） |
| **Settings** → Features → Discussions | 可选，开了可以把提问和 Bug 分开 |
| **Releases** → 编辑 v1.3.0 | 勾上 "Set as the latest release" |

---

## 六、注意事项

- **不要**把 `E:\挂\xiyanruoxue\` 那个目录整个传上去。那是你的使用环境，
  里面有别人的工具箱（含远程控制、内核驱动、机房管理破解模块），
  传到 GitHub 会因违反可接受使用政策被封号，跟有没有写来源声明无关。
- **不要**传 `D:\huanjin\zyper_src\`（反编译产物），那是别人软件的逆向结果。
- 仓库里只应有**你自己写的代码**。目前 `.gitignore` 已经排除了所有构建产物和二进制，
  但新增文件时留意一下别用 `git add -f` 绕过。
