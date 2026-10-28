using System;
using System.Text;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

// Load/save through Shell Link itself so property-store identities survive edits.
internal sealed class NativeShortcut : IDisposable
{
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] class ShellLink { }
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLink {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, IntPtr data, uint flags);
        void GetIDList(out IntPtr list); void SetIDList(IntPtr list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int count);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int count);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int count);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetHotkey(out short key); void SetHotkey(short key);
        void GetShowCmd(out int command); void SetShowCmd(int command);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int count, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string text, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr owner, uint flags); void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
    [StructLayout(LayoutKind.Sequential)] struct Key { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Sequential)] struct Variant { public ushort Type, A, B, C; public IntPtr Value, Reserved; }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore {
        void GetCount(out uint count); void GetAt(uint index, out Key key);
        void GetValue(ref Key key, out Variant value); void SetValue(ref Key key, ref Variant value); void Commit();
    }
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref Variant value);
    object instance; IShellLink link;
    public NativeShortcut(string path, bool writable = false) { instance = new ShellLink(); link = (IShellLink)instance; ((IPersistFile)instance).Load(path, writable ? 2 : 0); }
    public string Target { get { StringBuilder text = new StringBuilder(32768); link.GetPath(text, text.Capacity, IntPtr.Zero, 4); return text.ToString(); } }
    public string Arguments { get { StringBuilder text = new StringBuilder(32768); link.GetArguments(text, text.Capacity); return text.ToString(); } }
    public string Icon { get { StringBuilder text = new StringBuilder(32768); int index; link.GetIconLocation(text, text.Capacity, out index); return text + "," + index; } }
    static Key IdentityKey { get { return new Key { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 }; } }
    public string Identity {
        get { Key key = IdentityKey; Variant value; ((IPropertyStore)instance).GetValue(ref key, out value);
            try { return value.Type == 31 ? Marshal.PtrToStringUni(value.Value) : null; } finally { PropVariantClear(ref value); } }
        set { Key key = IdentityKey; Variant v = new Variant { Type = 31, Value = Marshal.StringToCoTaskMemUni(value) };
            try { ((IPropertyStore)instance).SetValue(ref key, ref v); ((IPropertyStore)instance).Commit(); } finally { PropVariantClear(ref v); } }
    }
    public void SetIcon(string path) { link.SetIconLocation(path, 0); }
    public void Save(string path) { ((IPersistFile)instance).Save(path, true); }
    public void Dispose() { if (instance != null) { Marshal.FinalReleaseComObject(instance); instance = null; link = null; } }
}
