# Codex / DeepSeek 双模型切换器 1.6.1

这是一个单文件便携管理包。双击 `.cmd` 后会出现菜单：

1. 安装 / 更新
2. 设置 DeepSeek API Key
3. 查看状态
4. 清理历史与无效文件
5. 干净卸载

## 系统要求

- Windows 10 / 11 x64
- 已安装并登录支持 Hooks 的新版 Codex 桌面端
- Windows PowerShell 5.1 或更高版本
- 不需要 Node.js、Python 或 C# 编译器

## 使用方式

1. 完全退出 Codex 桌面端。
2. 双击单文件 `.cmd`。
3. 选择“安装 / 更新”。
4. 如需使用 DeepSeek，在菜单中选择“设置 DeepSeek API Key”。
5. 从 `%USERPROFILE%\.codex\provider-switch` 固定两个切换快捷方式到任务栏。

更新会保留模型偏好、GPT 恢复数据、会话归属数据库和本地备份。安装器只替换程序与源码，不会覆盖 `config.toml` 中的其他配置。

## 干净卸载

完全退出 Codex 后选择“干净卸载”。卸载会：

- 将 `config.toml` 恢复正常模式并移除本工具的 DeepSeek 托管区块
- 移除发送前模型检查 Hook 及其信任项
- 移除两个切换快捷方式
- 恢复或删除由本工具管理的 `DEEPSEEK_API_KEY`
- 删除 `provider-switch` 工具目录、数据库、日志和备份

不会删除 Codex 登录信息、聊天记录、会话历史或其他配置。

## Key 与隐私

- 安装包不包含 `config.toml`、`auth.json`、API Key、聊天记录或用户数据库。
- DeepSeek Key 只保存在目标电脑当前 Windows 用户环境变量中。
- 若安装器替换过已有 Key，会使用 Windows DPAPI 加密保存恢复信息，卸载时恢复。

## 校验

`MANIFEST.sha256` 包含安装包内所有文件的 SHA-256。每次安装或更新前都会先校验，校验失败会停止。

## 最短使用流程

```text
退出 Codex -> 双击 CMD -> 1 安装/更新 -> 2 设置 Key -> 固定切换快捷方式 -> 打开 Codex
```

切换模型后，Codex 会要求完全退出再重新打开，以确保当前进程读取新的提供商配置。

## 1.6.1 更新

- 首次安装时自动创建 ChatGPT、DeepSeek 两个桌面快捷方式。更新不会补回用户删除的桌面快捷方式。
- 配置、校验与环境路由保存成功后，提前关闭快捷方式启动的独立黑窗；图标和任务栏缓存刷新仍同步完成。错误仍显示提示。命令行和重定向输出保持可用。
- 安装失败会回滚新建桌面快捷方式；不会覆盖其他程序占用的同名快捷方式。
