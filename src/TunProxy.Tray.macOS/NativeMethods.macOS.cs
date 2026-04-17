using System.Runtime.InteropServices;

namespace TunProxy.Tray.macOS;

/// <summary>
/// ObjC runtime 和 CoreFoundation P/Invoke
/// </summary>
internal static unsafe class NativeMethods
{
    private const string LibObjC = "libobjc.dylib";
    private const string LibCF   = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string LibAppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";

    // ── ObjC Runtime ──────────────────────────────────────────

    [DllImport(LibObjC)]
    public static extern IntPtr objc_getClass(string name);

    [DllImport(LibObjC)]
    public static extern IntPtr sel_registerName(string name);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr msgSend(IntPtr obj, IntPtr sel);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr msgSend_ptr(IntPtr obj, IntPtr sel, IntPtr arg1);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr msgSend_double(IntPtr obj, IntPtr sel, double arg1);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr msgSend_int(IntPtr obj, IntPtr sel, int arg1);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr msgSend_bool(IntPtr obj, IntPtr sel, bool arg1);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static extern void msgSend_void(IntPtr obj, IntPtr sel);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static extern void msgSend_void_ptr(IntPtr obj, IntPtr sel, IntPtr arg1);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static extern void msgSend_void_int(IntPtr obj, IntPtr sel, int arg1);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr msgSend_ptr_ptr(IntPtr obj, IntPtr sel, IntPtr a1, IntPtr a2);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr msgSend_ptr_int(IntPtr obj, IntPtr sel, IntPtr a1, int a2);

    // ── CoreFoundation ────────────────────────────────────────

    [DllImport(LibCF)]
    public static extern void CFRunLoopRun();

    [DllImport(LibCF)]
    public static extern IntPtr CFRunLoopGetCurrent();

    [DllImport(LibCF)]
    public static extern void CFRunLoopStop(IntPtr loop);

    // ── NSString helpers ──────────────────────────────────────

    /// <summary>从 .NET string 创建 NSString</summary>
    public static IntPtr NSStringFrom(string s)
    {
        var cls   = objc_getClass("NSString");
        var init  = sel_registerName("stringWithUTF8String:");
        var bytes = System.Text.Encoding.UTF8.GetBytes(s + "\0");
        fixed (byte* p = bytes)
            return msgSend_ptr(cls, init, (IntPtr)p);
    }

    /// <summary>初始化 NSApplication（必须在主线程上调用）</summary>
    public static void InitNSApp()
    {
        var cls = objc_getClass("NSApplication");
        var sel = sel_registerName("sharedApplication");
        msgSend(cls, sel);
    }
}
