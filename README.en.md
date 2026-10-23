# Codex / DeepSeek Dual-Model Switcher

**Windows 10 / 11 · Single-file portable · Version 1.6.1**

[简体中文](README.md) · [English](README.en.md)

A Windows utility for managing two Codex model configurations from one portable `.cmd` file. It supports installation, updates, status checks, cleanup, clean uninstallation, and switching between ChatGPT and DeepSeek provider configurations. Node.js, Python, and a C# compiler are not required.

> This tool changes Codex provider configuration and installs a model-checking hook. Fully exit Codex before first use and review these changes before running the script.

## Download

- [Codex dual-model switcher 1.6.1 (ZIP package containing the original `.cmd`)](./Codex双模型切换器-单文件管理版-1.6.1.zip)
- [Chinese offline guide](./使用说明.md)
- [1.6.1 verification report](./verification-1.6.1.json)

ZIP SHA-256:

```text
FBF1D110DA025C6039CB22A026D04E70CD2C2260BFE03423B35017BE52B065FD
```

SHA-256 of the original `.cmd` inside the ZIP:

```text
DA4CBEAF13406DA43DAC41B62113C825FEC74D7F48725E6EDCF0387A534D451E
```

## Features

- Single-file portable package with no separate installer or extraction step
- Installs or updates the Codex dual-model switcher
- Switches between ChatGPT and DeepSeek provider configurations
- Sets and stores a DeepSeek API key locally
- Reports installation, configuration, and hook status
- Cleans up old and invalid files
- Performs a clean uninstall and restores the previous environment where possible

## Requirements

- Windows 10 / 11 x64
- A signed-in Codex desktop app that supports hooks
- Windows PowerShell 5.1 or later
- No Node.js, Python, or C# compiler required

## Quick start

1. Fully exit the Codex desktop app.
2. Download the ZIP package, extract it, and double-click `Codex双模型切换器-单文件管理版-1.6.1.cmd`.
3. Select **1. Install / Update**.
4. To use DeepSeek, select **2. Set DeepSeek API Key** and enter your own key locally.
5. Pin the two switcher shortcuts from `%USERPROFILE%\.codex\provider-switch` to the taskbar.
6. Reopen Codex. After switching models, fully exit and reopen Codex so the new provider configuration is loaded.

## Menu

| Option | Action |
| --- | --- |
| 1 | Install / Update |
| 2 | Set DeepSeek API key |
| 3 | Show status |
| 4 | Clean old and invalid files |
| 5 | Clean uninstall |

## Update and uninstall

Updates preserve model preferences, GPT recovery data, the session-ownership database, and local backups. The installer replaces the program and source files without overwriting unrelated settings in `config.toml`.

The first installation creates two desktop shortcuts for ChatGPT and DeepSeek. Later updates do not restore desktop shortcuts that the user deliberately deleted.

After fully exiting Codex, choose **Clean uninstall**. The tool will:

- restore normal `config.toml` mode and remove its managed DeepSeek block
- remove the pre-send model-check hook and its trust entry
- remove the switcher shortcuts
- restore or remove the managed `DEEPSEEK_API_KEY`
- remove the `provider-switch` tool directory, database, logs, and backups

Uninstalling does not remove Codex sign-in information, chats, session history, or unrelated configuration.

## API key and privacy

- The repository and package do not include `config.toml`, `auth.json`, API keys, chats, or user databases.
- The DeepSeek key is stored only in the environment variables of the current Windows user.
- If an existing key is replaced, recovery data is encrypted with Windows DPAPI and restored during uninstall.
- Never commit API keys, account credentials, or local configuration to this repository or share them with others.

## Integrity verification

The single-file package contains `MANIFEST.sha256`. Files are checked before installation or update, and the operation stops if verification fails.

All 20 embedded files were rechecked against `MANIFEST.sha256` for this release. This verifies consistency with the manifest, not later modifications to the downloaded file.

## Version 1.6.1 highlights

- Creates both ChatGPT and DeepSeek desktop shortcuts on the first installation
- Does not restore desktop shortcuts deliberately deleted by the user during an update
- Closes the shortcut's private console window after configuration, verification, and environment routing are saved
- Keeps icon and taskbar cache refreshes synchronous
- Keeps error messages and redirected output available
- Rolls back desktop shortcuts created by a failed installation

[Switch to 简体中文](README.md)
