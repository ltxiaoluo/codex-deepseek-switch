using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

internal static class ProviderGuard {
    static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
    internal static string Family(string value) {
        if(String.IsNullOrEmpty(value))return null;
        if(value=="openai"||value=="openai-direct"||value.StartsWith("gpt-",StringComparison.OrdinalIgnoreCase))return "GPT";
        if(value=="deepseek"||value.StartsWith("deepseek-",StringComparison.OrdinalIgnoreCase))return "DeepSeek";
        return null;
    }
    static string Text(Dictionary<string,object> data,string key) { object value;return data.TryGetValue(key,out value)&&value!=null?value.ToString():""; }
    static string Root(string file,string key) {
        foreach(string line in File.ReadLines(file)) { if(line.TrimStart().StartsWith("["))break;
            Match match=Regex.Match(line,"^\\s*"+key+"\\s*=\\s*[\"']([^\"']+)[\"']");if(match.Success)return match.Groups[1].Value; }
        return "";
    }
    static string TranscriptFamily(string id,string path) {
        if(String.IsNullOrEmpty(path))return null;
        try {
            using(var file=new StreamReader(new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete),Utf8)) {
                string line=file.ReadLine();if(line==null)return null;var data=FastJson.Read(line);object payload;
                if(Text(data,"type")=="session_meta"&&data.TryGetValue("payload",out payload)) {
                    var metadata=payload as Dictionary<string,object>;
                    if(metadata!=null&&Text(metadata,"id")==id)return Family(Text(metadata,"model_provider"));
                }
            }
        } catch(IOException){}catch(UnauthorizedAccessException){}catch(FormatException){}catch(ArgumentException){}
        return null;
    }
    static string Original(BindingStore store,string home,string id,string transcript,out bool maintenanceDue) {
        BindingEntry cached=store.Find(id);ThreadEntry source=null;string family=cached==null?null:cached.Family;
        if(cached==null) {
            family=TranscriptFamily(id,transcript);
            if(family!=null)source=new ThreadEntry{Id=id,Family=family,Path=transcript};
            else {
                Dictionary<string,ThreadEntry> index=ThreadIndex.Read(home,id);index.TryGetValue(id,out source);
                if(source!=null)family=source.Family;
            }
        }
        if(family==null)throw new IOException("无法确认这个对话原先所属的模型，已阻止发送。请新建对话。");
        return store.Touch(id,family,source,out maintenanceDue);
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct StartupInfo {
        public int Size;public string Reserved,Desktop,Title;public int X,Y,Width,Height,XChars,YChars,Fill,Flags;
        public short ShowWindow,ReservedSize;public IntPtr ReservedData,Input,Output,Error;
    }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInfo { public IntPtr Process,Thread;public uint Id,ThreadId; }
    [DllImport("kernel32.dll",EntryPoint="CreateProcessW",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)] static extern bool CreateProcess(string app,StringBuilder command,IntPtr pa,IntPtr ta,[MarshalAs(UnmanagedType.Bool)]bool inherit,uint flags,IntPtr env,string cwd,ref StartupInfo startup,out ProcessInfo process);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    static void StartMaintenance(string home) {
        string executable=System.Reflection.Assembly.GetExecutingAssembly().Location;
        var command=new StringBuilder("\""+executable+"\" --home \""+home+"\" --maintain");
        var startup=new StartupInfo();startup.Size=Marshal.SizeOf(typeof(StartupInfo));startup.Flags=1;startup.ShowWindow=0;
        ProcessInfo process;
        if(CreateProcess(executable,command,IntPtr.Zero,IntPtr.Zero,false,0x08000000,IntPtr.Zero,null,ref startup,out process)) { CloseHandle(process.Thread);CloseHandle(process.Process); }
    }
    static int Maintain(string home) {
        string token;using(var hash=new SHA256Managed())token=BitConverter.ToString(hash.ComputeHash(Utf8.GetBytes(home.ToUpperInvariant()))).Replace("-","");
        using(var mutex=new Mutex(false,"Local\\CodexBindingMaintenance_"+token)) {
            bool locked=false;
            try {
                try{locked=mutex.WaitOne(0);}catch(AbandonedMutexException){locked=true;}
                if(!locked)return 0;
                int count=BindingStore.Maintain(home);
                using(var store=new BindingStore(BindingStore.FilePath(home)))Console.WriteLine("{\"removed_deleted\":"+count+",\"status\":"+store.Status()+"}");
            } finally {if(locked)mutex.ReleaseMutex();}
        }
        return 0;
    }
    [STAThread] static int Main(string[] args) {
        string home=args.Length>1&&args[0]=="--home"?args[1]:Environment.GetEnvironmentVariable("CODEX_HOME");
        if(String.IsNullOrEmpty(home))home=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex");
        home=Path.GetFullPath(home).TrimEnd(Path.DirectorySeparatorChar);
        if(Array.IndexOf(args,"--migrate")>=0||Array.IndexOf(args,"--seed")>=0) {
            try {Console.WriteLine(BindingStore.Migrate(home));return 0;}catch(Exception e){Console.Error.WriteLine(e.Message);return 1;}
        }
        if(Array.IndexOf(args,"--maintain")>=0) {try{return Maintain(home);}catch(Exception e){Console.Error.WriteLine(e.Message);return 1;}}
        if(Array.IndexOf(args,"--status")>=0) {try{using(var store=new BindingStore(BindingStore.FilePath(home)))Console.WriteLine(store.Status());return 0;}catch(Exception e){Console.Error.WriteLine(e.Message);return 1;}}
        Console.InputEncoding=Utf8;Console.OutputEncoding=Utf8;string reason=null;bool maintenanceDue=false;
        try {
            var data=FastJson.Read(Console.In.ReadToEnd());Guid parsed;
            if(!Guid.TryParse(Text(data,"session_id"),out parsed))throw new IOException("无法识别对话标识，已阻止发送。");
            string id=parsed.ToString("D");string provider=Root(Path.Combine(home,"config.toml"),"model_provider");string selected=provider==""?"GPT":Family(provider),active=Family(Text(data,"model"));
            if(selected==null||active==null)throw new IOException("无法确认当前模型或切换配置，已阻止发送。请重新打开 Codex。");
            string original;
            using(var store=new BindingStore(BindingStore.FilePath(home)))original=Original(store,home,id,Text(data,"transcript_path"),out maintenanceDue);
            if(original!=selected)reason="当前选择的是 "+selected+"，这个对话属于 "+original+"。\n\n本次消息已在模型请求前阻止。请切回 "+original+"，或新建 "+selected+" 对话。";
            else if(active!=selected)reason="当前配置选择了 "+selected+"，但运行中的对话仍在使用 "+active+"。\n\n本次消息已阻止。请完全退出并重新打开 Codex，再发送消息。";
        } catch(Exception e) {reason="模型检查失败，本次消息已阻止。\n\n"+e.Message;}
        if(reason!=null) {Console.WriteLine("{\"decision\":\"block\",\"reason\":"+FastJson.Quote(reason)+"}");Console.Out.Flush();}
        // Full index/deletion work is detached and starts only after the decision.
        if(maintenanceDue)try{StartMaintenance(home);}catch(Exception){}
        return 0;
    }
}
