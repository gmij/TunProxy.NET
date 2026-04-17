using System.Diagnostics;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using static TunProxy.Tray.macOS.NativeMethods;

namespace TunProxy.Tray.macOS;

/// <summary>
/// macOS 系统菜单栏托盘应用（NSStatusBar via ObjC Runtime，支持 AOT）
/// </summary>
internal sealed class TrayApp
{
    private const string ApiBase = "http://localhost:50000";

    // Unicode 彩色圆形状态指示
    private const string DotGreen  = "🟢";
    private const string DotRed    = "🔴";
    private const string DotYellow = "🟡";
    private const string DotGray   = "⚫";

    private IntPtr _statusItem;
    private IntPtr _cfLoop;
    private string _buttonTitle = DotGray;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };

    // 静态实例引用（供 ObjC 回调访问）
    private static TrayApp? _instance;

    /// <summary>应用所在目录（单文件发布安全）</summary>
    private static string AppDir =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    public void Run()
    {
        _instance = this;

        // 初始化 NSApplication（必须在主线程）
        var nsAppCls = objc_getClass("NSApplication");
        var sharedApp = msgSend(nsAppCls, sel_registerName("sharedApplication"));

        // 注册 ObjC 委托类（用于菜单 target/action）
        var handlerInstance = RegisterAndCreateHandler();

        // 创建状态栏项
        var statusBar = msgSend(objc_getClass("NSStatusBar"), sel_registerName("systemStatusBar"));
        _statusItem   = msgSend_double(statusBar, sel_registerName("statusItemWithLength:"), -1.0);

        // 设置初始标题
        var button = msgSend(_statusItem, sel_registerName("button"));
        ObjcSetTitle(button, DotGray);

        // 创建并绑定菜单
        var menu = BuildMenu(handlerInstance);
        msgSend_void_ptr(_statusItem, sel_registerName("setMenu:"), menu);

        // 设置 NSApplication 激活策略（Accessory = 不在 Dock 显示）
        msgSend_void_int(sharedApp, sel_registerName("setActivationPolicy:"), 1 /* NSApplicationActivationPolicyAccessory */);

        // 保存主线程 RunLoop 引用，供后台线程更新 UI 使用
        _cfLoop = CFRunLoopGetCurrent();

        // 启动状态轮询（后台线程，每 3 秒）
        _ = Task.Run(PollLoop);

        // 立即轮询一次
        _ = Task.Run(UpdateStatusAsync);

        // 阻塞主线程
        CFRunLoopRun();

        _http.Dispose();
    }

    // ── 菜单构建 ──────────────────────────────────────────────

    private static IntPtr BuildMenu(IntPtr target)
    {
        var menuCls = objc_getClass("NSMenu");
        var menu    = msgSend(menuCls, sel_registerName("new"));

        AddMenuItem(menu, target, "启动服务", "startService");
        AddMenuItem(menu, target, "停止服务", "stopService");
        AddSeparator(menu);
        AddMenuItem(menu, target, "打开控制台", "openConsole");
        AddSeparator(menu);
        AddMenuItem(menu, target, "退出", "quitApp");

        return menu;
    }

    private static void AddMenuItem(IntPtr menu, IntPtr target, string title, string selectorName)
    {
        var itemCls   = objc_getClass("NSMenuItem");
        var allocSel  = sel_registerName("alloc");
        var initSel   = sel_registerName("initWithTitle:action:keyEquivalent:");
        var addSel    = sel_registerName("addItem:");
        var targetSel = sel_registerName("setTarget:");

        var nsTitle  = NSStringFrom(title);
        var actionSel = sel_registerName(selectorName + ":");
        var keyEq    = NSStringFrom("");

        var item = msgSend(itemCls, allocSel);
        item = msgSend_initMenuItem(item, initSel, nsTitle, actionSel, keyEq);

        msgSend_void_ptr(item, targetSel, target);
        msgSend_void_ptr(menu, addSel, item);
    }

    private static void AddSeparator(IntPtr menu)
    {
        var sep = msgSend(objc_getClass("NSMenuItem"), sel_registerName("separatorItem"));
        msgSend_void_ptr(menu, sel_registerName("addItem:"), sep);
    }

    // ── ObjC 委托类注册 ───────────────────────────────────────

    /// <summary>注册 TunProxyHandler ObjC 类并返回实例</summary>
    private static IntPtr RegisterAndCreateHandler()
    {
        var superCls = objc_getClass("NSObject");
        var cls      = objc_allocateClassPair(superCls, "TunProxyHandler", 0);

        // 注册各菜单动作（void method(id self, SEL _cmd, id sender)）
        unsafe
        {
            class_addMethod(cls, sel_registerName("startService:"),
                (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnStartService, "v@:@");
            class_addMethod(cls, sel_registerName("stopService:"),
                (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnStopService, "v@:@");
            class_addMethod(cls, sel_registerName("openConsole:"),
                (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnOpenConsole, "v@:@");
            class_addMethod(cls, sel_registerName("quitApp:"),
                (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnQuit, "v@:@");
        }

        objc_registerClassPair(cls);
        return msgSend(cls, sel_registerName("new"));
    }

    // ── ObjC 回调（UnmanagedCallersOnly，AOT 安全）───────────

    [UnmanagedCallersOnly]
    private static void OnStartService(IntPtr self, IntPtr sel, IntPtr sender)
    {
        var cli = Path.Combine(AppDir, "TunProxy.CLI");
        if (File.Exists(cli))
            Process.Start(new ProcessStartInfo(cli) { UseShellExecute = false });
    }

    [UnmanagedCallersOnly]
    private static void OnStopService(IntPtr self, IntPtr sel, IntPtr sender)
    {
        foreach (var p in Process.GetProcessesByName("TunProxy.CLI"))
        {
            try { p.Kill(); } catch { }
            p.Dispose();
        }
    }

    [UnmanagedCallersOnly]
    private static void OnOpenConsole(IntPtr self, IntPtr sel, IntPtr sender)
    {
        Process.Start(new ProcessStartInfo(ApiBase) { UseShellExecute = true });
    }

    [UnmanagedCallersOnly]
    private static void OnQuit(IntPtr self, IntPtr sel, IntPtr sender)
    {
        _instance?.DoQuit();
    }

    private void DoQuit() => CFRunLoopStop(_cfLoop);

    // ── 状态轮询 ──────────────────────────────────────────────

    private async Task PollLoop()
    {
        while (true)
        {
            await Task.Delay(3000);
            await UpdateStatusAsync();
        }
    }

    private async Task UpdateStatusAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var json = await _http.GetStringAsync($"{ApiBase}/api/status", cts.Token);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            bool running     = root.TryGetProperty("isRunning",     out var r) && r.GetBoolean();
            bool downloading = root.TryGetProperty("isDownloading", out var d) && d.GetBoolean();
            string mode      = root.TryGetProperty("mode",           out var m) ? m.GetString() ?? "" : "";
            int connections  = root.TryGetProperty("activeConnections", out var c) ? c.GetInt32() : 0;

            string title;
            if (downloading)  title = $"{DotYellow} 下载规则库...";
            else if (running)  title = $"{DotGreen} {(mode == "tun" ? "TUN" : "代理")} · {connections}";
            else               title = $"{DotRed} 异常";

            ScheduleUiUpdate(title);
        }
        catch (HttpRequestException) { ScheduleUiUpdate($"{DotGray} 未运行"); }
        catch                        { ScheduleUiUpdate($"{DotGray} 超时");  }
    }

    private void ScheduleUiUpdate(string title)
    {
        if (title == _buttonTitle) return;
        _buttonTitle = title;

        // dispatch_async 到主队列更新 UI
        var block = DispatchBlock.Create(() =>
        {
            var button = msgSend(_statusItem, sel_registerName("button"));
            ObjcSetTitle(button, _buttonTitle);
        });
        dispatch_async_f(dispatch_get_main_queue(), block.FuncPtr, block.InvokePtr);
        // block 对象由 GC 管理，dispatch_async_f 会在回调后自动 release（我们只发射一次）
    }

    // ── 辅助 ──────────────────────────────────────────────────

    private static void ObjcSetTitle(IntPtr button, string title)
    {
        msgSend_void_ptr(button, sel_registerName("setTitle:"), NSStringFrom(title));
    }

    // ── 额外 P/Invoke ─────────────────────────────────────────

    [DllImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr msgSend_initMenuItem(IntPtr obj, IntPtr sel,
        IntPtr title, IntPtr action, IntPtr keyEquivalent);

    [DllImport("libobjc.dylib")]
    private static extern IntPtr objc_allocateClassPair(IntPtr superClass, string name, int extraBytes);

    [DllImport("libobjc.dylib")]
    private static extern void objc_registerClassPair(IntPtr cls);

    [DllImport("libobjc.dylib")]
    private static extern unsafe bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);

    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern IntPtr dispatch_get_main_queue();

    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern void dispatch_async_f(IntPtr queue, IntPtr context, IntPtr work);
}

/// <summary>
/// 最简 dispatch block：将 .NET Action 包装为 C 函数指针可调用的上下文
/// </summary>
internal sealed class DispatchBlock
{
    private readonly Action _action;
    private readonly GCHandle _handle;

    private DispatchBlock(Action action)
    {
        _action = action;
        _handle = GCHandle.Alloc(this);
    }

    public IntPtr FuncPtr  => GCHandle.ToIntPtr(_handle);
    public IntPtr InvokePtr => _invokePtr;

    private static readonly IntPtr _invokePtr;

    static unsafe DispatchBlock()
    {
        _invokePtr = (IntPtr)(delegate* unmanaged<IntPtr, void>)&Invoke;
    }

    [UnmanagedCallersOnly]
    private static void Invoke(IntPtr context)
    {
        var handle = GCHandle.FromIntPtr(context);
        if (handle.Target is DispatchBlock block)
        {
            block._action();
            block._handle.Free();
        }
    }

    public static DispatchBlock Create(Action action) => new(action);
}
