# Codex / DeepSeek Dual-Model Switcher

**Windows 10 / 11 · Single-file portable · Version 1.6.1**

[简体中文](#简体中文) · [English](#english)

---

## 简体中文

一个面向 Windows 的 Codex 双模型管理工具。通过单个 `.cmd` 文件完成安装、更新、状态检查、历史清理和干净卸载，并可在 ChatGPT 与 DeepSeek 配置之间切换。无需额外安装 Node.js、Python 或 C# 编译器。

> 本工具会修改 Codex 的提供商配置并安装用于模型检查的 Hook。首次使用前请完全退出 Codex，并确认你了解这些改动。

### 下载

- [Codex双模型切换器 1.6.1（ZIP 发布包，内含原始 CMD）](./Codex双模型切换器-单文件管理版-1.6.1.zip)
- [离线使用说明](./使用说明.md)
- [1.6.1 验证报告](./verification-1.6.1.json)

ZIP SHA-256：

```text
FBF1D110DA025C6039CB22A026D04E70CD2C2260BFE03423B35017BE52B065FD
```

ZIP 内原始 CMD 的 SHA-256：

```text
DA4CBEAF13406DA43DAC41B62113C825FEC74D7F48725E6EDCF0387A534D451E
```

### 功能

- 一个单文件便携管理包，不需要解压或单独安装程序
- 安装或更新 Codex 双模型切换工具
- 在 ChatGPT 与 DeepSeek 提供商配置之间切换
- 本地设置和保存 DeepSeek API Key
- 查看当前安装、配置和 Hook 状态
- 清理历史文件与无效文件
- 干净卸载，并尽量恢复安装前的环境

### 系统要求

- Windows 10 / 11 x64
- 已安装并登录支持 Hooks 的新版 Codex 桌面端
- Windows PowerShell 5.1 或更高版本
- 不需要 Node.js、Python 或 C# 编译器

### 快速开始

1. 完全退出 Codex 桌面端。
2. 下载 ZIP 发布包，解压后双击 `Codex双模型切换器-单文件管理版-1.6.1.cmd`。
3. 在菜单中选择 **1. 安装 / 更新**。
4. 如需使用 DeepSeek，选择 **2. 设置 DeepSeek API Key**，在本地输入你自己的 Key。
5. 从 `%USERPROFILE%\.codex\provider-switch` 将两个切换快捷方式固定到任务栏。
6. 重新打开 Codex。切换模型后，请完全退出并重新打开 Codex，让新配置生效。

### 菜单

| 选项 | 作用 |
| --- | --- |
| 1 | 安装 / 更新 |
| 2 | 设置 DeepSeek API Key |
| 3 | 查看状态 |
| 4 | 清理历史与无效文件 |
| 5 | 干净卸载 |

### 更新与卸载

更新会保留模型偏好、GPT 恢复数据、会话归属数据库和本地备份。安装器只替换程序与源码，不会覆盖 `config.toml` 中的其他配置。

首次安装时会创建 ChatGPT 和 DeepSeek 两个桌面快捷方式。后续更新不会补回用户主动删除的桌面快捷方式。

完全退出 Codex 后选择“干净卸载”，工具会：

- 恢复 `config.toml` 的正常模式并移除 DeepSeek 托管区块
- 移除发送前模型检查 Hook 及其信任项
- 移除切换快捷方式
- 恢复或删除由本工具管理的 `DEEPSEEK_API_KEY`
- 删除 `provider-switch` 工具目录、数据库、日志和备份

卸载不会删除 Codex 登录信息、聊天记录、会话历史或其他配置。

### API Key 与隐私

- 仓库和安装包不包含 `config.toml`、`auth.json`、API Key、聊天记录或用户数据库。
- DeepSeek Key 只保存在目标电脑当前 Windows 用户的环境变量中。
- 若安装器替换过已有 Key，会使用 Windows DPAPI 加密保存恢复信息，卸载时恢复。
- 请勿将 API Key、账号凭据或本机配置提交到本仓库，也不要发送给其他人。

### 完整性校验

单文件包内包含 `MANIFEST.sha256`。安装或更新前会校验包内文件；校验失败会停止执行。

安装包内嵌的 20 个文件已按 `MANIFEST.sha256` 完整复核。此处仅证明当前发布文件与清单一致，不代表对其后任何修改的担保。

### 1.6.1 更新

- 首次安装时创建 ChatGPT、DeepSeek 两个桌面快捷方式
- 更新不会恢复用户主动删除的桌面快捷方式
- 配置、校验和环境路由保存后，提前关闭快捷方式启动的独立控制台窗口
- 图标与任务栏缓存仍会同步刷新，错误提示和重定向输出保持可用
- 安装失败时会回滚本次新建的桌面快捷方式

[返回双语导航](#codex--deepseek-dual-model-switcher)

---

## English

A Windows utility for managing two Codex model configurations from one portable `.cmd` file. It supports installation, updates, status checks, cleanup, clean uninstallation, and switching between ChatGPT and DeepSeek provider configurations. Node.js, Python, and a C# compiler are not required.

> This tool changes Codex provider configuration and installs a model-checking hook. Fully exit Codex before first use and review these changes before running the script.

### Download

- [Codex dual-model switcher 1.6.1 (ZIP package containing the original `.cmd`)](./Codex双模型切换器-单文件管理版-1.6.1.zip)
- [Offline Chinese guide](./使用说明.md)
- [1.6.1 verification report](./verification-1.6.1.json)

ZIP SHA-256:

```text
FBF1D110DA025C6039CB22A026D04E70CD2C2260BFE03423B35017BE52B065FD
```

SHA-256 of the original `.cmd` inside the ZIP:

```text
DA4CBEAF13406DA43DAC41B62113C825FEC74D7F48725E6EDCF0387A534D451E
```

### Features

- Single-file portable package with no separate installer or extraction step
- Installs or updates the Codex dual-model switcher
- Switches between ChatGPT and DeepSeek provider configurations
- Sets and stores a DeepSeek API key locally
- Reports installation, configuration, and hook status
- Cleans up old and invalid files
- Performs a clean uninstall and restores the previous environment where possible

### Requirements

- Windows 10 / 11 x64
- A signed-in Codex desktop app that supports hooks
- Windows PowerShell 5.1 or later
- No Node.js, Python, or C# compiler required

### Quick start

1. Fully exit the Codex desktop app.
2. Download the ZIP package, extract it, and double-click `Codex双模型切换器-单文件管理版-1.6.1.cmd`.
3. Select **1. Install / Update**.
4. To use DeepSeek, select **2. Set DeepSeek API Key** and enter your own key locally.
5. Pin the two switcher shortcuts from `%USERPROFILE%\.codex\provider-switch` to the taskbar.
6. Reopen Codex. After switching models, fully exit and reopen Codex so the new provider configuration is loaded.

### Menu

| Option | Action |
| --- | --- |
| 1 | Install / Update |
| 2 | Set DeepSeek API key |
| 3 | Show status |
| 4 | Clean old and invalid files |
| 5 | Clean uninstall |

### Update and uninstall

Updates preserve model preferences, GPT recovery data, the session-ownership database, and local backups. The installer replaces the program and source files without overwriting unrelated settings in `config.toml`.

The first installation creates two desktop shortcuts for ChatGPT and DeepSeek. Later updates do not restore desktop shortcuts that the user deliberately deleted.

After fully exiting Codex, choose **Clean uninstall**. The tool will:

- restore normal `config.toml` mode and remove its managed DeepSeek block
- remove the pre-send model-check hook and its trust entry
- remove the switcher shortcuts
- restore or remove the managed `DEEPSEEK_API_KEY`
- remove the `provider-switch` tool directory, database, logs, and backups

Uninstalling does not remove Codex sign-in information, chats, session history, or unrelated configuration.

### API key and privacy

- The repository and package do not include `config.toml`, `auth.json`, API keys, chats, or user databases.
- The DeepSeek key is stored only in the environment variables of the current Windows user.
- If an existing key is replaced, recovery data is encrypted with Windows DPAPI and restored during uninstall.
- Never commit API keys, account credentials, or local configuration to this repository or share them with others.

### Integrity verification

The single-file package contains `MANIFEST.sha256`. Files are checked before installation or update, and the operation stops if verification fails.

All 20 embedded files were rechecked against `MANIFEST.sha256` for this release. This verifies consistency with the manifest, not later modifications to the downloaded file.

### Version 1.6.1 highlights

- Creates both ChatGPT and DeepSeek desktop shortcuts on the first installation
- Does not restore desktop shortcuts deliberately deleted by the user during an update
- Closes the shortcut's private console window after configuration, verification, and environment routing are saved
- Keeps icon and taskbar cache refreshes synchronous
- Keeps error messages and redirected output available
- Rolls back desktop shortcuts created by a failed installation

[Back to language selector](#codex--deepseek-dual-model-switcher)
