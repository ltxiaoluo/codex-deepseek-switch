using System;
using System.Windows.Forms;

// A short-lived GUI-subsystem process: no console window, no resident watcher.
internal static class IconRefreshWorker
{
    [STAThread]
    static int Main(string[] args)
    {
        bool quiet = Array.IndexOf(args, "-Quiet") >= 0;
        try {
            string directory = null;
            bool localOnly = false;
            bool broadcast = false;
            bool iconsDone = false;
            for (int i = 0; i < args.Length; i++) {
                if (args[i] == "-Quiet") continue;
                if (args[i] == "-LocalIconsOnly") { localOnly = true; continue; }
                if (args[i] == "-Broadcast") { broadcast = true; continue; }
                if (args[i] == "-IconsDone") { iconsDone = true; continue; }
                if (args[i] != "-Directory" || ++i == args.Length) throw new ArgumentException("Missing icon refresh directory.");
                directory = args[i];
            }
            if (String.IsNullOrEmpty(directory)) throw new ArgumentException("Missing icon refresh directory.");
            SwitchCodexProvider.RefreshIconsInWorker(directory, localOnly, broadcast, iconsDone);
            return 0;
        } catch (Exception e) {
            if (!quiet) MessageBox.Show("配置已保存，但图标更新失败。请再次点击同一目标按钮重试。\n\n" + e.Message,
                "Codex status icon refresh failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 2;
        }
    }
}

