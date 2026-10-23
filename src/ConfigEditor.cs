using System;
using System.IO;
using System.Text;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Web.Script.Serialization;
using System.Runtime.InteropServices;

public static class ConfigEditor {
    static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
    static object Call(string method,params object[] values) {
        try{return typeof(SwitchCodexProvider).GetMethod(method,BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,values);}
        catch(TargetInvocationException e){throw e.InnerException;}
    }
    static object Field(object target,string name){return target.GetType().GetField(name).GetValue(target);}
    static List<object> Items(string text){var list=new List<object>();foreach(object item in (IEnumerable)Field(Call("Parse",text),"Items"))list.Add(item);return list;}
    static bool PathIs(object item,params string[] path){string[] actual=(string[])Field(item,"Path");if(actual==null||actual.Length!=path.Length)return false;for(int i=0;i<path.Length;i++)if(actual[i]!=path[i])return false;return true;}
    static string NL(string text){return (string)Call("NewLine",text);}
    static string TableKey(string value){
        if(value.IndexOf('\'')>=0)throw new IOException("Hook trust key contains an unsupported single quote.");
        return "'"+value+"'";
    }
    public static string RootString(string text,string key){return (string)Call("Get",Call("Parse",text),key);}
    public static bool HasTable(string text,params string[] path){foreach(object item in Items(text))if((string)Field(item,"Kind")=="table"&&PathIs(item,path))return true;return false;}
    public static void Validate(string text){object doc=Call("Parse",text);Call("ValidateManaged",doc,Call("FindBlock",doc,"# BEGIN CODEX DEEPSEEK SWITCH (managed)","# END CODEX DEEPSEEK SWITCH (managed)"),Call("FindBlock",doc,"# BEGIN CODEX DEEPSEEK PROVIDER (managed, always available)","# END CODEX DEEPSEEK PROVIDER (managed, always available)"));}
    const string Begin="# BEGIN CODEX PROVIDER GUARD (managed)",End="# END CODEX PROVIDER GUARD (managed)";
    public static string RemoveGuardBlock(string text){object block=Call("FindBlock",Call("Parse",text),Begin,End);return block==null?text:text.Remove((int)Field(block,"Start"),(int)Field(block,"End")-(int)Field(block,"Start"));}
    public static string RemoveTrust(string text,string key){
        var cuts=new List<int[]>();bool selected=false;
        foreach(object item in Items(text)){
            string kind=(string)Field(item,"Kind");
            if(kind=="table"){selected=PathIs(item,"hooks","state",key);if(selected)cuts.Add(new int[]{(int)Field(item,"Start"),(int)Field(item,"End")});}
            else if(selected && kind=="field")cuts.Add(new int[]{(int)Field(item,"Start"),(int)Field(item,"End")});
        }
        cuts.Sort(delegate(int[] a,int[] b){return b[0].CompareTo(a[0]);});foreach(int[] cut in cuts)text=text.Remove(cut[0],cut[1]-cut[0]);return text;
    }
    public static string AddTrust(string text,string key,string hash){text=RemoveGuardBlock(RemoveTrust(text,key));string nl=NL(text);return text+(text.Length>0&&!text.EndsWith("\n")?nl:"")+Begin+nl+"[hooks.state."+TableKey(key)+"]"+nl+"trusted_hash = "+Json.Serialize(hash)+nl+End+nl;}
    public static string RemapTrust(string text,string source,string slug,int group,int handler,bool wholeGroup){
        string prefix=source+":"+slug+":";var changes=new List<object[]>();
        foreach(object item in Items(text)){
            if((string)Field(item,"Kind")!="table")continue;string[] path=(string[])Field(item,"Path");
            if(path.Length!=3||path[0]!="hooks"||path[1]!="state"||!path[2].StartsWith(prefix,StringComparison.OrdinalIgnoreCase))continue;
            string[] numbers=path[2].Substring(prefix.Length).Split(':');int g,h;if(numbers.Length!=2||!Int32.TryParse(numbers[0],out g)||!Int32.TryParse(numbers[1],out h))continue;
            if(wholeGroup&&g>group)g--;else if(!wholeGroup&&g==group&&h>handler)h--;else continue;
            string header="[hooks.state."+TableKey(prefix+g+":"+h)+"]"+NL(text);
            changes.Add(new object[]{(int)Field(item,"Start"),(int)Field(item,"End"),header});
        }
        changes.Sort(delegate(object[] a,object[] b){return ((int)b[0]).CompareTo((int)a[0]);});foreach(object[] c in changes)text=text.Remove((int)c[0],(int)c[1]-(int)c[0]).Insert((int)c[0],(string)c[2]);return text;
    }
    public static string RemoveEmptyDesktop(string text){
        object header=null;bool populated=false,inside=false;
        foreach(object item in Items(text)){
            string kind=(string)Field(item,"Kind");if(kind=="table"){
                string[] p=(string[])Field(item,"Path");inside=PathIs(item,"desktop");if(inside)header=item;else if(p.Length>1&&p[0]=="desktop")populated=true;
            }else if(inside&&kind=="field")populated=true;
        }
        return header!=null&&!populated?text.Remove((int)Field(header,"Start"),(int)Field(header,"End")-(int)Field(header,"Start")):text;
    }
    public static string ProviderBlock(string text){string nl=NL(text);return (text.Length>0&&!text.EndsWith("\n")?nl:"")+"# BEGIN CODEX DEEPSEEK PROVIDER (managed, always available)"+nl+"[model_providers.deepseek]"+nl+"name = \"DeepSeek\""+nl+"base_url = \"https://api.deepseek.com/\""+nl+"wire_api = \"responses\""+nl+"env_key = \"DEEPSEEK_API_KEY\""+nl+"# END CODEX DEEPSEEK PROVIDER (managed, always available)"+nl;}
    static object ReadResponse(Process p,int id){
        DateTime deadline=DateTime.UtcNow.AddSeconds(15);
        while(DateTime.UtcNow<deadline){var task=p.StandardOutput.ReadLineAsync();if(!task.Wait(Math.Max(1,(int)(deadline-DateTime.UtcNow).TotalMilliseconds)))throw new TimeoutException("客户端配置检查超时。");string line=task.Result;if(line==null)throw new IOException("客户端配置检查提前退出。");var response=Json.DeserializeObject(line) as Dictionary<string,object>;object value;if(response!=null&&response.TryGetValue("id",out value)&&Convert.ToInt32(value)==id){if(response.ContainsKey("error"))throw new IOException("当前客户端不支持模型检查管理，请先更新客户端。");return response["result"];}}
        throw new TimeoutException("客户端配置检查超时。");
    }
    public static string HookInfo(string cli,string home,string command){
        var info=new ProcessStartInfo(cli,"app-server --stdio"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=home,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
        info.EnvironmentVariables["CODEX_HOME"]=home;
        using(Process p=Process.Start(info)){
            var stderr=p.StandardError.ReadToEndAsync();
            try{
                p.StandardInput.WriteLine(Json.Serialize(new{ id=1,method="initialize",@params=new{ clientInfo=new{name="provider-switch-setup",version="1.5.0"},capabilities=new{experimentalApi=true,explicitGatewayOauth=true}}}));p.StandardInput.Flush();ReadResponse(p,1);
                p.StandardInput.WriteLine("{\"method\":\"initialized\"}");
                p.StandardInput.WriteLine(Json.Serialize(new{id=2,method="hooks/list",@params=new{cwds=new string[]{home}}}));p.StandardInput.Flush();
                var result=(Dictionary<string,object>)ReadResponse(p,2);
                foreach(object item in (object[])result["data"]){var entry=(Dictionary<string,object>)item;foreach(object value in (object[])entry["hooks"]){var hook=(Dictionary<string,object>)value;object cmd;if(hook.TryGetValue("command",out cmd)&&(string)cmd==command&&String.Equals((string)hook["sourcePath"],Path.Combine(home,"hooks.json"),StringComparison.OrdinalIgnoreCase)){if(!(bool)hook["enabled"])throw new IOException("客户端已禁用此检查，安装未完成。");return Json.Serialize(hook);}}}
                throw new IOException("客户端未识别到模型检查，安装已中止。");
            }finally{try{p.StandardInput.Close();if(!p.WaitForExit(2000)){p.Kill();p.WaitForExit(2000);}}catch{}}
        }
    }
    public static bool ClientRunning(){foreach(string name in new string[]{"Codex","ChatGPT"})foreach(Process p in Process.GetProcessesByName(name)){using(p)try{string path=p.MainModule.FileName;if(p.MainWindowHandle!=IntPtr.Zero&&(path.IndexOf("Codex",StringComparison.OrdinalIgnoreCase)>=0||path.IndexOf("OpenAI.Codex_",StringComparison.OrdinalIgnoreCase)>=0))return true;}catch{}}return false;}
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]static extern void SHChangeNotify(uint evt,uint flags,string path,IntPtr second);
    public static void ShortcutDeleted(string path){SHChangeNotify(4,0x3005,path,IntPtr.Zero);}
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern IntPtr SendMessageTimeout(IntPtr w,uint msg,UIntPtr a,string b,uint flags,uint timeout,out UIntPtr result);
    public static void EnvironmentChanged(){UIntPtr result;SendMessageTimeout(new IntPtr(0xffff),0x1a,UIntPtr.Zero,"Environment",2,1000,out result);}
}
