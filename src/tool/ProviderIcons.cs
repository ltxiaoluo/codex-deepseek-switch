using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

// Persist the status in shortcut resources, not a running-window overlay.
// Only shortcuts targeting this exact installation, mode and config are changed.
internal static class ProviderIcons
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern void SHChangeNotify(uint events, uint flags, string first, IntPtr second);
    [DllImport("shell32.dll", EntryPoint = "SHChangeNotify")]
    static extern void RefreshCache(uint events, uint flags, IntPtr first, IntPtr second);

    sealed class Change {
        public string Path, Icon, Identity;
        public byte[] Original, Updated;
        public bool Committed;
    }
    static string Full(string path) { return Path.GetFullPath(path).TrimEnd('\\', '/'); }
    static bool Same(string a, string b) { return String.Equals(Full(a), Full(b), StringComparison.OrdinalIgnoreCase); }
    static bool Equal(byte[] a, byte[] b) { if (a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true; }
    static void Notify(Change change) {
        SHChangeNotify(0x2000, 0x3005, change.Path, IntPtr.Zero); // UPDATEITEM, PATHW | FLUSHNOWAIT
    }
    // Preserve the identity of the pinned .lnk file: replacing it can leave the
    // taskbar holding a stale shell item. Config files still use atomic replace.
    static void WriteExisting(string path, byte[] next, byte[] expected) {
        using (FileStream f = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
            byte[] before = new byte[f.Length]; int read = 0;
            while (read < before.Length) { int n = f.Read(before, read, before.Length - read); if (n == 0) throw new IOException("Incomplete shortcut read."); read += n; }
            if (expected != null && !Equal(before, expected)) throw new IOException("Shortcut changed externally; retrying.");
            try { f.Position = 0; f.Write(next, 0, next.Length); f.SetLength(next.Length); f.Flush(true); }
            catch { f.Position = 0; f.Write(before, 0, before.Length); f.SetLength(before.Length); f.Flush(true); throw; }
        }
    }
    // Build the new shortcut outside the pinned folder so Explorer never sees a
    // temporary .lnk next to the real pins (and can never leave one behind).
    static byte[] WithIcon(string tool, byte[] source, string iconPath, string identity) {
        string tempDir = Path.Combine(tool, "tmp");
        Directory.CreateDirectory(tempDir);
        string temp = Path.Combine(tempDir, "icon-" + Guid.NewGuid().ToString("N") + ".lnk");
        try {
            File.WriteAllBytes(temp, source);
            using (NativeShortcut staged = new NativeShortcut(temp, true)) { staged.SetIcon(iconPath); staged.Identity = identity; staged.Save(temp); }
            return File.ReadAllBytes(temp);
        } finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    static void Commit(string tool, Change change, List<string> notes) {
        for (int attempt = 0; attempt < 4; attempt++) {
            byte[] current = File.ReadAllBytes(change.Path);
            if (!Equal(current, change.Original)) {
                // Explorer or the user rewrote the shortcut meanwhile. Keep those
                // edits and re-apply only the icon and managed identity.
                change.Original = current;
                change.Updated = WithIcon(tool, current, change.Icon, change.Identity);
                notes.Add("REBUILD " + Path.GetFileName(change.Path));
            }
            try {
                WriteExisting(change.Path, change.Updated, change.Original);
                change.Committed = true;
                return;
            } catch (IOException) {
                if (attempt == 3) throw;
                notes.Add("RETRY " + Path.GetFileName(change.Path));
                Thread.Sleep(40);
            }
        }
    }
    static void CleanStaleTemps(string directory, List<string> dirs, List<string> notes) {
        List<string> all = new List<string>(dirs);
        all.Add(Path.Combine(directory, "provider-switch"));
        all.Add(Path.Combine(directory, "provider-switch", "tmp"));
        foreach (string dir in all) {
            if (!Directory.Exists(dir)) continue;
            string[] patterns = { ".codex-icon-*.lnk", "icon-*.lnk" };
            foreach (string pattern in patterns) foreach (string file in Directory.GetFiles(dir, pattern)) {
                try {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromMinutes(2)) continue;
                    File.Delete(file);
                    notes.Add("CLEAN " + Path.GetFileName(file));
                } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
    static void Log(string directory, string selected, List<Change> changes, List<string> notes, string status) {
        try {
            string path = Path.Combine(directory, "provider-switch", "icon-sync.log");
            FileInfo info = new FileInfo(path);
            if (info.Exists && info.Length > 262144) {
                string previous = path + ".1";
                try { if (File.Exists(previous)) File.Delete(previous); File.Move(path, previous); } catch (IOException) { }
            }
            StringBuilder line = new StringBuilder();
            line.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(" status=").Append(status).Append(" selected=").Append(selected).Append(" changes=").Append(changes.Count);
            foreach (Change change in changes) line.Append(' ').Append(Path.GetFileName(change.Path)).Append('=').Append(Path.GetFileName(change.Icon));
            foreach (string note in notes) line.Append(" | ").Append(note);
            File.AppendAllText(path, line.ToString() + "\r\n", new UTF8Encoding(false));
        } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    public static void RefreshShell() {
        RefreshCache(0x08000000, 0x1000, IntPtr.Zero, IntPtr.Zero);
    }
    public static int Sync(string directory, string selected, bool reset, bool forceRefresh = false, bool includePinned = true)
    {
        string tool = Path.Combine(directory, "provider-switch"), iconDir = Path.Combine(tool, "icons");
        // Config-only fixtures / pre-icon legacy tools have no icon installation.
        if (!Directory.Exists(iconDir)) return 0;
        string[] modes = { "OpenAI", "DeepSeek" };
        foreach (string mode in modes) foreach (string suffix in new string[] { "", "-Selected" })
            if (!File.Exists(Path.Combine(iconDir, "Codex-" + mode + suffix + ".ico")))
                throw new IOException("Status icon files are incomplete. Configuration is saved; reinstall the icon assets.");
        HashSet<string> candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> pinnedCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string manifest = Path.Combine(tool, "install.json");
        if (File.Exists(manifest)) {
            foreach (string path in SwitchCodexProvider.JsonStringArray(File.ReadAllText(manifest), "shortcuts")) {
                if (path != null && Path.IsPathRooted(path) && path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) candidates.Add(Full(path));
            }
        }
        // Do not enumerate the real user's pinned links for an alternate-directory test.
        string home = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (String.IsNullOrWhiteSpace(home)) home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        string pinned = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft\\Internet Explorer\\Quick Launch\\User Pinned\\TaskBar");
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (Same(directory, home)) {
            if (Directory.Exists(desktop)) foreach (string path in Directory.GetFiles(desktop, "*.lnk")) candidates.Add(Full(path));
            if (includePinned && Directory.Exists(pinned)) foreach (string path in Directory.GetFiles(pinned, "*.lnk")) {
                candidates.Add(Full(path)); pinnedCandidates.Add(Full(path));
            }
        }
        List<Change> changes = new List<Change>();
        List<string> notes = new List<string>();
        List<string> cleanDirs = new List<string>();
        if (includePinned) { cleanDirs.Add(pinned); cleanDirs.Add(desktop); }
        try {
            CleanStaleTemps(directory, cleanDirs, notes);
            foreach (string path in candidates) {
                if (!File.Exists(path)) continue;
                // The copies kept for future re-pinning are refreshed by
                // SyncIcons; rewriting them on every click only adds latency.
                if (includePinned && pinnedCandidates.Count > 0 && !forceRefresh && Same(Path.GetDirectoryName(path), tool)) continue;
                using (NativeShortcut link = new NativeShortcut(path)) {
                    string target = link.Target;
                    if (String.IsNullOrEmpty(target) || !Same(target, Path.Combine(tool, "Switch-CodexProvider.exe"))) continue;
                    Match args = Regex.Match(link.Arguments, "\\A-Mode (OpenAI|DeepSeek) -Directory \"([^\"]+)\"\\z");
                    if (!args.Success || !Same(args.Groups[2].Value, directory)) continue;
                    string mode = args.Groups[1].Value;
                    string icon = Path.Combine(iconDir, "Codex-" + mode + (!reset && mode == selected ? "-Selected" : "") + ".ico");
                    string old = link.Icon;
                    string identity = link.Identity;
                    string managedIdentity;
                    using (SHA256 hash = SHA256.Create()) managedIdentity = "CodexProviderSwitch." + mode + "." + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(Full(directory).ToUpperInvariant()))).Replace("-", "").Substring(0, 16);
                    // Legacy pins with a shared implicit identity must be replaced
                    // manually; changing their grouping while Explorer runs is unsafe.
                    if (pinnedCandidates.Contains(path) && identity != managedIdentity) {
                        notes.Add("SKIP-IDENTITY " + Path.GetFileName(path));
                        continue;
                    }
                    bool managed = pinnedCandidates.Contains(path) || identity == managedIdentity;
                    if (String.IsNullOrEmpty(identity)) identity = managedIdentity;
                    if (String.Equals(old, icon + ",0", StringComparison.OrdinalIgnoreCase) && identity == link.Identity) continue;
                    string normal = Path.Combine(iconDir, "Codex-" + mode + ".ico") + ",0";
                    string checkedIcon = Path.Combine(iconDir, "Codex-" + mode + "-Selected.ico") + ",0";
                    // A managed pin whose icon was reset externally is still ours;
                    // only shortcuts we never stamped keep a custom user icon.
                    if (!managed && !String.Equals(old, normal, StringComparison.OrdinalIgnoreCase) && !String.Equals(old, checkedIcon, StringComparison.OrdinalIgnoreCase)) {
                        notes.Add("SKIP-CUSTOM " + Path.GetFileName(path));
                        continue;
                    }
                    byte[] original = File.ReadAllBytes(path);
                    Change change = new Change { Path = path, Icon = icon, Identity = identity, Original = original };
                    change.Updated = WithIcon(tool, original, icon, identity);
                    changes.Add(change);
                }
            }
            // Notify each file as soon as it is written: Explorer starts
            // repainting the first button while the next file is still being built.
            foreach (Change change in changes) { Commit(tool, change, notes); Notify(change); }
            // Complete one synchronous cache notification before the switch exits.
            // Never schedule another refresh after the user starts the client.
            if (includePinned && forceRefresh) RefreshShell();
            // Re-read every managed shortcut; repair once if Explorer rewrote it
            // after our commit but before the icons were cached.
            foreach (Change change in changes) {
                // Byte comparison is enough: the written bytes already contain
                // the icon path and identity, and it avoids another COM load.
                byte[] current = File.ReadAllBytes(change.Path);
                if (Equal(current, change.Updated)) continue;
                notes.Add("VERIFY " + Path.GetFileName(change.Path));
                byte[] next = WithIcon(tool, current, change.Icon, change.Identity);
                WriteExisting(change.Path, next, current);
                change.Original = current; change.Updated = next; change.Committed = true;
                Notify(change);
            }

            Log(directory, selected, changes, notes, "ok");
            return changes.Count;
        } catch {
            foreach (Change change in changes) if (change.Committed) {
                try { WriteExisting(change.Path, change.Original, change.Updated); Notify(change); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            Log(directory, selected, changes, notes, "failed");
            throw;
        }
    }
}


