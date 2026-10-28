[CmdletBinding()]
param(
    [ValidateSet('Menu','Install','Update','Uninstall','SetKey','Status','Cleanup')]
    [string]$Action = 'Menu',
    [string]$CodexDir = $(if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $env:USERPROFILE '.codex' }),
    [string]$PackageRoot = $PSScriptRoot,
    [string]$IsolatedRoot,
    [string]$CliPath,
    [switch]$RemoveSharedKey,
    [switch]$KeepData,
    [switch]$SkipIdleCheck,
    [switch]$ReadKeyFromStdin
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Add-Type -AssemblyName System.Security
Add-Type -AssemblyName System.IO.Compression.FileSystem

$script:Utf8 = New-Object System.Text.UTF8Encoding($false)
[Console]::InputEncoding = $script:Utf8
[Console]::OutputEncoding = $script:Utf8
$script:Version = (Get-Content -LiteralPath (Join-Path $PackageRoot 'VERSION.txt') -Raw -Encoding UTF8).Trim()
$script:CodexDir = [IO.Path]::GetFullPath($CodexDir).TrimEnd('\','/')
$script:ToolDir = Join-Path $script:CodexDir 'provider-switch'
$script:ConfigPath = Join-Path $script:CodexDir 'config.toml'
$script:HooksPath = Join-Path $script:CodexDir 'hooks.json'
$script:ManifestPath = Join-Path $script:ToolDir 'install.json'
$script:IsIsolated = -not [string]::IsNullOrWhiteSpace($IsolatedRoot)

if ($script:IsIsolated) {
    $script:IsolatedRoot = [IO.Path]::GetFullPath($IsolatedRoot).TrimEnd('\','/')
    if (-not $script:CodexDir.StartsWith($script:IsolatedRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw '隔离测试目录越界。'
    }
    $script:DesktopDir = Join-Path $script:IsolatedRoot 'Desktop'
    $script:TaskbarDir = Join-Path $script:IsolatedRoot 'TaskBar'
    $script:StartMenuDir = Join-Path $script:IsolatedRoot 'StartMenu'
} else {
    $script:DesktopDir = [Environment]::GetFolderPath('DesktopDirectory')
    $script:TaskbarDir = Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar'
    $script:StartMenuDir = [Environment]::GetFolderPath('Programs')
}

function Write-Step([string]$Message) { Write-Host ('  ' + $Message) }
function Hash-Bytes([byte[]]$Bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($Bytes)).Replace('-','') }
    finally { $sha.Dispose() }
}
function Hash-File([string]$Path) { return Hash-Bytes ([IO.File]::ReadAllBytes($Path)) }
function Read-Json([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    return (Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json)
}
function Write-Text([string]$Path, [string]$Text) {
    $directory = Split-Path -Parent $Path
    if ($directory) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
    $temporary = $Path + '.provider-next-' + [Guid]::NewGuid().ToString('N')
    try {
        [IO.File]::WriteAllText($temporary, $Text, $script:Utf8)
        if (Test-Path -LiteralPath $Path) {
            [IO.File]::Replace($temporary, $Path, [NullString]::Value)
        } else {
            [IO.File]::Move($temporary, $Path)
        }
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}
function Write-Json([string]$Path, $Value) {
    Write-Text $Path (($Value | ConvertTo-Json -Depth 60) + [Environment]::NewLine)
}
function Get-Prop($Object, [string]$Name, $Default = $null) {
    if ($null -eq $Object) { return $Default }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $Default }
    return $property.Value
}
function Set-Prop($Object, [string]$Name, $Value) {
    $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value -Force
}
function Assert-SafePaths {
    if ($script:CodexDir -eq [IO.Path]::GetPathRoot($script:CodexDir) -or $script:CodexDir -eq $env:USERPROFILE) {
        throw 'CODEX_HOME 路径不安全，已停止。'
    }
    if ([IO.Path]::GetFileName($script:ToolDir) -ne 'provider-switch') { throw '工具目录边界不正确。' }
    foreach ($path in @($script:CodexDir, $script:ToolDir)) {
        if (Test-Path -LiteralPath $path) {
            $attributes = (Get-Item -LiteralPath $path -Force).Attributes
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "目录是链接，未作修改：$path"
            }
        }
    }
}
function Assert-Idle {
    if ($SkipIdleCheck -or $script:IsIsolated) { return }
    foreach ($name in @('ChatGPT','Codex')) {
        foreach ($process in @(Get-Process -Name $name -ErrorAction SilentlyContinue)) {
            try {
                if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
                    $path = $process.Path
                    if ($path -and ($path -match 'OpenAI.Codex_' -or $path -match '\\Codex\\')) {
                        throw '请先完全退出 Codex 桌面端，再安装、更新或卸载。本工具不会关闭客户端。'
                    }
                }
            } catch [System.InvalidOperationException] {
            } catch [System.ComponentModel.Win32Exception] {
            } finally {
                $process.Dispose()
            }
        }
    }
    foreach ($name in @('ProviderGuard','Refresh-CodexProviderIcons','Switch-CodexProvider')) {
        foreach ($process in @(Get-Process -Name $name -ErrorAction SilentlyContinue)) {
            try {
                if ($process.Path -and $process.Path.StartsWith($script:ToolDir + '\',[StringComparison]::OrdinalIgnoreCase)) {
                    throw '切换或模型检查程序正在运行，请稍后重试。'
                }
            } finally { $process.Dispose() }
        }
    }
}
function Verify-Package {
    $manifestPath = Join-Path $PackageRoot 'MANIFEST.sha256'
    if (-not (Test-Path -LiteralPath $manifestPath)) { throw '安装包缺少 MANIFEST.sha256。' }
    foreach ($line in Get-Content -LiteralPath $manifestPath -Encoding UTF8) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $parts = $line -split '  ',2
        if ($parts.Count -ne 2) { throw 'MANIFEST.sha256 格式不正确。' }
        $path = Join-Path $PackageRoot ($parts[1].Replace('/', '\'))
        if (-not (Test-Path -LiteralPath $path)) { throw "安装包缺少文件：$($parts[1])" }
        if ((Hash-File $path) -ne $parts[0].ToUpperInvariant()) { throw "安装包校验失败：$($parts[1])" }
    }
}
function Find-Cli {
    if ($CliPath -and (Test-Path -LiteralPath $CliPath)) { return [IO.Path]::GetFullPath($CliPath) }
    $command = Get-Command codex.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $binRoot = Join-Path $env:LOCALAPPDATA 'OpenAI\Codex\bin'
    if (Test-Path -LiteralPath $binRoot) {
        $found = @(Get-ChildItem -LiteralPath $binRoot -Filter codex.exe -Recurse -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending)
        if ($found.Count) { return $found[0].FullName }
    }
    throw '未找到 Codex 客户端组件。请先安装并登录 Codex。'
}
function Invoke-Tool([string]$Name, [string[]]$Arguments) {
    $exe = Join-Path $script:ToolDir $Name
    if (-not (Test-Path -LiteralPath $exe)) { throw "缺少程序：$Name" }
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $exe
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = $script:Utf8
    $info.StandardErrorEncoding = $script:Utf8
    $info.Arguments = (($Arguments | ForEach-Object { '"' + $_.Replace('"','\"') + '"' }) -join ' ')
    $process = [Diagnostics.Process]::Start($info)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    try {
        if (-not $process.WaitForExit(20000)) { throw "操作超时：$Name" }
        $output = $stdout.Result
        $errors = $stderr.Result
        if ($process.ExitCode -ne 0) { throw (($errors + $output).Trim()) }
        return $output.Trim()
    } finally {
        $process.Dispose()
    }
}
function Load-Editor {
    Add-Type -Path (Join-Path $PackageRoot 'ConfigEditor.dll')
}
function Read-ConfigText { return [IO.File]::ReadAllText($script:ConfigPath, $script:Utf8) }
function Config-Provider {
    $text = Read-ConfigText
    $match = [regex]::Match($text, '(?m)^\s*model_provider\s*=\s*"([^"]+)"')
    if ($match.Success) { return $match.Groups[1].Value }
    return 'openai'
}
function New-Transaction {
    $root = Join-Path ([IO.Path]::GetTempPath()) ('CodexProviderTransaction-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $root | Out-Null
    $snapshot = [pscustomobject]@{
        Root = $root
        ToolExisted = (Test-Path -LiteralPath $script:ToolDir)
        ConfigExisted = (Test-Path -LiteralPath $script:ConfigPath)
        HooksExisted = (Test-Path -LiteralPath $script:HooksPath)
        Shortcuts = @{}
    }
    if ($snapshot.ToolExisted) { Copy-Item -LiteralPath $script:ToolDir -Destination (Join-Path $root 'tool') -Recurse }
    foreach ($name in @('config.toml','hooks.json')) {
        $source = Join-Path $script:CodexDir $name
        if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $root $name) }
    }
    foreach ($path in Get-ManagedShortcutPaths) {
        $snapshot.Shortcuts[$path] = [IO.File]::ReadAllBytes($path)
    }
    # Track absent desktop links too, so a failed first install removes new links.
    foreach ($name in @('Codex - ChatGPT.lnk','Codex - DeepSeek.lnk')) {
        $path = Join-Path $script:DesktopDir $name
        if (-not (Test-Path -LiteralPath $path)) { $snapshot.Shortcuts[$path] = $null }
    }
    return $snapshot
}
function Restore-Transaction($Snapshot) {
    Assert-SafePaths
    if (Test-Path -LiteralPath $script:ToolDir) { Remove-Item -LiteralPath $script:ToolDir -Recurse -Force }
    if ($Snapshot.ToolExisted) { Copy-Item -LiteralPath (Join-Path $Snapshot.Root 'tool') -Destination $script:ToolDir -Recurse }
    if ($Snapshot.ConfigExisted) { Copy-Item -LiteralPath (Join-Path $Snapshot.Root 'config.toml') -Destination $script:ConfigPath -Force }
    if ($Snapshot.HooksExisted) { Copy-Item -LiteralPath (Join-Path $Snapshot.Root 'hooks.json') -Destination $script:HooksPath -Force }
    elseif (Test-Path -LiteralPath $script:HooksPath) { Remove-Item -LiteralPath $script:HooksPath -Force }
    foreach ($path in $Snapshot.Shortcuts.Keys) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
        if ($null -ne $Snapshot.Shortcuts[$path]) {
            [IO.File]::WriteAllBytes($path, $Snapshot.Shortcuts[$path])
        } elseif (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force
        }
        try { [ConfigEditor]::ShortcutDeleted($path) } catch {}
    }
}
function Remove-Transaction($Snapshot) {
    $root = [IO.Path]::GetFullPath($Snapshot.Root)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $root.StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase) -or (Split-Path $root -Leaf) -notlike 'CodexProviderTransaction-*') {
        throw '事务清理路径不安全。'
    }
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}

function New-GuardCommand {
    $guard = (Join-Path $script:ToolDir 'ProviderGuard.exe').Replace("'","''")
    $homeText = $script:CodexDir.Replace("'","''")
    return "& '$guard' --home '$homeText'"
}
function Is-OurHook($Handler) {
    $command = [string](Get-Prop $Handler 'command' '')
    $guard = (Join-Path $script:ToolDir 'ProviderGuard.exe')
    return ($command -eq (New-GuardCommand) -or $command.IndexOf($guard,[StringComparison]::OrdinalIgnoreCase) -ge 0)
}
function Get-HookDocument {
    $document = Read-Json $script:HooksPath
    if ($null -eq $document) { $document = [pscustomobject]@{ hooks = [pscustomobject]@{} } }
    if ($null -eq (Get-Prop $document 'hooks')) { Set-Prop $document 'hooks' ([pscustomobject]@{}) }
    return $document
}
function Install-HookHandler {
    $document = Get-HookDocument
    $groups = @(Get-Prop $document.hooks 'UserPromptSubmit' @())
    $foundHooks = @()
    for ($g=0; $g -lt $groups.Count; $g++) {
        $handlers = @($groups[$g].hooks)
        for ($h=0; $h -lt $handlers.Count; $h++) {
            if (Is-OurHook $handlers[$h]) { $foundHooks += ,([pscustomobject]@{ Group=$g; Handler=$h }) }
        }
    }
    if ($foundHooks.Count -gt 1) { throw '检测到重复的模型检查 Hook，请先手动清理后再安装。' }
    $handler = [pscustomobject]@{
        type = 'command'
        command = (New-GuardCommand)
        timeout = 3
        statusMessage = '检查对话与当前模型是否一致'
    }
    if ($foundHooks.Count -eq 1) {
        $handlers = @($groups[$foundHooks[0].Group].hooks)
        $handlers[$foundHooks[0].Handler] = $handler
        $groups[$foundHooks[0].Group].hooks = @($handlers)
    } else {
        $groups += ,([pscustomobject]@{ hooks = @($handler) })
    }
    Set-Prop $document.hooks 'UserPromptSubmit' @($groups)
    Write-Json $script:HooksPath $document
}
function Remove-HookHandler {
    if (-not (Test-Path -LiteralPath $script:HooksPath)) { return }
    $document = Read-Json $script:HooksPath
    if ($null -eq $document -or $null -eq (Get-Prop $document 'hooks')) { return }
    $configText = Read-ConfigText
    $configText = [ConfigEditor]::RemoveGuardBlock($configText)
    foreach ($event in @($document.hooks.PSObject.Properties)) {
        $groups = @($event.Value)
        $slug = [regex]::Replace($event.Name,'(?<!^)([A-Z])','_$1').ToLowerInvariant()
        for ($g=$groups.Count-1; $g -ge 0; $g--) {
            $handlers = @($groups[$g].hooks)
            $removed = $false
            for ($h=$handlers.Count-1; $h -ge 0; $h--) {
                if (Is-OurHook $handlers[$h]) {
                    $key = $script:HooksPath + ':' + $slug + ':' + $g + ':' + $h
                    $configText = [ConfigEditor]::RemoveTrust($configText,$key)
                    $removed = $true
                    $handlers = @(for ($i=0; $i -lt $handlers.Count; $i++) { if ($i -ne $h) { $handlers[$i] } })
                    if ($handlers.Count) { $configText = [ConfigEditor]::RemapTrust($configText,$script:HooksPath,$slug,$g,$h,$false) }
                }
            }
            if ($handlers.Count) {
                $groups[$g].hooks = @($handlers)
            } elseif ($removed) {
                $groups = @(for ($i=0; $i -lt $groups.Count; $i++) { if ($i -ne $g) { $groups[$i] } })
                $configText = [ConfigEditor]::RemapTrust($configText,$script:HooksPath,$slug,$g,0,$true)
            }
        }
        if ($groups.Count) { Set-Prop $document.hooks $event.Name @($groups) }
        else { $document.hooks.PSObject.Properties.Remove($event.Name) }
    }
    Write-Text $script:ConfigPath $configText
    if (@($document.hooks.PSObject.Properties).Count -eq 0 -and @($document.PSObject.Properties).Count -eq 1) {
        Remove-Item -LiteralPath $script:HooksPath -Force
    } else {
        Write-Json $script:HooksPath $document
    }
}
function Get-HookInfo {
    $cli = Find-Cli
    $json = [ConfigEditor]::HookInfo($cli,$script:CodexDir,(New-GuardCommand))
    return ($json | ConvertFrom-Json)
}
function Install-HookTrust {
    $configText = Read-ConfigText
    $hook = Get-HookInfo
    if ($hook.trustStatus -ne 'trusted' -and $hook.trustStatus -ne 'managed') {
        if ([string]::IsNullOrWhiteSpace([string]$hook.currentHash) -or [string]::IsNullOrWhiteSpace([string]$hook.key)) {
            throw '客户端未返回模型检查的信任信息。'
        }
        $configText = [ConfigEditor]::AddTrust($configText,[string]$hook.key,[string]$hook.currentHash)
        Write-Text $script:ConfigPath $configText
        $hook = Get-HookInfo
    }
    if (-not $hook.enabled) { throw 'Codex 已禁用模型检查 Hook。' }
    if ($hook.trustStatus -ne 'trusted' -and $hook.trustStatus -ne 'managed') {
        throw ('模型检查 Hook 未受信任：' + $hook.trustStatus)
    }
    return $hook
}

function Set-LocalShortcut([string]$Path,[string]$Mode) {
    $exe = Join-Path $script:ToolDir 'Switch-CodexProvider.exe'
    $shell = New-Object -ComObject WScript.Shell
    try {
        if (Test-Path -LiteralPath $Path) {
            $existing = $shell.CreateShortcut($Path)
            if ($existing.TargetPath -and $existing.TargetPath -ne $exe -and -not $existing.TargetPath.StartsWith($script:ToolDir + '\',[StringComparison]::OrdinalIgnoreCase)) {
                throw "快捷方式被其他程序占用：$Path"
            }
        }
        $link = $shell.CreateShortcut($Path)
        $link.TargetPath = $exe
        $link.Arguments = '-Mode ' + $Mode + ' -Directory "' + $script:CodexDir + '"'
        $link.WorkingDirectory = $script:ToolDir
        $link.IconLocation = (Join-Path $script:ToolDir ('icons\Codex-' + $Mode + '.ico')) + ',0'
        $link.WindowStyle = 1
        $link.Description = 'Select model configuration; restart Codex manually after switching.'
        $link.Save()
    } finally {
        if ($link) { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) | Out-Null }
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
    }
}
function Install-LocalShortcuts {
    Set-LocalShortcut (Join-Path $script:ToolDir 'Codex - ChatGPT.lnk') 'OpenAI'
    Set-LocalShortcut (Join-Path $script:ToolDir 'Codex - DeepSeek.lnk') 'DeepSeek'
}
function Test-ManagedShortcut([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    $shell = New-Object -ComObject WScript.Shell
    try {
        $link = $shell.CreateShortcut($Path)
        $exe = Join-Path $script:ToolDir 'Switch-CodexProvider.exe'
        $expected = '^-Mode (OpenAI|DeepSeek) -Directory "' + [regex]::Escape($script:CodexDir) + '"$'
        return ($link.TargetPath -eq $exe -and $link.Arguments -match $expected)
    } catch { return $false }
    finally {
        if ($link) { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) | Out-Null }
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
    }
}
function Get-ManagedShortcutPaths {
    $paths = @()
    foreach ($name in @('Codex - ChatGPT.lnk','Codex - DeepSeek.lnk')) {
        $local = Join-Path $script:ToolDir $name
        if (Test-Path -LiteralPath $local) { $paths += $local }
    }
    foreach ($directory in @($script:TaskbarDir,$script:DesktopDir)) {
        if (-not (Test-Path -LiteralPath $directory)) { continue }
        foreach ($file in @(Get-ChildItem -LiteralPath $directory -Filter '*.lnk' -File -ErrorAction SilentlyContinue)) {
            if (Test-ManagedShortcut $file.FullName) { $paths += $file.FullName }
        }
    }
    return @($paths | Select-Object -Unique)
}
function Remove-ManagedShortcuts {
    foreach ($path in Get-ManagedShortcutPaths) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force
            try { [ConfigEditor]::ShortcutDeleted($path) } catch {}
        }
    }
}

function Get-KeyStatePath { return Join-Path $script:ToolDir 'key-state.json' }
function Get-UserKey {
    if ($script:IsIsolated) {
        $state = Read-Json (Join-Path $script:IsolatedRoot 'environment.json')
        return [string](Get-Prop $state 'DEEPSEEK_API_KEY' '')
    }
    return [string][Environment]::GetEnvironmentVariable('DEEPSEEK_API_KEY','User')
}
function Set-UserKey([string]$Value) {
    if ($script:IsIsolated) {
        $path = Join-Path $script:IsolatedRoot 'environment.json'
        $state = Read-Json $path
        if ($null -eq $state) { $state = [pscustomobject]@{} }
        if ($null -eq $Value) { $state.PSObject.Properties.Remove('DEEPSEEK_API_KEY') }
        else { Set-Prop $state 'DEEPSEEK_API_KEY' $Value }
        Write-Json $path $state
    } else {
        [Environment]::SetEnvironmentVariable('DEEPSEEK_API_KEY',$Value,'User')
        [Environment]::SetEnvironmentVariable('DEEPSEEK_API_KEY',$Value,'Process')
        try { [ConfigEditor]::EnvironmentChanged() } catch {}
    }
}
function Set-DeepSeekKey {
    $secure = $null
    $pointer = [IntPtr]::Zero
    $value = $null
    try {
        if ($ReadKeyFromStdin) {
            $value = [Console]::In.ReadLine()
        } else {
            $secure = Read-Host '请输入你自己的 DeepSeek API Key（输入隐藏）' -AsSecureString
            $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
            $value = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
        }
        if ([string]::IsNullOrWhiteSpace($value) -or -not $value.StartsWith('sk-')) { throw 'Key 格式不正确，未保存。' }
        $statePath = Get-KeyStatePath
        $state = Read-Json $statePath
        if ($null -eq $state) {
            $old = Get-UserKey
            $protected = $null
            if (-not [string]::IsNullOrWhiteSpace($old)) {
                $protected = [Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect($script:Utf8.GetBytes($old),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser))
            }
            $state = [pscustomobject]@{
                hadOriginal = -not [string]::IsNullOrWhiteSpace($old)
                originalProtected = $protected
                ownedHash = $null
            }
        }
        $state.ownedHash = Hash-Bytes ($script:Utf8.GetBytes($value))
        Write-Json $statePath $state
        Set-UserKey $value
        Write-Host 'DeepSeek Key 已保存到当前 Windows 用户环境。' -ForegroundColor Green
    } finally {
        if ($pointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
        $value = $null
        if ($secure) { $secure.Dispose() }
    }
}
function Restore-DeepSeekKey {
    $state = Read-Json (Get-KeyStatePath)
    $current = Get-UserKey
    if ($RemoveSharedKey) { Set-UserKey $null; return }
    if ($null -eq $state -or [string]::IsNullOrWhiteSpace($current)) { return }
    if ((Hash-Bytes ($script:Utf8.GetBytes($current))) -ne $state.ownedHash) { return }
    $original = $null
    if ($state.hadOriginal -and $state.originalProtected) {
        $plain = [Security.Cryptography.ProtectedData]::Unprotect(
            [Convert]::FromBase64String($state.originalProtected),
            $null,
            [Security.Cryptography.DataProtectionScope]::CurrentUser
        )
        $original = $script:Utf8.GetString($plain)
    }
    Set-UserKey $original
}

function Cleanup-History {
    if (-not (Test-Path -LiteralPath $script:ToolDir)) { return }
    $backup = Join-Path $script:ToolDir 'backup'
    if (Test-Path -LiteralPath $backup) {
        foreach ($spec in @(
            @('config.before-*.toml',10),
            @('openai-state-*.toml',5),
            @('install.before-*.json',3)
        )) {
            $files = @(Get-ChildItem -LiteralPath $backup -Filter $spec[0] -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending)
            for ($i=[int]$spec[1]; $i -lt $files.Count; $i++) { Remove-Item -LiteralPath $files[$i].FullName -Force }
        }
        foreach ($file in @(Get-ChildItem -LiteralPath $backup -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '\.(tmp|next|switch-[0-9a-f]+)$' })) {
            Remove-Item -LiteralPath $file.FullName -Force
        }
        foreach ($directory in @(Get-ChildItem -LiteralPath $backup -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'upgrade-*' })) {
            Remove-Item -LiteralPath $directory.FullName -Recurse -Force
        }
    }
    $tmp = Join-Path $script:ToolDir 'tmp'
    if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Recurse -Force }
    foreach ($name in @(
        'Repair-Shortcuts.cmd','Repair-Shortcuts.ps1',
        'Set-DeepSeekApiKey.cmd','Set-DeepSeekApiKey.ps1',
        'Uninstall.cmd','Uninstall.ps1',
        'icon-sync.log.1','icon-sync.log.2',
        'FastConsoleSwitch.exe','CodexEnvironmentSwitch.exe','Switch-CodexEnvironment.ps1'
    )) {
        $path = Join-Path $script:ToolDir $name
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    }
    $buttons = Join-Path $script:ToolDir 'buttons'
    if (Test-Path -LiteralPath $buttons) {
        $others = @()
        foreach ($file in @(Get-ChildItem -LiteralPath $buttons -File -Force -ErrorAction SilentlyContinue)) {
            if (-not (Test-ManagedShortcut $file.FullName)) { $others += $file.FullName }
        }
        if ($others.Count -eq 0) { Remove-Item -LiteralPath $buttons -Recurse -Force }
    }
    $log = Join-Path $script:ToolDir 'icon-sync.log'
    if (Test-Path -LiteralPath $log) {
        $lines = @(Get-Content -LiteralPath $log -Tail 200 -Encoding UTF8)
        Write-Text $log (($lines -join [Environment]::NewLine) + [Environment]::NewLine)
    }
}
function Write-InstallManifest([string]$OriginalConfigHash) {
    $shortcuts = @(Get-ManagedShortcutPaths)
    $manifest = [ordered]@{
        version = $script:Version
        codexDir = $script:CodexDir
        shortcuts = $shortcuts
        hadManagedProvider = $false
        originalConfigHash = $OriginalConfigHash
        installedAt = (Get-Date).ToString('o')
    }
    Write-Json $script:ManifestPath $manifest
    return $manifest
}
function Write-GuardManifest($Hook) {
    $stamp = [ordered]@{
        version = $script:Version
        trustedHash = [string]$Hook.currentHash
        guardSha256 = Hash-File (Join-Path $script:ToolDir 'ProviderGuard.exe')
        hooksSha256 = Hash-File $script:HooksPath
        backup = $null
    }
    Write-Json (Join-Path $script:ToolDir 'guard-install.json') $stamp
}

function Install-OrUpdate {
    Assert-SafePaths
    Assert-Idle
    Verify-Package
    if (-not (Test-Path -LiteralPath $script:ConfigPath)) {
        throw "未找到 $script:ConfigPath。请先启动并登录 Codex 桌面端。"
    }
    $originalConfigHash = Hash-File $script:ConfigPath
    $toolExisted = Test-Path -LiteralPath $script:ToolDir
    $snapshot = New-Transaction
    try {
        New-Item -ItemType Directory -Force -Path $script:ToolDir | Out-Null
        New-Item -ItemType Directory -Force -Path (Join-Path $script:ToolDir 'icons') | Out-Null
        $payloadTool = Join-Path $PackageRoot 'tool'
        foreach ($file in @(Get-ChildItem -LiteralPath $payloadTool -File -Force)) {
            Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $script:ToolDir $file.Name) -Force
        }
        foreach ($icon in @(Get-ChildItem -LiteralPath (Join-Path $payloadTool 'icons') -File)) {
            Copy-Item -LiteralPath $icon.FullName -Destination (Join-Path $script:ToolDir 'icons') -Force
        }
        Load-Editor

        $configText = Read-ConfigText
        try { [ConfigEditor]::Validate($configText) }
        catch {
            if (-not $toolExisted) { throw ('现有配置含未完成的切换标记，已停止：' + $_.Exception.Message) }
        }
        $hasActive = $configText -match '# BEGIN CODEX DEEPSEEK SWITCH \(managed\)'
        $hasProvider = $configText -match '# BEGIN CODEX DEEPSEEK PROVIDER \(managed, always available\)'
        if ($hasActive -and -not (Test-Path -LiteralPath (Join-Path $script:ToolDir 'openai-state.txt'))) {
            throw '当前配置处于 DeepSeek 托管状态，但缺少 GPT 恢复数据。请先修复旧安装。'
        }
        if (-not $hasProvider) {
            $configText = $configText + [ConfigEditor]::ProviderBlock($configText)
            [ConfigEditor]::Validate($configText)
            Write-Text $script:ConfigPath $configText
        }

        Install-LocalShortcuts
        if (-not $toolExisted) {
            New-Item -ItemType Directory -Force -Path $script:DesktopDir | Out-Null
            Set-LocalShortcut (Join-Path $script:DesktopDir 'Codex - ChatGPT.lnk') 'OpenAI'
            Set-LocalShortcut (Join-Path $script:DesktopDir 'Codex - DeepSeek.lnk') 'DeepSeek'
        }
        Install-HookHandler
        $hook = Install-HookTrust
        $manifest = Write-InstallManifest $originalConfigHash
        Write-GuardManifest $hook
        Invoke-Tool 'Switch-CodexProvider.exe' @('-Mode','Check','-Quiet','-Directory',$script:CodexDir) | Out-Null
        Invoke-Tool 'Switch-CodexProvider.exe' @('-Mode','SyncIcons','-Quiet','-LocalIconsOnly','-Directory',$script:CodexDir) | Out-Null
        Cleanup-History
        Remove-Transaction $snapshot
        Write-Host ''
        Write-Host ("安装/更新完成：$script:Version") -ForegroundColor Green
        Write-Host ("安装目录：$script:ToolDir")
        Write-Host ("当前配置提供商：$(Config-Provider)")
        if ([string]::IsNullOrWhiteSpace((Get-UserKey))) {
            Write-Host '尚未设置 DeepSeek Key，请返回菜单选择“设置 DeepSeek Key”。' -ForegroundColor Yellow
        }
        Write-Host '可从 provider-switch 目录固定两个切换快捷方式到任务栏。'
    } catch {
        try { Restore-Transaction $snapshot } catch { Write-Host ('回滚失败：' + $_.Exception.Message) -ForegroundColor Red }
        throw
    }
}

function Uninstall-ProviderSwitch {
    Assert-SafePaths
    Assert-Idle
    if (-not (Test-Path -LiteralPath $script:ToolDir)) {
        Write-Host '未检测到已安装工具。' -ForegroundColor Yellow
        return
    }
    $snapshot = New-Transaction
    try {
        Load-Editor
        $manifest = Read-Json $script:ManifestPath
        if ($null -ne $manifest) {
            $manifest.hadManagedProvider = $false
            Write-Json $script:ManifestPath $manifest
        }
        Invoke-Tool 'Switch-CodexProvider.exe' @('-Mode','Uninstall','-Quiet','-Directory',$script:CodexDir) | Out-Null
        Remove-HookHandler
        Restore-DeepSeekKey
        Remove-ManagedShortcuts
        $configText = Read-ConfigText
        if ($configText -match '# BEGIN CODEX DEEPSEEK (SWITCH|PROVIDER) \(managed') {
            throw '配置仍含托管 DeepSeek 区块，未删除工具目录。'
        }
        if (-not $KeepData) {
            if (Test-Path -LiteralPath $script:ToolDir) { Remove-Item -LiteralPath $script:ToolDir -Recurse -Force }
        }
        Remove-Transaction $snapshot
        Write-Host ''
        Write-Host '卸载完成，Codex 配置、登录和聊天记录未删除。' -ForegroundColor Green
        if ($KeepData) { Write-Host '按参数要求保留了 provider-switch 数据目录。' }
    } catch {
        try { Restore-Transaction $snapshot } catch { Write-Host ('回滚失败：' + $_.Exception.Message) -ForegroundColor Red }
        throw
    }
}

function Show-Status {
    $manifest = Read-Json $script:ManifestPath
    $provider = if (Test-Path -LiteralPath $script:ConfigPath) { Config-Provider } else { '未找到配置' }
    Write-Host ''
    Write-Host 'Codex 双模型切换器状态' -ForegroundColor Cyan
    Write-Host ("安装版本：$(Get-Prop $manifest 'version' '未安装')")
    Write-Host ("安装目录：$script:ToolDir")
    Write-Host ("当前提供商：$provider")
    Write-Host ("DeepSeek Key：$(if ([string]::IsNullOrWhiteSpace((Get-UserKey))) { '未设置' } else { '已设置' })")
    if (Test-Path -LiteralPath (Join-Path $script:ToolDir 'ProviderGuard.exe')) {
        try {
            Load-Editor
            $status = Invoke-Tool 'ProviderGuard.exe' @('--home',$script:CodexDir,'--status')
            Write-Host ("会话归属记录：$status")
            $hook = Get-HookInfo
            Write-Host ("发送前检查：enabled=$($hook.enabled), trust=$($hook.trustStatus)")
        } catch {
            Write-Host ('状态检查失败：' + $_.Exception.Message) -ForegroundColor Yellow
        }
    }
    $shortcuts = @(Get-ManagedShortcutPaths)
    Write-Host ("托管快捷方式数量：$($shortcuts.Count)")
}

function Invoke-Action([string]$Name) {
    switch ($Name) {
        'Install' { Install-OrUpdate }
        'Update'  { Install-OrUpdate }
        'Uninstall' { Uninstall-ProviderSwitch }
        'SetKey' { Set-DeepSeekKey }
        'Status' { Show-Status }
        'Cleanup' { Cleanup-History; Write-Host '历史备份和无效临时文件已清理。' -ForegroundColor Green }
        default { throw "未知操作：$Name" }
    }
}

try {
    if ($Action -eq 'Menu') {
        while ($true) {
            Write-Host ''
            Write-Host '============================================' -ForegroundColor DarkCyan
            Write-Host ("  Codex / DeepSeek 双模型切换器 " + $script:Version + "（小洛同学）") -ForegroundColor Cyan
            Write-Host '============================================' -ForegroundColor DarkCyan
            Write-Host '  1. 安装 / 更新'
            Write-Host '  2. 设置 DeepSeek API Key'
            Write-Host '  3. 查看状态'
            Write-Host '  4. 清理历史与无效文件'
            Write-Host '  5. 干净卸载'
            Write-Host '  Q. 退出'
            $choice = Read-Host '请选择'
            if ($choice -match '^[Qq]$') { break }
            try {
                switch ($choice) {
                    '1' { Invoke-Action 'Install' }
                    '2' { Invoke-Action 'SetKey' }
                    '3' { Invoke-Action 'Status' }
                    '4' { Invoke-Action 'Cleanup' }
                    '5' {
                        $confirm = Read-Host '将删除本工具文件、数据库和快捷方式，但保留 Codex 登录与聊天记录。输入 YES 继续'
                        if ($confirm -ceq 'YES') { Invoke-Action 'Uninstall' }
                        else { Write-Host '已取消卸载。' -ForegroundColor Yellow }
                    }
                    default { Write-Host '无效选项。' -ForegroundColor Yellow }
                }
            } catch {
                Write-Host ('操作失败：' + $_.Exception.Message) -ForegroundColor Red
            }
            Read-Host '按 Enter 返回菜单' | Out-Null
        }
    } else {
        Invoke-Action $Action
    }
} catch {
    Write-Host ('操作失败：' + $_.Exception.Message) -ForegroundColor Red
    if ($Action -eq 'Menu') { Read-Host '按 Enter 退出' | Out-Null }
    throw
}
