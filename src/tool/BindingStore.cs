using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

// Only the dedicated cache is writable. The client's index is always READONLY.
internal sealed class NativeDatabase : IDisposable {
    internal IntPtr Handle;
    static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
    [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_open_v2(byte[] name, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_close(IntPtr db);
    [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)] static extern IntPtr sqlite3_errmsg(IntPtr db);
    [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_busy_timeout(IntPtr db, int ms);
    [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr arg, out IntPtr error);
    [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)] static extern void sqlite3_free(IntPtr p);
    [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)] internal static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int len, out IntPtr stmt, IntPtr tail);
    [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)] internal static extern int sqlite3_step(IntPtr stmt);
    [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)] internal static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)] internal static extern int sqlite3_bind_text(IntPtr stmt, int index, byte[] value, int bytes, IntPtr destructor);
    [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)] internal static extern int sqlite3_bind_int64(IntPtr stmt, int index, long value);
    [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)] internal static extern int sqlite3_bind_null(IntPtr stmt, int index);
    [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);
    [DllImport("winsqlite3.dll", CallingConvention=CallingConvention.Cdecl)] internal static extern long sqlite3_column_int64(IntPtr stmt, int col);
    internal static byte[] Z(string value) { return Utf8.GetBytes(value + "\0"); }
    internal static string Text(IntPtr value) {
        if (value == IntPtr.Zero) return null;
        int size=0; while (Marshal.ReadByte(value,size)!=0) size++;
        byte[] bytes=new byte[size]; Marshal.Copy(value,bytes,0,size); return Utf8.GetString(bytes);
    }
    internal NativeDatabase(string path, bool writable) {
        if (!writable && !File.Exists(path)) throw new IOException("会话索引暂不可用，已跳过删除清理。");
        int result=sqlite3_open_v2(Z(path),out Handle,writable?6:1,IntPtr.Zero);
        if (result!=0) { string message=Handle==IntPtr.Zero?"无法打开会话记录库。":Text(sqlite3_errmsg(Handle)); Dispose(); throw new IOException(message); }
        sqlite3_busy_timeout(Handle,writable?25:50);
    }
    internal void Check(int result) { if (result!=0) throw new IOException(Text(sqlite3_errmsg(Handle))); }
    internal void Execute(string sql) {
        IntPtr error; int result=sqlite3_exec(Handle,Z(sql),IntPtr.Zero,IntPtr.Zero,out error);
        string message=error==IntPtr.Zero?null:Text(error); if(error!=IntPtr.Zero)sqlite3_free(error);
        if(result!=0)throw new IOException(message??Text(sqlite3_errmsg(Handle)));
    }
    internal NativeStatement Prepare(string sql, params object[] values) { return new NativeStatement(this,sql,values); }
    internal void Run(string sql, params object[] values) { using(NativeStatement s=Prepare(sql,values)) { if(s.Read())throw new IOException("记录库返回了意外结果。"); } }
    internal long Number(string sql, params object[] values) { using(NativeStatement s=Prepare(sql,values))return s.Read()?s.Number(0):0; }
    public void Dispose() { if(Handle!=IntPtr.Zero) { sqlite3_close(Handle); Handle=IntPtr.Zero; } }
}
internal sealed class NativeStatement : IDisposable {
    NativeDatabase db; IntPtr handle;
    internal NativeStatement(NativeDatabase database,string sql,object[] values) {
        db=database; db.Check(NativeDatabase.sqlite3_prepare_v2(db.Handle,NativeDatabase.Z(sql),-1,out handle,IntPtr.Zero));
        try {
            for(int i=0;i<values.Length;i++) {
                object value=values[i]; int result;
                if(value==null)result=NativeDatabase.sqlite3_bind_null(handle,i+1);
                else if(value is string) { byte[] bytes=NativeDatabase.Z((string)value); result=NativeDatabase.sqlite3_bind_text(handle,i+1,bytes,bytes.Length-1,new IntPtr(-1)); }
                else result=NativeDatabase.sqlite3_bind_int64(handle,i+1,Convert.ToInt64(value));
                db.Check(result);
            }
        } catch { Dispose(); throw; }
    }
    internal bool Read() { int result=NativeDatabase.sqlite3_step(handle); if(result==100)return true; if(result==101)return false; db.Check(result); return false; }
    internal string Text(int col) { return NativeDatabase.Text(NativeDatabase.sqlite3_column_text(handle,col)); }
    internal long Number(int col) { return NativeDatabase.sqlite3_column_int64(handle,col); }
    public void Dispose() { if(handle!=IntPtr.Zero) { NativeDatabase.sqlite3_finalize(handle); handle=IntPtr.Zero; } }
}
internal sealed class ThreadEntry {
    internal string Id, Family, Path;
    internal long Recent;
    internal bool Archived, Indexed;
}
internal static class ThreadIndex {
    internal static Dictionary<string,ThreadEntry> Read(string home, string onlyId) {
        var entries=new Dictionary<string,ThreadEntry>(StringComparer.OrdinalIgnoreCase);
        using(var db=new NativeDatabase(System.IO.Path.Combine(home,"state_5.sqlite"),false)) {
            var columns=new HashSet<string>();
            using(var s=db.Prepare("PRAGMA table_info(threads)"))while(s.Read())columns.Add(s.Text(1));
            foreach(string key in new string[]{"id","model_provider","rollout_path","updated_at","archived"})
                if(!columns.Contains(key))throw new IOException("会话索引结构无法确认，已跳过删除清理。");
            var times=new List<string>();
            if(columns.Contains("recency_at_ms"))times.Add("NULLIF(recency_at_ms,0)");
            if(columns.Contains("recency_at"))times.Add("NULLIF(recency_at,0)*1000");
            if(columns.Contains("updated_at_ms"))times.Add("NULLIF(updated_at_ms,0)");
            times.Add("updated_at*1000");
            string sql="SELECT id,model_provider,rollout_path,COALESCE("+String.Join(",",times.ToArray())+",0),archived FROM threads";
            if(onlyId!=null)sql+=" WHERE id=?";
            using(var s=db.Prepare(sql,onlyId==null?new object[0]:new object[]{onlyId}))while(s.Read()) {
                Guid parsed; if(!Guid.TryParse(s.Text(0),out parsed))continue;
                string id=parsed.ToString("D");
                entries[id]=new ThreadEntry{Id=id,Family=ProviderGuard.Family(s.Text(1)),Path=s.Text(2),Recent=s.Number(3),Archived=s.Number(4)!=0,Indexed=true};
            }
        }
        return entries;
    }
}
internal sealed class BindingEntry {
    internal string Id, Family, Path;
    internal long LastUsed, MissingSince, MissingChecks;
    internal bool SeenInIndex;
}
internal sealed class BindingStore : IDisposable {
    internal const int Capacity=200;
    internal const long CleanupInterval=300000; // Opportunistic, at most once per five minutes.
    internal const long DeleteGrace=86400000; // Two successful observations at least 24 hours apart.
    internal static readonly long Epoch=new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).Ticks;
    internal static long Now { get { return (DateTime.UtcNow.Ticks-Epoch)/10000; } }
    internal static string FilePath(string home) { return System.IO.Path.Combine(home,"provider-switch","thread-bindings.sqlite"); }
    NativeDatabase db; Mutex gate; bool locked;
    internal BindingStore(string path) {
        try {
            // Coordination only, not a security checksum: a collision merely
            // serializes two independent databases and never mixes their data.
            ulong token=14695981039346656037UL;
            unchecked { foreach(char c in System.IO.Path.GetFullPath(path).ToUpperInvariant()) {token^=c;token*=1099511628211UL;} }
            gate=new Mutex(false,"Local\\CodexBindingStore_"+token.ToString("X16"));
            try{locked=gate.WaitOne(250);}catch(AbandonedMutexException){locked=true;}
            if(!locked)throw new IOException("会话模型检查正在处理其他消息，请重试。");
            bool exists=File.Exists(path); db=new NativeDatabase(path,true);
            if(!exists) {
                db.Execute("PRAGMA page_size=4096; PRAGMA auto_vacuum=FULL; PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; BEGIN IMMEDIATE;"+
                    "CREATE TABLE bindings(id TEXT PRIMARY KEY NOT NULL,family TEXT NOT NULL CHECK(family IN ('GPT','DeepSeek')),last_used INTEGER NOT NULL,rollout_path TEXT,seen_in_index INTEGER NOT NULL DEFAULT 0,missing_since INTEGER,missing_checks INTEGER NOT NULL DEFAULT 0);"+
                    "CREATE TABLE metadata(key TEXT PRIMARY KEY NOT NULL,value TEXT NOT NULL);"+
                    "PRAGMA application_id=1129337666; PRAGMA user_version=1; COMMIT;");
            } else if(db.Number("PRAGMA application_id")!=1129337666 || db.Number("PRAGMA user_version")!=1)throw new IOException("会话记录库格式无法确认，已阻止发送。");
        } catch { Dispose(); throw; }
    }
    static BindingEntry Row(NativeStatement s) {
        return new BindingEntry{Id=s.Text(0),Family=s.Text(1),LastUsed=s.Number(2),Path=s.Text(3),SeenInIndex=s.Number(4)!=0,MissingSince=s.Number(5),MissingChecks=s.Number(6)};
    }
    const string Fields="id,family,last_used,rollout_path,seen_in_index,COALESCE(missing_since,0),missing_checks";
    internal BindingEntry Find(string id) { using(var s=db.Prepare("SELECT "+Fields+" FROM bindings WHERE id=?",id))return s.Read()?Row(s):null; }
    internal List<BindingEntry> All() { var rows=new List<BindingEntry>(); using(var s=db.Prepare("SELECT "+Fields+" FROM bindings ORDER BY last_used DESC,id DESC"))while(s.Read())rows.Add(Row(s)); return rows; }
    void Prune() { db.Execute("DELETE FROM bindings WHERE id IN (SELECT id FROM bindings ORDER BY last_used DESC,id DESC LIMIT -1 OFFSET 200)"); }
    void Set(string key,string value) { db.Run("INSERT OR REPLACE INTO metadata(key,value) VALUES(?,?)",key,value); }
    internal long MetaNumber(string key) { return db.Number("SELECT CAST(value AS INTEGER) FROM metadata WHERE key=?",key); }
    internal string Touch(string id,string recovered,ThreadEntry source,out bool maintenanceDue) {
        maintenanceDue=false; long now=Now;
        db.Execute("BEGIN IMMEDIATE");
        try {
            BindingEntry existing=Find(id);
            if(existing==null) {
                if(recovered!="GPT" && recovered!="DeepSeek")throw new IOException("无法确认会话原模型，已阻止发送。");
                db.Run("INSERT INTO bindings(id,family,last_used,rollout_path,seen_in_index) VALUES(?,?,?,?,?)",id,recovered,now,source==null?null:source.Path,source!=null&&source.Indexed);
            } else {
                // The original family is immutable, including simultaneous cache misses.
                recovered=existing.Family;
                db.Run("UPDATE bindings SET last_used=?,missing_since=NULL,missing_checks=0 WHERE id=?",Math.Max(now,existing.LastUsed+1),id);
            }
            Prune();
            if(MetaNumber("maintenance_due")<=now) { Set("maintenance_due",(now+CleanupInterval).ToString(System.Globalization.CultureInfo.InvariantCulture)); maintenanceDue=true; }
            db.Execute("COMMIT"); return recovered;
        } catch { try{db.Execute("ROLLBACK");}catch{} throw; }
    }
    // File.Exists hides access and I/O errors. They must never count as deletion.
    static int Presence(string home,string path) {
        if(String.IsNullOrEmpty(path))return -1;
        try {
            string full=System.IO.Path.GetFullPath(path);
            if(full.StartsWith("\\\\?\\",StringComparison.Ordinal))full=full.Substring(4);
            // External/unrecognised locations can be disconnected or moved.
            // Their disappearance is insufficient evidence of permanent deletion.
            string sessions=System.IO.Path.Combine(home,"sessions")+System.IO.Path.DirectorySeparatorChar;
            string archives=System.IO.Path.Combine(home,"archived_sessions")+System.IO.Path.DirectorySeparatorChar;
            if(!full.StartsWith(sessions,StringComparison.OrdinalIgnoreCase)&&!full.StartsWith(archives,StringComparison.OrdinalIgnoreCase))return -1;
            File.GetAttributes(full); return 1;
        }
        catch(FileNotFoundException){return 0;}catch(DirectoryNotFoundException){return 0;}
        catch(IOException){return -1;}catch(UnauthorizedAccessException){return -1;}catch(ArgumentException){return -1;}catch(NotSupportedException){return -1;}
    }
    internal static int Maintain(string home) {
        Dictionary<string,ThreadEntry> index=ThreadIndex.Read(home,null); // Throws on missing, busy, corrupt or incompatible index.
        List<BindingEntry> rows;
        using(var store=new BindingStore(FilePath(home)))rows=store.All();
        var absent=new Dictionary<string,int>();
        foreach(BindingEntry row in rows)if(!index.ContainsKey(row.Id))absent[row.Id]=Presence(home,row.Path);
        bool confirming=false;
        foreach(BindingEntry row in rows)if(row.SeenInIndex && row.MissingSince>0 && row.MissingChecks>=1 && Now-row.MissingSince>=DeleteGrace && !index.ContainsKey(row.Id) && absent[row.Id]==0)confirming=true;
        if(confirming) {
            // Revalidate deletion candidates against a fresh successful index
            // snapshot and filesystem observation immediately before commit.
            index=ThreadIndex.Read(home,null);
            foreach(BindingEntry row in rows)if(!index.ContainsKey(row.Id))absent[row.Id]=Presence(home,row.Path);
        }
        // Filesystem and client-index reads hold no cache lock.
        using(var store=new BindingStore(FilePath(home)))return store.ApplyMaintenance(index,rows,absent);
    }
    int ApplyMaintenance(Dictionary<string,ThreadEntry> index,List<BindingEntry> rows,Dictionary<string,int> absent) {
        long now=Now; int removed=0;
        db.Execute("BEGIN IMMEDIATE");
        try {
            foreach(BindingEntry row in rows) {
                ThreadEntry entry;
                if(index.TryGetValue(row.Id,out entry)) {
                    // Archived rows are present rows. Never treat archived=1 as deleted.
                    db.Run("UPDATE bindings SET rollout_path=?,seen_in_index=1,last_used=MAX(last_used,?),missing_since=NULL,missing_checks=0 WHERE id=? AND last_used=?",entry.Path,entry.Recent,row.Id,row.LastUsed);
                } else if(row.SeenInIndex && absent[row.Id]==0) {
                    if(row.MissingSince>0 && row.MissingChecks>=1 && now-row.MissingSince>=DeleteGrace) {
                        db.Run("DELETE FROM bindings WHERE id=? AND last_used=? AND missing_since=? AND missing_checks=?",row.Id,row.LastUsed,row.MissingSince,row.MissingChecks);
                        removed+=(int)db.Number("SELECT changes()");
                    } else {
                        db.Run("UPDATE bindings SET missing_since=?,missing_checks=missing_checks+1 WHERE id=? AND last_used=? AND COALESCE(missing_since,0)=? AND missing_checks=?",row.MissingSince>0?row.MissingSince:now,row.Id,row.LastUsed,row.MissingSince,row.MissingChecks);
                    }
                } else {
                    db.Run("UPDATE bindings SET missing_since=NULL,missing_checks=0 WHERE id=? AND last_used=?",row.Id,row.LastUsed);
                }
            }
            Prune(); Set("last_maintenance",now.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Set("last_removed",removed.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Set("maintenance_due",(now+CleanupInterval).ToString(System.Globalization.CultureInfo.InvariantCulture));
            db.Execute("COMMIT"); return removed;
        } catch { try{db.Execute("ROLLBACK");}catch{} throw; }
    }
    internal static string Migrate(string home) {
        string destination=FilePath(home);
        if(File.Exists(destination))throw new IOException("记录库已存在，未覆盖。请先检查迁移状态。");
        Dictionary<string,ThreadEntry> index=ThreadIndex.Read(home,null);
        var legacy=new List<BindingEntry>(); string directory=System.IO.Path.Combine(home,"provider-switch","thread-bindings");
        if(Directory.Exists(directory))foreach(string file in Directory.GetFiles(directory,"*.txt")) {
            Guid id; string family=File.ReadAllText(file).Trim();
            if(!Guid.TryParse(System.IO.Path.GetFileNameWithoutExtension(file),out id) || (family!="GPT"&&family!="DeepSeek"))throw new IOException("旧会话记录格式错误，已停止迁移。");
            ThreadEntry entry; index.TryGetValue(id.ToString("D"),out entry);
            legacy.Add(new BindingEntry{Id=id.ToString("D"),Family=family,LastUsed=entry==null?(File.GetLastWriteTimeUtc(file).Ticks-Epoch)/10000:entry.Recent,Path=entry==null?null:entry.Path,SeenInIndex=entry!=null});
        }
        if(legacy.Count==0)foreach(ThreadEntry entry in index.Values)if(entry.Family!=null)legacy.Add(new BindingEntry{Id=entry.Id,Family=entry.Family,LastUsed=entry.Recent,Path=entry.Path,SeenInIndex=true});
        legacy.Sort(delegate(BindingEntry a,BindingEntry b){int order=b.LastUsed.CompareTo(a.LastUsed);return order!=0?order:StringComparer.Ordinal.Compare(b.Id,a.Id);});
        string temporary=destination+".migrating-"+Guid.NewGuid().ToString("N"); int kept=Math.Min(Capacity,legacy.Count),archived=0;
        try {
            using(var store=new BindingStore(temporary)) {
                store.db.Execute("BEGIN IMMEDIATE");
                for(int i=0;i<kept;i++) {
                    BindingEntry entry=legacy[i];
                    store.db.Run("INSERT INTO bindings(id,family,last_used,rollout_path,seen_in_index) VALUES(?,?,?,?,?)",entry.Id,entry.Family,entry.LastUsed,entry.Path,entry.SeenInIndex);
                    ThreadEntry original;if(index.TryGetValue(entry.Id,out original)&&original.Archived)archived++;
                }
                store.Set("migration_imported",legacy.Count.ToString());store.Set("migration_evicted",(legacy.Count-kept).ToString());store.Set("maintenance_due",(Now+CleanupInterval).ToString());
                store.db.Execute("COMMIT");
                List<BindingEntry> actual=store.All();if(actual.Count!=kept)throw new IOException("迁移数量校验失败。");
                for(int i=0;i<kept;i++)if(actual[i].Id!=legacy[i].Id||actual[i].Family!=legacy[i].Family||actual[i].LastUsed!=legacy[i].LastUsed)throw new IOException("迁移内容校验失败。");
                using(var s=store.db.Prepare("PRAGMA integrity_check"))if(!s.Read()||s.Text(0)!="ok")throw new IOException("迁移记录库完整性校验失败。");
            }
            File.Move(temporary,destination);
        } finally { if(File.Exists(temporary))File.Delete(temporary); }
        return "{\"imported\":"+legacy.Count+",\"kept\":"+kept+",\"capacity_evicted\":"+(legacy.Count-kept)+",\"archived_kept\":"+archived+",\"database_bytes\":"+new FileInfo(destination).Length+"}";
    }
    internal string Status() { return "{\"count\":"+db.Number("SELECT COUNT(*) FROM bindings")+",\"limit\":200,\"pending_deletion\":"+db.Number("SELECT COUNT(*) FROM bindings WHERE missing_since IS NOT NULL")+",\"last_maintenance\":"+MetaNumber("last_maintenance")+",\"last_removed\":"+MetaNumber("last_removed")+"}"; }
    public void Dispose() {
        if(db!=null) { db.Dispose();db=null; }
        if(gate!=null) { if(locked) {gate.ReleaseMutex();locked=false;} gate.Dispose();gate=null; }
    }
}
