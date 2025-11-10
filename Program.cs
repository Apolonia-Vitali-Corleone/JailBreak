using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Windows.Forms;
using Microsoft.Win32;

/// <summary>
/// ========================= 项目说明(请先阅读) =========================
/// 这个 WinForms 托盘小工具用于在 Windows 上快速切换全局系统代理。
///
/// 主要做法:
///  - 通过修改注册表 HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings 下的键值来启/停代理:
///      * ProxyEnable (DWORD)         : 0 关闭 / 1 开启全局代理
///      * ProxyServer (STRING)        : 形如 "host:port" 的代理地址
///      * ProxyOverride (STRING)      : 分号分隔的绕行名单(不走代理),典型包含 localhost;127.0.0.1;<local>
///  - 通过调用 WinINet 的 InternetSetOption 通知系统"设置已变更"并刷新(否则部分程序不会立即生效)。
///  - 托盘图标红/黑颜色指示当前代理状态,双击或菜单可一键切换。
///
/// 配置文件:
///  - proxy-bypass.txt : 全局代理模式下的绕行名单(ProxyOverride),每行一条,支持注释行(#或//开头)。
///
/// 适用范围/注意:
///  - 该方案修改的是 WinINet 代理设置,多数浏览器(IE/旧版Edge/Chrome/Edge Chromium 默认遵循)会跟随;
///    但个别程序可能使用自带网络栈或 WinHTTP 独立配置,不一定生效(如某些 Electron/Java 应用)。
///  - 修改注册表需要当前用户权限;若被组策略强制覆盖,设置可能被还原。
/// ======================================================================
/// </summary>
internal static class Program
{
    // === 你的代理参数 ===
    // 上游代理地址:形如 "IP:Port" 或 "host:port"。
    private const string ProxyServer = "10.0.0.1:38964";

    // Windows 系统代理配置所在注册表路径(当前用户作用域)
    private const string RegPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    // 全局代理下的"绕行名单"配置文件
    // 当启用全局代理(ProxyEnable=1)时,ProxyOverride 中的主机/域名将直连(不走代理)。
    private static readonly string ProxyBypassPath = Path.Combine(AppContext.BaseDirectory, "proxy-bypass.txt");

    // 当找不到 proxy-bypass.txt 或读取失败时,使用此默认绕行串。
    private const string DefaultProxyBypass = "localhost;127.0.0.1;<local>"; // <local> 代表本地域名(无点的主机名)

    // ===== 调用 WinINet 通知系统设置已变化(不调用的话,部分应用不会立刻感知变更) =====
    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
    private const int INTERNET_OPTION_SETTINGS_CHANGED = 39; // 通知:设置已变化
    private const int INTERNET_OPTION_REFRESH = 37; // 请求:刷新代理设置缓存

    // 托盘图标与资源引用
    private static NotifyIcon? _ni;
    private static Icon? _iconOn;   // 红色圆点(代理已启用)
    private static Icon? _iconOff;  // 黑色圆点(代理关闭)

    // 从 Bitmap.GetHicon 得到的句柄需要 DestroyIcon 以释放 GDI 资源
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);

    [STAThread]
    static void Main()
    {
        // WinForms 基础初始化
        ApplicationConfiguration.Initialize();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 生成两种状态的托盘图标:红(启用) 与 黑(关闭)
        _iconOn = CreateStateIcon(Color.Red);
        _iconOff = CreateStateIcon(Color.Black);

        // 创建托盘图标与右键菜单
        _ni = new NotifyIcon
        {
            Text = "Proxy: OFF",                           // 初始提示文本
            Icon = _iconOff ?? SystemIcons.Application,     // 初始图标(关闭态)
            Visible = true,
            ContextMenuStrip = BuildMenu()                  // 右键菜单
        };

        // 双击托盘图标 = 快速切换全局代理开/关
        _ni.DoubleClick += (_, __) => ToggleProxy();

        // 应用退出时做资源清理
        Application.ApplicationExit += OnAppExit;

        // 根据当前注册表状态刷新 GUI 显示(状态文本/图标)
        UpdateVisual();
        Application.Run();
    }

    // ==== 托盘与清理 ====
    /// <summary>
    /// 应用退出时释放托盘图标、Icon 资源,避免资源泄漏。
    /// </summary>
    private static void OnAppExit(object? sender, EventArgs e)
    {
        if (_ni != null)
        {
            _ni.Visible = false;  // 隐藏托盘图标(防止残留)
            _ni.Dispose();        // 释放组件
        }
        _iconOn?.Dispose();
        _iconOff?.Dispose();
    }

    /// <summary>
    /// 构建托盘右键菜单(全局代理相关操作)。
    /// </summary>
    private static ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        // 全局代理(ProxyEnable=1;ProxyOverride 为绕行名单)
        var enable = new ToolStripMenuItem("开启全局代理", null, (_, __) => EnableProxy());
        var disable = new ToolStripMenuItem("关闭全局代理", null, (_, __) => DisableProxy());
        var toggle = new ToolStripMenuItem("切换开/关(双击托盘也可)", null, (_, __) => ToggleProxy());
        var apply = new ToolStripMenuItem("仅应用最新绕行规则", null, (_, __) => ApplyBypassOnly());

        var openCfg = new ToolStripMenuItem("打开配置目录", null, (_, __) => System.Diagnostics.Process.Start("explorer.exe", AppContext.BaseDirectory));
        var exit = new ToolStripMenuItem("退出", null, (_, __) => Application.Exit());

        menu.Items.Add(enable);
        menu.Items.Add(disable);
        menu.Items.Add(toggle);
        menu.Items.Add(apply);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(openCfg);
        menu.Items.Add(exit);
        return menu;
    }

    // ==== 全局代理(传统 WinINet 代理)====
    /// <summary>
    /// 启用"全局代理"模式:
    ///  - 确保没有PAC配置(删除 AutoConfigURL,避免冲突)
    ///  - ProxyEnable = 1,写入 ProxyServer 与 ProxyOverride(绕行名单)
    ///  - 通知系统设置变更并刷新缓存
    /// </summary>
    private static void EnableProxy()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: true)!;

        // 确保关闭 PAC(避免与全局代理冲突)
        key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
        key.SetValue("AutoDetect", 0, RegistryValueKind.DWord);

        // 开启全局代理
        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", ProxyServer, RegistryValueKind.String);
        key.SetValue("ProxyOverride", LoadProxyBypass(), RegistryValueKind.String);

        RefreshWinInet();
        UpdateVisual();
    }

    /// <summary>
    /// 仅更新全局代理的绕行名单(ProxyOverride),不改变开关状态。
    /// </summary>
    private static void ApplyBypassOnly()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: true)!;
        key.SetValue("ProxyOverride", LoadProxyBypass(), RegistryValueKind.String);
        RefreshWinInet();
        UpdateVisual();
    }

    /// <summary>
    /// 关闭全局代理(保留 ProxyServer/ProxyOverride 值,但不生效)。
    /// </summary>
    private static void DisableProxy()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: true)!;
        key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        RefreshWinInet();
        UpdateVisual();
    }

    /// <summary>
    /// 快速切换:如果当前已开启全局代理则关闭,否则开启。
    /// </summary>
    private static void ToggleProxy()
    {
        if (IsProxyOn()) DisableProxy(); else EnableProxy();
    }

    /// <summary>
    /// 读取注册表判断全局代理是否开启(ProxyEnable != 0)。
    /// </summary>
    private static bool IsProxyOn()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: false)!;
        var val = key.GetValue("ProxyEnable", 0);
        return val is int i && i != 0;
    }

    // ==== 读取配置 ====
    /// <summary>
    /// 读取 proxy-bypass.txt,过滤空行与注释行,去重后拼为分号分隔串,供 ProxyOverride 使用。
    /// </summary>
    private static string LoadProxyBypass()
    {
        try
        {
            if (!File.Exists(ProxyBypassPath)) return DefaultProxyBypass;
            var lines = File.ReadAllLines(ProxyBypassPath);
            var items = new List<string>();
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;                 // 跳过空行
                if (line.StartsWith("#") || line.StartsWith("//")) continue; // 跳过注释
                items.Add(line);
            }
            var uniq = new HashSet<string>(items, StringComparer.OrdinalIgnoreCase);
            var joined = string.Join(";", uniq);               // WinINet 需要分号分隔
            return joined.Replace(";;", ";");
        }
        catch { return DefaultProxyBypass; }                    // 读取失败则回退到默认绕行
    }

    // ==== 系统刷新与状态展示 ====
    /// <summary>
    /// 通知 WinINet:代理设置已变化,并请求刷新,使设置即时生效。
    /// </summary>
    private static void RefreshWinInet()
    {
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
    }

    /// <summary>
    /// 根据当前注册表状态更新托盘图标与提示文本:
    ///  - 全局代理开启:显示红点 + "Proxy: ON (host:port)"
    ///  - 代理关闭:显示黑点 + "Proxy: OFF"
    /// </summary>
    private static void UpdateVisual()
    {
        if (_ni == null) return;

        if (IsProxyOn())
        {
            _ni.Icon = _iconOn ?? SystemIcons.Application;
            _ni.Text = $"Proxy: ON ({ProxyServer})";
        }
        else
        {
            _ni.Icon = _iconOff ?? SystemIcons.Application;
            _ni.Text = "Proxy: OFF";
        }
    }

    /// <summary>
    /// 动态绘制一个小圆点图标(红/黑),避免外置 .ico 资源依赖。
    /// 注意:GetHicon 产生的句柄需使用 DestroyIcon 释放(本方法在克隆后即释放原句柄)。
    /// </summary>
    private static Icon CreateStateIcon(Color fillColor)
    {
        var size = System.Windows.Forms.SystemInformation.SmallIconSize; // 系统推荐的小图标尺寸
        using var bmp = new Bitmap(size.Width, size.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            int pad = Math.Max(2, size.Width / 8);
            var rect = new Rectangle(pad, pad, size.Width - pad * 2, size.Height - pad * 2);
            using var fill = new SolidBrush(fillColor);
            using var pen = new Pen(Color.Black, 1);
            g.FillEllipse(fill, rect);
            g.DrawEllipse(pen, rect);
        }
        IntPtr hIcon = bmp.GetHicon();
        try { using var tmp = Icon.FromHandle(hIcon); return (Icon)tmp.Clone(); }
        finally { DestroyIcon(hIcon); } // 释放原始句柄,避免 GDI 句柄泄漏
    }
}
