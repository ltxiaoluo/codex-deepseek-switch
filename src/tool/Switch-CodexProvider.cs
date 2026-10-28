using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;
using System.Runtime.InteropServices;

// Conservative TOML editing, not general validation. Unsupported owned fields
// are rejected before any state/config write. Other values are preserved verbatim.
internal static class SwitchCodexProvider
{
    const string Begin = "# BEGIN CODEX DEEPSEEK SWITCH (managed)";
    const string End = "# END CODEX DEEPSEEK SWITCH (managed)";
    const string ProviderBegin = "# BEGIN CODEX DEEPSEEK PROVIDER (managed, always available)";
    const string ProviderEnd = "# END CODEX DEEPSEEK PROVIDER (managed, always available)";
    static readonly HashSet<string> Owned = new HashSet<string>(new string[] {
        "model", "model_provider", "preferred_auth_method", "forced_login_method",
        "model_reasoning_effort", "web_search", "model_catalog_json" });
    static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
    sealed class Item {
        public int Start, End; public string Kind, Key, Value; public string[] Path; public bool Root, IsArray;
    }
    sealed class Document {
        public string Text; public int TopEnd;
        public List<Item> Items = new List<Item>();
        public Dictionary<string, Item> Fields = new Dictionary<string, Item>();
    }
    sealed class Block { public int Start, End; }

    [STAThread]
    static int Main(string[] args)
    {
        bool quiet = Array.IndexOf(args, "-Quiet") >= 0;
        bool localIconsOnly = Array.IndexOf(args, "-LocalIconsOnly") >= 0;
        try {
            Console.InputEncoding = Utf8;
            Console.OutputEncoding = Utf8;
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), Utf8) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), Utf8) { AutoFlush = true });
            if (!quiet && (Array.IndexOf(args,"OpenAI") >= 0 || Array.IndexOf(args,"DeepSeek") >= 0)) Console.WriteLine("正在保存模型配置…");
            string mode = null, directory = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (String.IsNullOrWhiteSpace(directory)) directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            for (int i = 0; i < args.Length; i++) {
                if (args[i] == "-Quiet" || args[i] == "-LocalIconsOnly") continue;
                if (i + 1 == args.Length) Fail("缺少参数值。");
                string name = args[i], value = args[++i];
                if (name == "-Mode") mode = value;
                else if (name == "-Directory") directory = value;
                else Fail("未知参数：" + name);
            }
            if (mode != "OpenAI" && mode != "DeepSeek" && mode != "Validate" && mode != "Check" && mode != "SyncIcons" && mode != "RefreshStatus" && mode != "Uninstall") Fail("不支持的操作。");
            directory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
            string token;
            using (SHA256 hash = SHA256.Create()) token = BitConverter.ToString(hash.ComputeHash(Utf8.GetBytes(directory.ToUpperInvariant()))).Replace("-", "");
            using (Mutex mutex = new Mutex(false, "Local\\CodexProviderSwitch_" + token)) {
                bool locked = false;
                try {
                    try { locked = mutex.WaitOne(0); } catch (AbandonedMutexException) { locked = true; }
                    if (!locked) Fail("另一个切换操作正在进行，请稍后重试。");
                    if (mode == "OpenAI" || mode == "DeepSeek") VerifyGuard(directory);
                    string result = Switch(directory, (mode == "SyncIcons" || mode == "RefreshStatus") ? "Check" : mode);
                    UpdateDesktopWorkMode(directory, mode);
                    bool routingChanged = UpdateDesktopRouting(directory, mode);
                    // Explorer and newly activated packaged apps cache the user
                    // environment. Publish it before any icon-cache work or early
                    // client launch, otherwise the first launch can inherit the
                    // previous provider's route and exit during startup.
                    if (routingChanged) BroadcastEnvironment();
                    Console.WriteLine(result);
                    if (!quiet && (mode == "OpenAI" || mode == "DeepSeek")) ReleaseOwnConsole();
                    if (mode == "OpenAI" || mode == "DeepSeek" || mode == "SyncIcons" || mode == "RefreshStatus" || mode == "Uninstall") {
                        try {
                            if (!quiet && (mode == "OpenAI" || mode == "DeepSeek")) {
                                // Repaint the shortcuts in this process: spawning the
                                // helper first adds a full CLR start before the mark appears.
                                // The synchronous cache refresh must finish before this
                                // short-lived process exits; no delayed shell notification
                                // is left behind to interfere with the next app launch.
                                try {
                                    string selectedProvider = Get(Parse(Decode(Bytes(Path.Combine(directory, "config.toml")))), "model_provider");
                                    ProviderIcons.Sync(directory, selectedProvider == "deepseek" ? "DeepSeek" : "OpenAI", false, false, !localIconsOnly);
                                    if (!localIconsOnly) ProviderIcons.RefreshShell();
                                } catch (Exception) {
                                    // Only the failure path uses the detached helper,
                                    // preserving its repair and retry behavior.
                                    StartIconWorker(directory, localIconsOnly, false, false);
                                }
                                return 0;
                            }
                            string provider = Get(Parse(Decode(Bytes(Path.Combine(directory, "config.toml")))), "model_provider");
                            ProviderIcons.Sync(directory, provider == "deepseek" ? "DeepSeek" : "OpenAI", mode == "Uninstall", mode == "SyncIcons", !localIconsOnly);
                        } catch (Exception iconError) {
                            Console.Error.WriteLine("Configuration is saved, but status icon refresh failed: " + iconError.Message);
                            if (!quiet) ShowError("配置已保存，但图标更新失败。请再次点击同一目标按钮重试。\n\n" + iconError.Message, "Codex 图标更新失败");
                            return 2;
                        }
                    }
                } finally { if (locked) mutex.ReleaseMutex(); }
            }
            return 0;
        } catch (Exception e) {
            Console.Error.WriteLine(e.Message);
            if (!quiet) ShowError(e.Message, "Codex 配置切换失败");
            return 1;
        }
    }
    // Close only the private console opened by a shortcut. Keep shared terminals
    // and redirected output intact. Icon work stays synchronous under the mutex.
    static void ReleaseOwnConsole()
    {
        uint consoleMode;
        if (!GetConsoleMode(GetStdHandle(-11), out consoleMode)) return;
        uint[] processes = new uint[2];
        if (GetConsoleProcessList(processes, (uint)processes.Length) != 1) return;
        Console.Out.Flush(); Console.Error.Flush();
        if (!FreeConsole()) return;
        Console.SetOut(TextWriter.Null); Console.SetError(TextWriter.Null);
    }
    [DllImport("kernel32.dll", SetLastError=true)]
    static extern uint GetConsoleProcessList([Out] uint[] processes, uint count);
    [DllImport("kernel32.dll", SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    static extern bool FreeConsole();
    [DllImport("kernel32.dll")]
    static extern IntPtr GetStdHandle(int id);
    [DllImport("kernel32.dll", SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    static extern bool GetConsoleMode(IntPtr handle, out uint mode);

    static void StartIconWorker(string directory, bool localOnly, bool broadcastEnvironment, bool iconsDone)
    {
        string tool = Path.Combine(directory, "provider-switch");
        if (!Directory.Exists(Path.Combine(tool, "icons"))) return;
        string worker = Path.Combine(tool, "Refresh-CodexProviderIcons.exe");
        if (!File.Exists(worker)) throw new FileNotFoundException("Missing icon refresh helper. Please update the tool.", worker);
        StringBuilder command = new StringBuilder("\"" + worker + "\" -Directory \"" + directory + "\"" + (localOnly ? " -LocalIconsOnly" : "") + (broadcastEnvironment ? " -Broadcast" : "") + (iconsDone ? " -IconsDone" : ""));
        StartupInfo startup = new StartupInfo();
        startup.Size = Marshal.SizeOf(typeof(StartupInfo));
        startup.Flags = 1; startup.ShowWindow = 0;
        ProcessInfo process;
        // No inherited handles: the existing GUI helper cannot keep the switch
        // console/output pipes alive. Its refresh logic and arguments are unchanged.
        if (!CreateProcess(worker, command, IntPtr.Zero, IntPtr.Zero, false, 0x08000000,
            IntPtr.Zero, null, ref startup, out process))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        CloseHandle(process.Thread); CloseHandle(process.Process);
    }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    struct StartupInfo {
        public int Size; public string Reserved, Desktop, Title;
        public int X, Y, Width, Height, XChars, YChars, Fill, Flags;
        public short ShowWindow, ReservedSize;
        public IntPtr ReservedData, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInfo { public IntPtr Process, Thread; public uint Id, ThreadId; }
    [DllImport("kernel32.dll", EntryPoint="CreateProcessW", CharSet=CharSet.Unicode, SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags,
        IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    internal static void RefreshIconsInWorker(string directory, bool localOnly, bool broadcastEnvironment, bool iconsDone)
    {
        directory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        string token;
        using (SHA256 hash = SHA256.Create()) token = BitConverter.ToString(hash.ComputeHash(Utf8.GetBytes(directory.ToUpperInvariant()))).Replace("-", "");
        using (Mutex mutex = new Mutex(false, "Local\\CodexProviderSwitch_" + token)) {
            bool locked = false;
            try {
                try { locked = mutex.WaitOne(10000); } catch (AbandonedMutexException) { locked = true; }
                if (!locked) Fail("图标刷新等待其他切换操作超时，请重试。");
                if (!iconsDone) {
                    Switch(directory, "Check");
                    // Read after obtaining the lock, so rapid clicks never restore
                    // an older selection merely because its worker started late.
                    string provider = Get(Parse(Decode(Bytes(Path.Combine(directory, "config.toml")))), "model_provider");
                    ProviderIcons.Sync(directory, provider == "deepseek" ? "DeepSeek" : "OpenAI", false, false, !localOnly);
                }
            } finally { if (locked) mutex.ReleaseMutex(); }
        }
        // The cache refresh and the environment broadcast do not need the lock;
        // keeping them outside lets the next click start immediately.
        if (!localOnly && !iconsDone) {
            ProviderIcons.RefreshShell();
            // The taskbar can process one of two link updates after its cache
            // notification. A second notification is needed after those messages.
            // Never perform that retry while the client is creating its windows.
            Thread.Sleep(150);
            DateTime deadline=DateTime.UtcNow.AddSeconds(15);
            bool starting;
            do {
                starting=false;
                foreach(Process client in Process.GetProcessesByName("ChatGPT")) {
                    using(client) try {
                        string path=client.MainModule.FileName;
                        if(path.IndexOf("OpenAI.Codex_",StringComparison.OrdinalIgnoreCase)>=0 &&
                            DateTime.Now-client.StartTime<TimeSpan.FromSeconds(3)) { starting=true; break; }
                    } catch(InvalidOperationException) {} catch(System.ComponentModel.Win32Exception) {}
                }
                if(starting) Thread.Sleep(150);
            } while(starting && DateTime.UtcNow<deadline);
            if(!starting) ProviderIcons.RefreshShell();
        }
        if (broadcastEnvironment) BroadcastEnvironment();
    }
    static void ValidateDeepPreference(string model, string effort, string tool) {
        string catalog = File.ReadAllText(Path.Combine(tool,"deepseek-models.json"));
        if (String.IsNullOrEmpty(model) || !Regex.IsMatch(catalog, "\"slug\"\\s*:\\s*\"" + Regex.Escape(model) + "\"") ||
            (effort != "low" && effort != "high" && effort != "max"))
            Fail("DeepSeek 模型或推理档位不在已安装目录中，未覆盖设置。");
    }
    static void VerifyGuard(string directory) {
        // Isolated fixtures use the same guard requirements as the installed tool.
        string tool = Path.Combine(directory, "provider-switch");
        foreach (string name in new string[] { "ProviderGuard.exe", "guard-install.json" })
            if (!File.Exists(Path.Combine(tool, name))) Fail("缺少发送前检查组件，请重新安装修复版。未切换配置。");
        string stamp = File.ReadAllText(Path.Combine(tool, "guard-install.json"));
        if (JsonText(stamp,"guardSha256") != Digest(Bytes(Path.Combine(tool,"ProviderGuard.exe"))) ||
            JsonText(stamp,"hooksSha256") != Digest(Bytes(Path.Combine(directory,"hooks.json"))))
            Fail("发送前检查文件已改变，请重新安装修复版。未切换配置。");
        string hash = JsonText(stamp, "trustedHash");
        if (String.IsNullOrEmpty(hash) || !File.ReadAllText(Path.Combine(directory,"config.toml")).Contains(hash))
            Fail("发送前检查尚未受信任，请完成修复安装。未切换配置。");
    }
    [DllImport("user32.dll", CharSet=CharSet.Unicode)]
    static extern int MessageBox(IntPtr owner, string text, string title, uint flags);
    static void ShowError(string text, string title) { MessageBox(IntPtr.Zero,text,title,0x10010); }
    static void Fail(string message) { throw new InvalidOperationException(message); }
    const string DesktopRouteVariable = "CODEX_APP_SERVER_FORCE_CLI";
    internal sealed class DesktopRouteState {
        public bool active { get; set; }
        public string originalUserValue { get; set; }
    }
    internal sealed class DesktopRoutePlan {
        public DesktopRouteState State;
        public bool WriteEnvironment;
        public string Value;
    }
    internal static DesktopRoutePlan PlanDesktopRouting(string mode, string current, DesktopRouteState previous)
    {
        if (mode == "DeepSeek" || mode == "OpenAI") {
            return new DesktopRoutePlan {
                State = new DesktopRouteState { active = true, originalUserValue = previous != null && previous.active ? previous.originalUserValue : current },
                WriteEnvironment = current != "1", Value = "1"
            };
        }
        if (previous == null || !previous.active) return new DesktopRoutePlan { State = previous };
        return new DesktopRoutePlan {
            State = new DesktopRouteState { active = false, originalUserValue = previous.originalUserValue },
            WriteEnvironment = current == "1" && current != previous.originalUserValue,
            Value = previous.originalUserValue
        };
    }
    internal sealed class DesktopWorkModeState {
        public bool active { get; set; }
        public bool hadOriginal { get; set; }
        public string originalEntry { get; set; }
    }
    internal sealed class DesktopWorkModePlan {
        public string Config;
        public DesktopWorkModeState State;
    }
    internal static DesktopWorkModePlan PlanDesktopWorkMode(string config, string mode, DesktopWorkModeState previous)
    {
        Document d = Parse(config); Item desktopTable = null, detail = null; bool inDesktop = false;
        foreach (Item item in d.Items) {
            if (item.Kind == "table") {
                inDesktop = !item.IsArray && item.Path.Length == 1 && item.Path[0] == "desktop";
                if (inDesktop) {
                    if (desktopTable != null) Fail("[desktop] 配置表重复，未作修改。");
                    desktopTable = item;
                }
            } else if (inDesktop && item.Kind == "field" && item.Path.Length == 1 && item.Path[0] == "conversationDetailMode") {
                if (detail != null) Fail("conversationDetailMode 配置重复，未作修改。");
                detail = item;
            }
        }
        string current = detail == null ? null : ReadString(detail.Value);
        if (mode == "DeepSeek") {
            DesktopWorkModeState state = previous != null && previous.active ? previous : new DesktopWorkModeState {
                active = true,
                hadOriginal = detail != null,
                originalEntry = detail == null ? null : config.Substring(detail.Start, detail.End - detail.Start)
            };
            state.active = true;
            if (current == "STEPS_COMMANDS") return new DesktopWorkModePlan { Config = config, State = state };
            string nl = NewLine(config);
            string entry = "conversationDetailMode = \"STEPS_COMMANDS\"" + nl;
            string next;
            if (detail != null) next = config.Remove(detail.Start, detail.End - detail.Start).Insert(detail.Start, entry);
            else if (desktopTable != null) next = config.Insert(desktopTable.End, entry);
            else next = config + (config.Length > 0 && !config.EndsWith("\n") ? nl : "") + "[desktop]" + nl + entry;
            return new DesktopWorkModePlan { Config = next, State = state };
        }
        if ((mode != "OpenAI" && mode != "Uninstall") || previous == null || !previous.active)
            return new DesktopWorkModePlan { Config = config, State = previous };
        string restored = config;
        // Restore only the value managed by us. If the user changed it while
        // DeepSeek was active, preserve that external edit.
        if (detail != null && current == "STEPS_COMMANDS") {
            restored = previous.hadOriginal
                ? config.Remove(detail.Start, detail.End - detail.Start).Insert(detail.Start, previous.originalEntry)
                : config.Remove(detail.Start, detail.End - detail.Start);
        }
        return new DesktopWorkModePlan {
            Config = restored,
            State = new DesktopWorkModeState { active = false, hadOriginal = previous.hadOriginal, originalEntry = previous.originalEntry }
        };
    }
    static void WriteStateIfChanged(string path, string text)
    {
        byte[] current = File.Exists(path) ? Bytes(path) : null;
        if (current != null && Equal(current, Utf8.GetBytes(text))) return;
        AtomicWrite(path, text, current, null);
    }
    // The state files are tiny and written on every switch. System.Web's
    // JavaScriptSerializer costs ~40 ms on first use (JIT + reflection), so a
    // strict reader/writer for these fixed shapes is used instead.
    internal static string JsonRaw(string json, string name)
    {
        Match match = Regex.Match(json, "\"" + Regex.Escape(name) + "\"\\s*:\\s*(null|true|false|-?[0-9]+(?:\\.[0-9]+)?|\"(?:\\\\.|[^\"\\\\])*\")");
        return match.Success ? match.Groups[1].Value : null;
    }
    internal static bool JsonBool(string json, string name)
    {
        string raw = JsonRaw(json, name);
        if (raw == "true") return true;
        if (raw == "false") return false;
        Fail("状态文件格式无法识别，未作修改。");
        return false;
    }
    internal static string JsonText(string json, string name)
    {
        string raw = JsonRaw(json, name);
        if (raw == null || raw == "null") return null;
        if (raw.Length < 2 || raw[0] != '"') Fail("状态文件格式无法识别，未作修改。");
        return JsonUnescape(raw.Substring(1, raw.Length - 2));
    }
    internal static string JsonUnescape(string text)
    {
        StringBuilder result = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++) {
            char c = text[i];
            if (c != '\\') { result.Append(c); continue; }
            if (++i == text.Length) Fail("状态文件转义不完整，未作修改。");
            char escape = text[i];
            if (escape == '"' || escape == '\\' || escape == '/') result.Append(escape);
            else if (escape == 'b') result.Append('\b');
            else if (escape == 'f') result.Append('\f');
            else if (escape == 'n') result.Append('\n');
            else if (escape == 'r') result.Append('\r');
            else if (escape == 't') result.Append('\t');
            else if (escape == 'u') {
                if (i + 4 >= text.Length) Fail("状态文件转义不完整，未作修改。");
                int code;
                if (!Int32.TryParse(text.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code)) Fail("状态文件转义不完整，未作修改。");
                result.Append((char)code); i += 4;
            } else Fail("状态文件含未知转义，未作修改。");
        }
        return result.ToString();
    }
    internal static string JsonString(string value)
    {
        if (value == null) return "null";
        StringBuilder result = new StringBuilder("\"");
        foreach (char c in value) {
            if (c == '"') result.Append("\\\"");
            else if (c == '\\') result.Append("\\\\");
            else if (c == '\n') result.Append("\\n");
            else if (c == '\r') result.Append("\\r");
            else if (c == '\t') result.Append("\\t");
            else if (c < ' ') result.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            else result.Append(c);
        }
        return result.Append('"').ToString();
    }
    internal static List<string> JsonStringArray(string json, string name)
    {
        List<string> result = new List<string>();
        Match array = Regex.Match(json, "\"" + Regex.Escape(name) + "\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline);
        if (!array.Success) return result;
        foreach (Match item in Regex.Matches(array.Groups[1].Value, "\"(?:\\\\.|[^\"\\\\])*\""))
            result.Add(JsonUnescape(item.Value.Substring(1, item.Value.Length - 2)));
        return result;
    }
    static DesktopWorkModeState ReadWorkModeState(string path)
    {
        if (!File.Exists(path)) return null;
        string json = File.ReadAllText(path);
        return new DesktopWorkModeState { active = JsonBool(json, "active"), hadOriginal = JsonBool(json, "hadOriginal"), originalEntry = JsonText(json, "originalEntry") };
    }
    static string SerializeWorkModeState(DesktopWorkModeState state)
    {
        return "{\"active\":" + (state.active ? "true" : "false") + ",\"hadOriginal\":" + (state.hadOriginal ? "true" : "false") + ",\"originalEntry\":" + JsonString(state.originalEntry) + "}";
    }
    static DesktopRouteState ReadRouteState(string path)
    {
        if (!File.Exists(path)) return null;
        string json = File.ReadAllText(path);
        return new DesktopRouteState { active = JsonBool(json, "active"), originalUserValue = JsonText(json, "originalUserValue") };
    }
    static string SerializeRouteState(DesktopRouteState state)
    {
        return "{\"active\":" + (state.active ? "true" : "false") + ",\"originalUserValue\":" + JsonString(state.originalUserValue) + "}";
    }
    static void UpdateDesktopWorkMode(string directory, string mode)
    {
        if (mode != "DeepSeek" && mode != "OpenAI" && mode != "Uninstall") return;
        string configPath = Path.Combine(directory, "config.toml");
        string statePath = Path.Combine(directory, "provider-switch", "desktop-work-mode-state.json");
        DesktopWorkModeState state = ReadWorkModeState(statePath);
        byte[] originalBytes = Bytes(configPath); string original = Decode(originalBytes);
        DesktopWorkModePlan plan = PlanDesktopWorkMode(original, mode, state);
        if (mode == "DeepSeek") {
            WriteStateIfChanged(statePath, SerializeWorkModeState(plan.State));
            if (plan.Config != original) AtomicWrite(configPath, plan.Config, originalBytes, Path.Combine(directory, "provider-switch", "backup", "config.before-work-mode-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".toml"));
        } else {
            if (plan.Config != original) AtomicWrite(configPath, plan.Config, originalBytes, Path.Combine(directory, "provider-switch", "backup", "config.before-work-mode-restore-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".toml"));
            if (plan.State != null) WriteStateIfChanged(statePath, SerializeWorkModeState(plan.State));
        }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr first,
        string value, uint flags, uint timeout, out UIntPtr result);
    static void BroadcastEnvironment()
    {
        UIntPtr result;
        SendMessageTimeout(new IntPtr(0xffff), 0x1a, UIntPtr.Zero, "Environment", 2, 1000, out result);
    }
    // Desktop builds that prefer the signed-in durable WebSocket route cannot
    // submit API-key provider turns through that route. Use the client's own
    // local CLI route only in DeepSeek mode, restoring the exact previous user
    // setting on GPT/uninstall. Alternate-directory fixtures never touch HKCU.
    static bool UpdateDesktopRouting(string directory, string mode)
    {
        if (mode != "DeepSeek" && mode != "OpenAI" && mode != "Uninstall") return false;
        string realHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (String.IsNullOrWhiteSpace(realHome)) realHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        if (!String.Equals(Path.GetFullPath(directory).TrimEnd('\\','/'), Path.GetFullPath(realHome).TrimEnd('\\','/'), StringComparison.OrdinalIgnoreCase)) return false;
        string path = Path.Combine(directory, "provider-switch", "desktop-routing-state.json");
        DesktopRouteState state = ReadRouteState(path);
        using (RegistryKey environment = Registry.CurrentUser.CreateSubKey("Environment")) {
            string current = environment.GetValue(DesktopRouteVariable, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            DesktopRoutePlan plan = PlanDesktopRouting(mode, current, state);
            if (plan.State != null) WriteStateIfChanged(path, SerializeRouteState(plan.State));
            if (!plan.WriteEnvironment) return false;
            if (plan.Value == null) environment.DeleteValue(DesktopRouteVariable, false);
            else environment.SetValue(DesktopRouteVariable, plan.Value, RegistryValueKind.String);
            return true;
        }
    }
    static bool Space(char c) { return c == ' ' || c == '\t' || c == '\r'; }
    static int LineEnd(string s, int start) { int n = s.IndexOf('\n', start); return n < 0 ? s.Length : n + 1; }

    // Quoted paths are supported, but escaped key spellings are rejected rather
    // than risk missing a provider collision. Values have a separate lexer.
    static string[] KeyPath(string source)
    {
        List<string> parts = new List<string>(); int i = 0;
        while (i < source.Length) {
            while (i < source.Length && Space(source[i])) i++;
            if (i == source.Length) Fail("无法安全识别配置键。");
            string part;
            if (source[i] == '\'' || source[i] == '"') {
                char quote = source[i++]; int start = i;
                while (i < source.Length && source[i] != quote) {
                    if ((quote == '"' && source[i] == '\\') || source[i] == '\n') Fail("暂不支持带转义的配置键，请先检查配置。");
                    i++;
                }
                if (i == source.Length) Fail("配置键引号未闭合。");
                part = source.Substring(start, i++ - start);
            } else {
                int start = i;
                while (i < source.Length && ((source[i] >= 'a' && source[i] <= 'z') || (source[i] >= 'A' && source[i] <= 'Z') || (source[i] >= '0' && source[i] <= '9') || source[i] == '_' || source[i] == '-')) i++;
                if (i == start) Fail("无法安全识别配置键。");
                part = source.Substring(start, i - start);
            }
            parts.Add(part);
            while (i < source.Length && Space(source[i])) i++;
            if (i == source.Length) break;
            if (source[i++] != '.') Fail("无法安全识别配置键路径。");
            if (i == source.Length) Fail("配置键路径不完整。");
        }
        if (parts.Count == 0) Fail("配置键为空。");
        return parts.ToArray();
    }
    static int ValueEnd(string s, int start)
    {
        Stack<char> brackets = new Stack<char>();
        char quote = '\0'; bool triple = false, escape = false, any = false;
        for (int i = start; i < s.Length; i++) {
            char c = s[i];
            if (quote != '\0') {
                if (escape) { escape = false; continue; }
                if (quote == '"' && c == '\\') { escape = true; continue; }
                if (c == quote) {
                    if (!triple) quote = '\0';
                    else if (i + 2 < s.Length && s[i + 1] == quote && s[i + 2] == quote) {
                        i += 2; int extra = 0;
                        while (i + 1 < s.Length && s[i + 1] == quote && extra < 2) { i++; extra++; }
                        quote = '\0';
                    }
                } else if (c == '\n' && !triple) Fail("配置含未闭合的单行字符串，未作修改。");
                continue;
            }
            if (c == '#') {
                int end = LineEnd(s, i); i = end - 1;
                if (brackets.Count == 0) { if (!any) Fail("配置值为空。"); return end; }
                continue;
            }
            if (c == '\n' && brackets.Count == 0) { if (!any) Fail("配置值为空。"); return i + 1; }
            if (c == '"' || c == '\'') {
                any = true; quote = c; triple = i + 2 < s.Length && s[i + 1] == c && s[i + 2] == c;
                if (triple) i += 2;
            } else if (c == '[' || c == '{') { any = true; brackets.Push(c); }
            else if (c == ']' || c == '}') {
                if (brackets.Count == 0 || brackets.Pop() != (c == ']' ? '[' : '{')) Fail("配置括号不匹配。");
            } else if (!Char.IsWhiteSpace(c)) any = true;
        }
        if (quote != '\0' || brackets.Count != 0 || !any) Fail("配置值不完整，未作修改。");
        return s.Length;
    }
    static Document Parse(string s)
    {
        Document d = new Document(); d.Text = s; d.TopEnd = s.Length; bool root = true;
        for (int pos = 0; pos < s.Length; ) {
            int end = LineEnd(s, pos), i = pos;
            while (i < end && Space(s[i])) i++;
            if (i == end || s[i] == '\n') { pos = end; continue; }
            Item item = new Item(); item.Start = pos; item.End = end; item.Root = root;
            if (s[i] == '#') { item.Kind = "comment"; item.Value = s.Substring(i, end - i).TrimEnd('\r', '\n'); }
            else if (s[i] == '[') {
                item.Kind = "table"; int start = ++i; bool array = i < end && s[i] == '[';
                item.IsArray = array;
                if (array) start = ++i;
                char quote = '\0';
                for (; i < end; i++) {
                    char c = s[i];
                    if (quote != '\0') { if (quote == '"' && c == '\\') Fail("暂不支持表名中的转义。"); if (c == quote) quote = '\0'; }
                    else if (c == '"' || c == '\'') quote = c;
                    else if (c == ']') break;
                }
                if (i == end || quote != '\0') Fail("配置表名未闭合。");
                item.Path = KeyPath(s.Substring(start, i - start)); i++;
                if (array && (i == end || s[i++] != ']')) Fail("数组表名未闭合。");
                while (i < end && Space(s[i])) i++;
                if (i < end && s[i] != '#' && s[i] != '\n') Fail("配置表名后存在无法识别的内容。");
                if (root) d.TopEnd = pos;
                root = false; item.Root = false;
            } else {
                item.Kind = "field"; int start = i; char quote = '\0';
                for (; i < end; i++) {
                    char c = s[i];
                    if (quote != '\0') { if (quote == '"' && c == '\\') Fail("暂不支持配置键中的转义。"); if (c == quote) quote = '\0'; }
                    else if (c == '"' || c == '\'') quote = c;
                    else if (c == '=') break;
                }
                if (i == end || quote != '\0') Fail("无法识别配置赋值，未作修改。");
                item.Path = KeyPath(s.Substring(start, i - start));
                item.Key = item.Path.Length == 1 ? item.Path[0] : null;
                int valueStart = i + 1;
                item.End = ValueEnd(s, valueStart); item.Value = s.Substring(valueStart, item.End - valueStart).Trim();
                if (root && Owned.Contains(item.Path[0])) {
                    if (item.Path.Length != 1 || d.Fields.ContainsKey(item.Key)) Fail("切换字段重复或使用不支持的结构，未作修改。");
                    ReadString(item.Value); d.Fields.Add(item.Key, item);
                }
            }
            d.Items.Add(item); pos = item.End;
        }
        return d;
    }
    static string ReadString(string value)
    {
        if (value.IndexOf('\n') >= 0 || value.IndexOf('\r') >= 0 || value.StartsWith("\"\"\"") || value.StartsWith("'''")) Fail("切换字段使用多行格式，暂不支持安全修改；原配置保持不变。");
        if (value.Length < 2 || (value[0] != '"' && value[0] != '\'')) Fail("切换字段应为字符串，未作修改。");
        char quote = value[0]; StringBuilder b = new StringBuilder(); int i = 1;
        for (; i < value.Length; i++) {
            char c = value[i]; if (c == quote) break;
            if (c == '\\' && quote == '"') {
                if (++i == value.Length) Fail("字符串转义不完整。"); c = value[i];
                if (c == '\\' || c == '"') b.Append(c);
                else if (c == 'n') b.Append('\n'); else if (c == 'r') b.Append('\r'); else if (c == 't') b.Append('\t');
                else Fail("切换字段含暂不支持的转义，未作修改。");
            } else b.Append(c);
        }
        if (i == value.Length) Fail("字符串未闭合。");
        string tail = value.Substring(i + 1).Trim();
        if (tail.Length != 0 && !tail.StartsWith("#")) Fail("字符串后存在无法识别的内容。");
        return b.ToString();
    }
    static string Get(Document d, string key) { return d.Fields.ContainsKey(key) ? ReadString(d.Fields[key].Value) : null; }
    static string Quote(string s) { return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\""; }
    static Block FindBlock(Document d, string begin, string end)
    {
        Item a = null, b = null;
        foreach (Item item in d.Items) if (item.Kind == "comment") {
            if (item.Value == begin) { if (a != null) Fail("托管标记重复，未作修改。"); a = item; }
            if (item.Value == end) { if (b != null) Fail("托管标记重复，未作修改。"); b = item; }
        }
        if (a == null && b == null) return null;
        if (a == null || b == null || a.End > b.Start) Fail("托管标记损坏，未作修改。请检查备份。");
        return new Block { Start = a.Start, End = b.End };
    }
    static bool In(Item item, Block b) { return b != null && item.Start >= b.Start && item.End <= b.End; }
    static string Remove(string s, Block a, Block b)
    {
        List<Block> blocks = new List<Block>(); if (a != null) blocks.Add(a); if (b != null) blocks.Add(b);
        blocks.Sort(delegate(Block x, Block y) { return y.Start.CompareTo(x.Start); });
        foreach (Block block in blocks) s = s.Remove(block.Start, block.End - block.Start);
        return s;
    }
    static void ValidateManaged(Document d, Block active, Block provider)
    {
        if (active != null && provider != null && active.Start < provider.End && provider.Start < active.End) Fail("托管区块重叠，未作修改。");
        Dictionary<string, string> expected = new Dictionary<string, string> {
            {"name", "DeepSeek"}, {"base_url", "https://api.deepseek.com/"}, {"wire_api", "responses"}, {"env_key", "DEEPSEEK_API_KEY"} };
        int providerTables = 0; bool deepScope = false; HashSet<string> seen = new HashSet<string>();
        foreach (Item item in d.Items) {
            if (item.Kind == "comment") continue;
            if (item.Kind == "table") {
                deepScope = item.Path.Length >= 2 && item.Path[0] == "model_providers" && item.Path[1] == "deepseek";
                if (Owned.Contains(item.Path[0])) Fail("切换字段被定义为配置表，未作修改。");
            }
            if (item.Kind == "field" && deepScope && !In(item, provider)) Fail("DeepSeek 提供方区块之外还有该提供方的设置，未作修改。");
            if (In(item, active) && (item.Kind != "field" || !item.Root || item.Key == null || !Owned.Contains(item.Key))) Fail("切换托管区块含其他设置，已停止以免丢失修改。");
            if (active != null && item.Root && item.Kind == "field" && item.Key != null && Owned.Contains(item.Key) && !In(item, active)) Fail("托管区块之外还有切换字段，未作修改。");
            if (In(item, provider)) {
                if (item.Kind == "table" && !item.IsArray && item.Path.Length == 2 && item.Path[0] == "model_providers" && item.Path[1] == "deepseek") providerTables++;
                else if (item.Kind == "field" && providerTables == 1 && item.Key != null && expected.ContainsKey(item.Key) && seen.Add(item.Key) && ReadString(item.Value) == expected[item.Key]) { }
                else Fail("DeepSeek 提供方托管区块已被修改，未作覆盖。");
            } else if ((item.Kind == "table" && item.Path[0] == "model_providers" && (item.Path.Length == 1 || item.Path[1] == "deepseek")) ||
                (item.Kind == "field" && item.Root && item.Path[0] == "model_providers")) Fail("发现已有 DeepSeek 提供方或无法安全合并的提供方结构，未作覆盖。");
        }
        if (provider != null && (providerTables != 1 || seen.Count != expected.Count)) Fail("DeepSeek 提供方区块不完整，未作修改。");
        if (active != null && (provider == null || Get(d, "model_provider") != "deepseek")) Fail("托管状态与当前提供方不一致，未作修改。");
        string current = Get(d, "model_provider");
        if (active == null && current != null && current != "openai") Fail("当前使用其他提供方，请先确认配置；未作覆盖。");
    }
    static void ValidateState(string text)
    {
        Document d = Parse(text);
        foreach (Item item in d.Items) if (item.Kind != "comment" && (item.Kind != "field" || !item.Root || item.Key == null || !Owned.Contains(item.Key))) Fail("GPT 恢复数据不完整或含其他设置，未作修改。");
        string p = Get(d, "model_provider");
        if (p != null && p != "openai") Fail("GPT 恢复数据指向其他提供方，未作修改。");
    }
    static string WithoutFields(Document d)
    {
        string s = d.Text; List<Item> fields = new List<Item>(d.Fields.Values);
        fields.Sort(delegate(Item a, Item b) { return b.Start.CompareTo(a.Start); });
        foreach (Item item in fields) s = s.Remove(item.Start, item.End - item.Start);
        return s;
    }
    static byte[] Bytes(string path) { return File.ReadAllBytes(path); }
    static string Decode(byte[] bytes) { int offset = bytes.Length >= 3 && bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191 ? 3 : 0; return Utf8.GetString(bytes, offset, bytes.Length - offset); }
    static bool Equal(byte[] a, byte[] b) { if (a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true; }
    static string BackupName(string directory, string prefix) { return Path.Combine(directory, prefix + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N") + ".toml"); }
    static string Digest(byte[] bytes) { using (SHA256 hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", ""); }
    // Keep the file's own newline style so a provider round trip restores the
    // original bytes instead of injecting mixed CRLF/LF endings.
    static string NewLine(string text)
    {
        int crlf = 0, lf = 0;
        for (int i = 0; i < text.Length; i++) if (text[i] == '\n') {
            if (i > 0 && text[i - 1] == '\r') crlf++; else lf++;
        }
        return crlf > lf ? "\r\n" : "\n";
    }
    static string NormalizeNewlines(string text, string newline)
    {
        if (text == null) return null;
        text = text.Replace("\r\n", "\n").Replace("\r", "\n");
        return newline == "\n" ? text : text.Replace("\n", newline);
    }
    static DateTime BackupTime(string name, DateTime fallback)
    {
        Match match = Regex.Match(name, "(\\d{8})-(\\d{6})-(\\d{3})");
        DateTime stamp;
        if (match.Success && DateTime.TryParseExact(match.Groups[1].Value + match.Groups[2].Value + match.Groups[3].Value,
            "yyyyMMddHHmmssfff", CultureInfo.InvariantCulture, DateTimeStyles.None, out stamp)) return stamp;
        return fallback;
    }
    static void Prune(string directory, string pattern, int keep)
    {
        try {
            FileInfo[] files = new DirectoryInfo(directory).GetFiles(pattern);
            if (files.Length <= keep) return;
            Dictionary<FileInfo, DateTime> times = new Dictionary<FileInfo, DateTime>();
            foreach (FileInfo file in files) times[file] = BackupTime(file.Name, file.LastWriteTimeUtc);
            Array.Sort(files, delegate(FileInfo a, FileInfo b) {
                int order = times[b].CompareTo(times[a]);
                return order != 0 ? order : StringComparer.Ordinal.Compare(b.Name, a.Name);
            });
            for (int i = keep; i < files.Length; i++) {
                try { files[i].Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    // Backups are recovery snapshots, not history: retain enough recent
    // switches for a rollback without letting every click grow the folder.
    static void PruneBackups(string directory)
    {
        Prune(directory, "config.before-*.toml", 20);
        Prune(directory, "openai-state-*.toml", 5);
        Prune(directory, "install.before-*.json", 5);
    }
    static void AtomicWrite(string path, string text, byte[] expected, string backup)
    {
        string temp = path + ".switch-" + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            byte[] data = Utf8.GetBytes(text);
            using (FileStream f = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { f.Write(data, 0, data.Length); f.Flush(true); }
            if (expected == null) { if (File.Exists(path)) Fail("目标文件已被其他程序创建，请重试。"); File.Move(temp, path); }
            else {
                if (!Equal(Bytes(path), expected)) Fail("配置在操作期间被其他程序修改，请重试；未覆盖外部修改。");
                File.Replace(temp, path, backup);
            }
        } finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    static string Switch(string directory, string mode)
    {
        string config = Path.Combine(directory, "config.toml"), tool = Path.Combine(directory, "provider-switch");
        string state = Path.Combine(tool, "openai-state.txt"), backupDir = Path.Combine(tool, "backup");
        string stateHash = Path.Combine(tool, "openai-state.sha256");
        if (!File.Exists(config)) Fail("未找到 Codex 配置。请先启动并登录 Codex。");
        byte[] originalBytes = Bytes(config); string original = Decode(originalBytes);
        string newline = NewLine(original);
        Document d = Parse(original); Block active = FindBlock(d, Begin, End), provider = FindBlock(d, ProviderBegin, ProviderEnd);
        ValidateManaged(d, active, provider);
        if (mode == "Validate") { if (active != null) Fail("安装前请先恢复 GPT 配置。"); return "配置检查通过。"; }
        if (!Directory.Exists(tool)) Fail("工具目录不存在，请重新安装。");
        bool keepProvider = true;
        if (mode == "Uninstall") {
            string manifest = File.ReadAllText(Path.Combine(tool, "install.json"));
            if (!Regex.IsMatch(manifest, "\"hadManagedProvider\"\\s*:\\s*(true|false)")) Fail("安装记录不完整。");
            keepProvider = Regex.IsMatch(manifest, "\"hadManagedProvider\"\\s*:\\s*true");
        }
        if (active == null && (mode == "OpenAI" || (mode == "Uninstall" && (provider == null || keepProvider)))) return "GPT configuration already selected. Please manually exit and reopen Codex to apply it.";
        string saved = null;
        if (active != null) {
            if (!File.Exists(state)) Fail("缺少 GPT 恢复数据，请检查本机备份。未作修改。");
            byte[] savedBytes = Bytes(state);
            // Legacy 1.0.1 states had no digest; preserve compatibility after syntax checks.
            if (File.Exists(stateHash) && File.ReadAllText(stateHash).Trim() != Digest(savedBytes)) Fail("GPT 恢复数据校验失败，请检查备份。未作修改。");
            saved = Decode(savedBytes); ValidateState(saved); saved = NormalizeNewlines(saved, newline);
        }
        if (mode == "Check") return "配置与恢复数据检查通过。";
        if (mode == "DeepSeek") {
            // Presence only: never print, copy or log the credential.
            if (String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY")) && String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY", EnvironmentVariableTarget.User))) Fail("尚未设置 DeepSeek Key，请运行 Set-DeepSeekApiKey.cmd，在本机控制台隐藏输入。");
            string catalogPath = Path.Combine(tool, "deepseek-models.json");
            if (!File.Exists(catalogPath)) Fail("缺少 DeepSeek 模型目录，请重新安装。");
            if (active != null) { ValidateDeepPreference(Get(d,"model"), Get(d,"model_reasoning_effort"), tool); return "DeepSeek configuration already selected. Please manually exit and reopen Codex to apply it."; }
            if (active == null) {
                StringBuilder b = new StringBuilder();
                foreach (Item item in d.Items) if (item.Root && item.Kind == "field" && item.Key != null && Owned.Contains(item.Key)) b.Append(original.Substring(item.Start, item.End - item.Start).TrimEnd('\r', '\n')).Append(newline);
                saved = b.ToString(); ValidateState(saved);
            }
        }
        string deepState = Path.Combine(tool, "deepseek-preferences.json");
        string deepModel = "deepseek-flash", deepEffort = "high";
        if (active != null && (mode == "OpenAI" || mode == "Uninstall")) {
            deepModel = Get(d, "model"); deepEffort = Get(d, "model_reasoning_effort");
            ValidateDeepPreference(deepModel, deepEffort, tool);
            WriteStateIfChanged(deepState, "{\"model\":" + JsonString(deepModel) + ",\"effort\":" + JsonString(deepEffort) + "}");
        } else if (mode == "DeepSeek" && File.Exists(deepState)) {
            string pref = File.ReadAllText(deepState); deepModel = JsonText(pref, "model"); deepEffort = JsonText(pref, "effort");
            ValidateDeepPreference(deepModel, deepEffort, tool);
        }
        string raw = Remove(original, active, provider); Document clean = Parse(raw);
        if (mode == "DeepSeek" || active != null) raw = WithoutFields(clean);
        clean = Parse(raw); string top = raw.Substring(0, clean.TopEnd), tables = raw.Substring(clean.TopEnd);
        string managedProvider = ProviderBegin + newline + "[model_providers.deepseek]" + newline + "name = \"DeepSeek\"" + newline + "base_url = \"https://api.deepseek.com/\"" + newline + "wire_api = \"responses\"" + newline + "env_key = \"DEEPSEEK_API_KEY\"" + newline + ProviderEnd + newline;
        string next;
        if (mode == "DeepSeek") {
            string block = Begin + newline + "model = " + Quote(deepModel) + newline + "model_provider = \"deepseek\"" + newline + "model_reasoning_effort = " + Quote(deepEffort) + newline + "web_search = \"disabled\"" + newline + "model_catalog_json = " + Quote(Path.Combine(tool, "deepseek-models.json").Replace('\\', '/')) + newline + End + newline;
            next = top + (top.Length > 0 && !top.EndsWith("\n") ? newline : "") + block + tables;
        } else next = (active != null ? saved : "") + top + tables;
        if (mode != "Uninstall" || keepProvider) next += (next.Length > 0 && !next.EndsWith("\n") ? newline : "") + managedProvider;
        Document proposed = Parse(next); ValidateManaged(proposed, FindBlock(proposed, Begin, End), FindBlock(proposed, ProviderBegin, ProviderEnd));
        Directory.CreateDirectory(backupDir);
        // A managed DeepSeek session never replaces the GPT recovery state.
        if (mode == "DeepSeek" && active == null) {
            AtomicWrite(state, saved, File.Exists(state) ? Bytes(state) : null, File.Exists(state) ? BackupName(backupDir, "openai-state") : null);
            AtomicWrite(stateHash, Digest(Utf8.GetBytes(saved)), File.Exists(stateHash) ? Bytes(stateHash) : null, null);
        }
        if (next != original) AtomicWrite(config, next, originalBytes, BackupName(backupDir, "config.before-" + mode));
        if (mode == "DeepSeek" || mode == "OpenAI" || mode == "Uninstall") PruneBackups(backupDir);
        return mode == "DeepSeek" ? "DeepSeek configuration saved. Please manually exit and reopen Codex to apply it." : "GPT configuration saved. Please manually exit and reopen Codex to apply it.";
    }

}
