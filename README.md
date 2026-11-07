# Codex / DeepSeek Dual-Model Switcher

**Windows 10 / 11 · Single-file portable · Version 1.6.5**

[English](#english) · [中文](#中文)

## English

A Windows utility for managing ChatGPT and DeepSeek provider configurations for the Codex desktop app from one portable `.cmd` file. It supports installation, updates, API key setup, provider switching, and clean uninstallation. Node.js, Python, and a C# compiler are not required.

> This tool modifies Codex provider configuration and installs a model-checking hook. Fully exit Codex before the first use and review these changes before running the script.

### Download

- [Codex dual-model switcher 1.6.5 (single-file CMD)](./Codex双模型切换器-单文件管理版-1.6.5.cmd)
- [Chinese usage guide](./使用说明-1.6.5.md)
- [1.6.5 release notes](./1.6.5更新说明.md)
- [1.6.5 verification report](./verification-1.6.5.json)

SHA-256 of the CMD:

```text
D112DC4A0F3374588579E21FFF60CB3A113D605A494DFEA8864AE6C53AD6117F
```

### Version 1.6.5 highlights

- Fixes the DeepSeek welcome screen by declaring `requires_openai_auth = true` while retaining `env_key = "DEEPSEEK_API_KEY"`.
- Keeps ChatGPT account recognition available in DeepSeek mode without sending the ChatGPT token to DeepSeek.
- Simplifies the menu to **Install / Update**, **Set DeepSeek API Key**, and **Clean Uninstall**.
- Includes old-file and temporary-file cleanup in clean uninstallation.
- Keeps the continuous menu after each command; press Enter directly to exit.

### Features

- Single-file portable package with no separate installer
- Installs or updates the Codex dual-model switcher
- Switches between ChatGPT and DeepSeek provider configurations
- Sets and stores a DeepSeek API key locally
- Preserves chat history, account sign-in, model preferences, and existing shortcuts
- Performs a clean uninstall and restores the previous environment where possible

### Requirements

- Windows 10 / 11 x64
- A signed-in Codex desktop app that supports hooks
- Windows PowerShell 5.1 or later
- No Node.js, Python, or C# compiler required

### Quick start

1. Fully exit the Codex desktop app, including the tray process and `ChatGPT.exe`.
2. Download `Codex双模型切换器-单文件管理版-1.6.5.cmd`.
3. Double-click the CMD and select **1. Install / Update**.
4. To use DeepSeek, select **2. Set DeepSeek API Key** and enter your own key locally.
5. Pin the two switcher shortcuts from `%USERPROFILE%\.codex\provider-switch` to the taskbar.
6. Reopen Codex. After switching models, fully exit and reopen Codex so the new provider configuration is loaded.

### Menu

| Option | Action |
| --- | --- |
| 1 | Install / Update |
| 2 | Set DeepSeek API key |
| 3 | Clean uninstall |

### Update and uninstall

Updates preserve model preferences, GPT recovery data, the session-ownership database, and local backups. The installer replaces the program and source files without overwriting unrelated settings in `config.toml`.

The first installation creates ChatGPT and DeepSeek desktop shortcuts. Later updates do not restore shortcuts that the user deliberately deleted.

After fully exiting Codex, choose **3. Clean uninstall**. The tool will:

- restore normal `config.toml` mode and remove its managed DeepSeek block
- remove the pre-send model-check hook and its trust entry
- remove the switcher shortcuts
- restore or remove the managed `DEEPSEEK_API_KEY`
- remove the `provider-switch` tool directory, database, logs, backups, and temporary files

Uninstalling does not remove Codex sign-in information, chats, session history, or unrelated configuration.

### API key and privacy

- The repository and package do not include `config.toml`, `auth.json`, API keys, chats, or user databases.
- The DeepSeek key is stored only in the environment variables of the current Windows user.
- If an existing key is replaced, recovery data is encrypted with Windows DPAPI and restored during uninstall.
- Never commit API keys, account credentials, or local configuration to this repository.

### Integrity verification

The single-file package contains `MANIFEST.sha256`. Embedded files are checked before installation or update, and the operation stops if verification fails.

The 1.6.5 package was built from 20 embedded files. All manifest checks passed, the isolated installation reported version 1.6.5, and a real DeepSeek request returned `OK`.

### License

This project is open source under the [MIT License](LICENSE).

[Back to language links](#codex--deepseek-dual-model-switcher)

## 中文

一个面向 Windows 的 Codex 双模型管理工具。通过单个 `.cmd` 文件完成安装、更新、DeepSeek API Key 设置、ChatGPT 与 DeepSeek 配置切换以及干净卸载。无需额外安装 Node.js、Python 或 C# 编译器。

> 本工具会修改 Codex 的提供商配置，并安装用于模型检查的 Hook。首次使用前请完全退出 Codex，并确认你了解这些改动。

### 下载

- [Codex 双模型切换器 1.6.5（单文件 CMD）](./Codex双模型切换器-单文件管理版-1.6.5.cmd)
- [离线使用说明](./使用说明-1.6.5.md)
- [1.6.5 更新说明](./1.6.5更新说明.md)
- [1.6.5 验证报告](./verification-1.6.5.json)

CMD SHA-256：

```text
D112DC4A0F3374588579E21FFF60CB3A113D605A494DFEA8864AE6C53AD6117F
```

### 1.6.5 更新重点

- DeepSeek 提供商增加 `requires_openai_auth = true`，不再进入匿名欢迎流程。
- 同时保留 `env_key = "DEEPSEEK_API_KEY"`，真实 DeepSeek 请求仍由专用 API Key 认证。
- 菜单精简为 **安装 / 更新**、**设置 DeepSeek API Key**、**干净卸载** 三项。
- 历史文件和无效临时文件清理已并入干净卸载。
- 每次操作完成后仍会回到菜单；直接按 Enter 即可退出。

### 功能

- 单文件便携管理包，不需要单独安装程序
- 安装或更新 Codex 双模型切换工具
- 在 ChatGPT 与 DeepSeek 提供商配置之间切换
- 在本地设置和保存 DeepSeek API Key
- 保留聊天记录、账号登录、模型偏好和已有快捷方式
- 干净卸载，并尽量恢复安装前的环境

### 系统要求

- Windows 10 / 11 x64
- 已安装并登录支持 Hooks 的新版 Codex 桌面端
- Windows PowerShell 5.1 或更高版本
- 不需要 Node.js、Python 或 C# 编译器

### 快速开始

1. 完全退出 Codex 桌面端，包括托盘进程和 `ChatGPT.exe`。
2. 下载 `Codex双模型切换器-单文件管理版-1.6.5.cmd`。
3. 双击 CMD，在菜单中选择 **1. 安装 / 更新**。
4. 如需使用 DeepSeek，选择 **2. 设置 DeepSeek API Key**，在本地输入你自己的 Key。
5. 从 `%USERPROFILE%\.codex\provider-switch` 将两个切换快捷方式固定到任务栏。
6. 重新打开 Codex。切换模型后，请完全退出并重新打开 Codex，让新配置生效。

### 菜单

| 选项 | 作用 |
| --- | --- |
| 1 | 安装 / 更新 |
| 2 | 设置 DeepSeek API Key |
| 3 | 干净卸载 |

### 更新与卸载

更新会保留模型偏好、GPT 恢复数据、会话归属数据库和本地备份。安装器只替换程序与嵌入源码，不会覆盖 `config.toml` 中的其他配置。

首次安装时会创建 ChatGPT 和 DeepSeek 两个桌面快捷方式。后续更新不会补回用户主动删除的桌面快捷方式。

完全退出 Codex 后选择 **3. 干净卸载**，工具会：

- 恢复 `config.toml` 的正常模式并移除 DeepSeek 托管区块
- 移除发送前模型检查 Hook 及其信任项
- 移除切换快捷方式
- 恢复或删除由本工具管理的 `DEEPSEEK_API_KEY`
- 删除 `provider-switch` 工具目录、数据库、日志、备份和临时文件

卸载不会删除 Codex 登录信息、聊天记录、会话历史或其他配置。

### API Key 与隐私

- 仓库和安装包不包含 `config.toml`、`auth.json`、API Key、聊天记录或用户数据库。
- DeepSeek Key 只保存在目标电脑当前 Windows 用户的环境变量中。
- 若安装器替换过已有 Key，会使用 Windows DPAPI 加密保存恢复信息，卸载时恢复。
- 请勿将 API Key、账号凭据或本机配置提交到本仓库。

### 完整性校验

单文件包内包含 `MANIFEST.sha256`。安装或更新前会校验包内文件；校验失败会停止执行。

1.6.5 发布包内嵌 20 个文件，全部通过清单校验；隔离安装版本为 1.6.5，真实 DeepSeek 请求返回 `OK`。

### 许可证

本项目采用 [MIT License](LICENSE) 开源。

[返回语言导航](#codex--deepseek-dual-model-switcher)
