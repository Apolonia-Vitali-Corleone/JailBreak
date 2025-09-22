using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;
using System.Windows.Forms;
using Microsoft.Win32;

/// <summary>
/// ========================= 项目说明（请先阅读） =========================
/// 这个 WinForms 托盘小工具用于在 Windows 上快速切换两种代理形态：
///  1) 全局系统代理（WinINet 代理）：所有 WinINet/WinHTTP 走系统代理，支持绕行名单（ProxyOverride）。
///  2) PAC 分流：默认直连，仅命中名单（域名/IP/CIDR）时通过上游代理。
///
/// 主要做法：
///  - 通过修改注册表 HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings 下的键值来启/停代理：
///      * ProxyEnable (DWORD)         : 0 关闭 / 1 开启全局代理
///      * ProxyServer (STRING)        : 形如 "host:port" 的代理地址
///      * ProxyOverride (STRING)      : 分号分隔的绕行名单（不走代理），典型包含 localhost;127.0.0.1;<local>
///      * AutoConfigURL (STRING)      : 指向 PAC 文件的 file:/// URL，存在即启用 PAC
///      * AutoDetect (DWORD)          : 是否启用 WPAD 自动发现，通常置 0 以避免干扰
///  - 通过调用 WinINet 的 InternetSetOption 通知系统"设置已变更"并刷新（否则部分程序不会立即生效）。
///  - 托盘图标红/黑颜色指示当前代理/PAC 状态，双击或菜单可一键切换。
///
/// 配置文件：
///  - proxy-bypass.txt : 全局代理模式下的绕行名单（ProxyOverride），每行一条，支持注释行(#或//开头)。
///  - proxy-via.txt    : PAC 分流下需要经代理的域名/IP/CIDR 列表，每行一条，支持注释行。
///  - proxy-split.pac  : 程序根据 proxy-via.txt 自动生成的 PAC 文件（默认写在程序目录）。
///
/// 适用范围/注意：
///  - 该方案修改的是 WinINet 代理设置，多数浏览器(IE/旧版Edge/Chrome/Edge Chromium 默认遵循)会跟随；
///    但个别程序可能使用自带网络栈或 WinHTTP 独立配置，不一定生效（如某些 Electron/Java 应用）。
///  - 修改注册表需要当前用户权限；若被组策略强制覆盖，设置可能被还原。
///  - PAC/全局不可同时开启，代码在启用一方时会显式关闭另一方以避免冲突。
///  - 仅处理 IPv4 CIDR；若需 IPv6，可扩展 TryParseCidr 与 PAC 逻辑。
/// ======================================================================
/// </summary>
internal static class Program
{
    // === 你的代理参数 ===
    // 上游代理地址：形如 "IP:Port" 或 "host:port"。这里假定 10.0.0.1:14444 是 WG(或 Clash) 上的本地/内网代理入口。
    private const string ProxyServer = "10.0.0.1:38964"; // 走 WG 的上游代理

    // Windows 系统代理配置所在注册表路径（当前用户作用域）
    private const string RegPath     = @"Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings";

    // —— 全局代理下的“绕行名单”配置文件 ——
    // 当启用全局代理(ProxyEnable=1)时，ProxyOverride 中的主机/域名将直连（不走代理）。
    private static readonly string ProxyBypassPath = Path.Combine(AppContext.BaseDirectory, "proxy-bypass.txt");

    // 当找不到 proxy-bypass.txt 或读取失败时，使用此默认绕行串。
    private const string DefaultProxyBypass = "localhost;127.0.0.1;<local>"; // <local> 代表本地域名（无点的主机名）

    // —— PAC 分流相关文件 ——
    // PacPath       : 生成的 PAC 文件保存位置（使用 file:/// 形式写入 AutoConfigURL）
    // ProxyViaPath  : 需要“走代理”的域名/IP/CIDR 列表，仅命中时才通过代理，其余直连。
    private static readonly string PacPath      = Path.Combine(AppContext.BaseDirectory, "proxy-split.pac");
    private static readonly string ProxyViaPath = Path.Combine(AppContext.BaseDirectory, "proxy-via.txt");

    // ===== 调用 WinINet 通知系统设置已变化（不调用的话，部分应用不会立刻感知变更） =====
    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
    private const int INTERNET_OPTION_SETTINGS_CHANGED = 39; // 通知：设置已变化
    private const int INTERNET_OPTION_REFRESH          = 37; // 请求：刷新代理设置缓存

    // 托盘图标与资源引用
    private static NotifyIcon? _ni;
    private static Icon? _iconOn;   // 红色圆点（代理/PAC 已启用）
    private static Icon? _iconOff;  // 黑色圆点（全部关闭）

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

        // 生成两种状态的托盘图标：红(启用) 与 黑(关闭)
        _iconOn  = CreateStateIcon(Color.Red);
        _iconOff = CreateStateIcon(Color.Black);

        // 创建托盘图标与右键菜单
        _ni = new NotifyIcon
        {
            Text = "Proxy: OFF",                           // 初始提示文本
            Icon = _iconOff ?? SystemIcons.Application,     // 初始图标（关闭态）
            Visible = true,
            ContextMenuStrip = BuildMenu()                  // 右键菜单
        };

        // 双击托盘图标 = 快速切换全局代理开/关
        _ni.DoubleClick += (_, __) => ToggleProxy();

        // 应用退出时做资源清理
        Application.ApplicationExit += OnAppExit;

        // 根据当前注册表状态刷新 GUI 显示（状态文本/图标）
        UpdateVisual();
        Application.Run();
    }

    // ==== 托盘与清理 ====
    /// <summary>
    /// 应用退出时释放托盘图标、Icon 资源，避免资源泄漏。
    /// </summary>
    private static void OnAppExit(object? sender, EventArgs e)
    {
        if (_ni != null)
        {
            _ni.Visible = false;  // 隐藏托盘图标（防止残留）
            _ni.Dispose();        // 释放组件
        }
        _iconOn?.Dispose();
        _iconOff?.Dispose();
    }

    /// <summary>
    /// 构建托盘右键菜单（PAC 分流、全局代理、切换、仅更新规则、打开目录、退出）。
    /// </summary>
    private static ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        // PAC 分流（默认直连，仅命中列表走代理）
        var pacOn  = new ToolStripMenuItem("启用分流（PAC：默认直连）", null, (_, __) => EnablePacSplit());
        var pacUpd = new ToolStripMenuItem("仅更新分流规则（PAC）",   null, (_, __) => UpdatePacOnly());
        var pacOff = new ToolStripMenuItem("关闭分流（PAC）",       null, (_, __) => DisablePac());

        // 全局代理（ProxyEnable=1；ProxyOverride 为绕行名单）
        var enable  = new ToolStripMenuItem("开启全局代理",           null, (_, __) => EnableProxy());
        var disable = new ToolStripMenuItem("关闭全局代理",           null, (_, __) => DisableProxy());
        var toggle  = new ToolStripMenuItem("切换开/关（双击托盘也可）", null, (_, __) => ToggleProxy());
        var apply   = new ToolStripMenuItem("仅应用最新绕行规则",       null, (_, __) => ApplyBypassOnly());

        var openCfg = new ToolStripMenuItem("打开配置目录",           null, (_, __) => System.Diagnostics.Process.Start("explorer.exe", AppContext.BaseDirectory));
        var exit    = new ToolStripMenuItem("退出",                   null, (_, __) => Application.Exit());

        menu.Items.Add(pacOn);
        menu.Items.Add(pacUpd);
        menu.Items.Add(pacOff);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(enable);
        menu.Items.Add(disable);
        menu.Items.Add(toggle);
        menu.Items.Add(apply);
        menu.Items.Add(openCfg);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exit);
        return menu;
    }

    // ==== 全局代理（传统 WinINet 代理）====
    /// <summary>
    /// 启用“全局代理”模式：
    ///  - 关闭 PAC（删除 AutoConfigURL，避免冲突）
    ///  - ProxyEnable = 1，写入 ProxyServer 与 ProxyOverride（绕行名单）
    ///  - 通知系统设置变更并刷新缓存
    /// </summary>
    private static void EnableProxy()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: true)!;
        // 关闭 PAC（避免与全局代理冲突）
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
    /// 仅更新全局代理的绕行名单（ProxyOverride），不改变开关状态。
    /// </summary>
    private static void ApplyBypassOnly()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: true)!;
        key.SetValue("ProxyOverride", LoadProxyBypass(), RegistryValueKind.String);
        RefreshWinInet();
        UpdateVisual();
    }

    /// <summary>
    /// 关闭全局代理（保留 ProxyServer/ProxyOverride 值，但不生效）。
    /// </summary>
    private static void DisableProxy()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: true)!;
        key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        RefreshWinInet();
        UpdateVisual();
    }

    /// <summary>
    /// 快速切换：如果当前已开启全局代理则关闭，否则开启。
    /// 注意：此处只切全局代理（不涉及 PAC），双击托盘同效。
    /// </summary>
    private static void ToggleProxy()
    {
        if (IsProxyOn()) DisableProxy(); else EnableProxy();
    }

    /// <summary>
    /// 读取注册表判断全局代理是否开启（ProxyEnable != 0）。
    /// </summary>
    private static bool IsProxyOn()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: false)!;
        var val = key.GetValue("ProxyEnable", 0);
        return val is int i && i != 0;
    }

    // ==== PAC 分流（默认直连，仅命中才走代理）====
    /// <summary>
    /// 启用 PAC 分流：
    ///  - 根据 proxy-via.txt 生成 PAC 文件（默认直连，命中名单才返回 PROXY）
    ///  - 关闭全局代理（ProxyEnable=0），写入 AutoConfigURL=file:///... 指向 PAC
    ///  - 通知系统刷新
    /// </summary>
    private static void EnablePacSplit()
    {
        // 生成 PAC 文件（根据经代理名单构造）
        File.WriteAllText(PacPath, BuildPacFromRules(ProxyServer, LoadProxyViaRules()), Encoding.UTF8);

        // 开启 PAC，关闭全局代理
        using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: true)!;
        key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        key.SetValue("AutoConfigURL", new Uri(PacPath).AbsoluteUri, RegistryValueKind.String); // file:///... 绝对 URI
        key.SetValue("AutoDetect", 0, RegistryValueKind.DWord);
        RefreshWinInet();
        UpdateVisual();
    }

    /// <summary>
    /// 仅更新 PAC 文件（不改变当前代理开关），常用于增量维护 proxy-via.txt 后的热更新。
    /// </summary>
    private static void UpdatePacOnly()
    {
        File.WriteAllText(PacPath, BuildPacFromRules(ProxyServer, LoadProxyViaRules()), Encoding.UTF8);
        RefreshWinInet();
        UpdateVisual();
    }

    /// <summary>
    /// 关闭 PAC（删除 AutoConfigURL），不改变全局代理的开关。
    /// </summary>
    private static void DisablePac()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: true)!;
        key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
        RefreshWinInet();
        UpdateVisual();
    }

    /// <summary>
    /// 判断是否处于 PAC 模式（AutoConfigURL 存在且非空）。
    /// </summary>
    private static bool IsPacOn()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: false)!;
        var v = key.GetValue("AutoConfigURL") as string;
        return !string.IsNullOrWhiteSpace(v);
    }

    // ==== 读取配置 ====
    /// <summary>
    /// 读取 proxy-bypass.txt，过滤空行与注释行，去重后拼为分号分隔串，供 ProxyOverride 使用。
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

    /// <summary>
    /// 读取 proxy-via.txt，返回需要"走代理"的域名/IP/CIDR 列表（用于 PAC）。
    /// </summary>
    private static List<string> LoadProxyViaRules()
    {
        var rules = new List<string>();
        try
        {
            if (!File.Exists(ProxyViaPath)) return rules;
            foreach (var raw in File.ReadAllLines(ProxyViaPath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;
                rules.Add(line);
            }
        }
        catch { /* ignore 读取异常，返回已有 */ }
        return rules;
    }

    // ==== 生成 PAC 内容 ====
    /// <summary>
    /// 根据“需要经代理”的规则生成 PAC 脚本。逻辑：
    ///  1) 先直连本地域名与常见保留网段（10/8、172.16/12、192.168/16）。
    ///  2) 对于每条规则：
    ///     - 支持前缀 *.example.com 形式（通配子域）。
    ///     - 支持 .example.com 形式（匹配根域及子域）。
    ///     - 支持单个 IPv4 地址与 IPv4 CIDR（转为 isInNet 形式）。
    ///     - 普通域名默认匹配根域 + 子域（dnsDomainIs 或 *.domain）。
    ///  3) 命中返回 "PROXY host:port; DIRECT"（失败容错回退直连），未命中则 "DIRECT"。
    /// </summary>
    private static string BuildPacFromRules(string proxyServer, List<string> rules)
    {
        var sb = new StringBuilder();
        sb.AppendLine("function FindProxyForURL(url, host) {");
        sb.AppendLine("  function isPlain(h){return isPlainHostName(h) || shExpMatch(h,'localhost') || shExpMatch(h,'*.local');}");
        sb.AppendLine("  if (isPlain(host)) return 'DIRECT';");
        sb.AppendLine("  if (isInNet(host,'10.0.0.0','255.0.0.0') || isInNet(host,'172.16.0.0','255.240.0.0') || isInNet(host,'192.168.0.0','255.255.0.0')) return 'DIRECT';");

        foreach (var r in rules.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var rule = r.Trim();
            if (rule.StartsWith("*."))
            {
                // 形式：*.example.com  —— 通配所有子域
                sb.AppendLine($"  if (shExpMatch(host,'{rule}')) return 'PROXY {proxyServer}; DIRECT';");
            }
            else if (rule.StartsWith("."))
            {
                // 形式：.example.com —— 根域与子域
                var suf = rule.TrimStart('.');
                sb.AppendLine($"  if (dnsDomainIs(host,'{suf}') || shExpMatch(host,'*.{suf}')) return 'PROXY {proxyServer}; DIRECT';");
            }
            else if (System.Net.IPAddress.TryParse(rule, out _))
            {
                // 单个 IPv4 地址
                sb.AppendLine($"  if (isInNet(host,'{rule}','255.255.255.255')) return 'PROXY {proxyServer}; DIRECT';");
            }
            else if (TryParseCidr(rule, out var baseIp, out var mask))
            {
                // IPv4 CIDR：转为 isInNet(host, baseIp, mask)
                sb.AppendLine($"  if (isInNet(host,'{baseIp}','{mask}')) return 'PROXY {proxyServer}; DIRECT';");
            }
            else
            {
                // 普通域名（默认根域 + 子域）
                sb.AppendLine($"  if (dnsDomainIs(host,'{rule}') || shExpMatch(host,'*.{rule}')) return 'PROXY {proxyServer}; DIRECT';");
            }
        }

        // 其余默认直连
        sb.AppendLine("  return 'DIRECT';");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>
    /// 解析 IPv4 CIDR（形如 192.0.2.0/24），输出网络地址(baseIp)与子网掩码(mask)。
    /// 仅处理 IPv4；若需 IPv6，需扩展此方法与 PAC 侧匹配逻辑。
    /// </summary>
    private static bool TryParseCidr(string input, out string baseIp, out string mask)
    {
        baseIp = mask = string.Empty;
        var parts = input.Split('/');
        if (parts.Length != 2) return false;
        if (!System.Net.IPAddress.TryParse(parts[0], out var ip)) return false;
        if (!int.TryParse(parts[1], out var bits) || bits < 0 || bits > 32) return false;

        uint ipU = ToUInt(ip);
        uint m = bits == 0 ? 0u : 0xFFFFFFFFu << (32 - bits);
        uint net = ipU & m; // 取网络地址

        baseIp = FromUInt(net);
        mask   = FromUInt(m);
        return true;

        // 将 IPAddress(IPv4) 转为 uint（注意字节序）
        static uint ToUInt(System.Net.IPAddress addr) => (uint)BitConverter.ToInt32(addr.GetAddressBytes().Reverse().ToArray(), 0);
        // 将 uint 还原为点分十进制字符串
        static string FromUInt(uint v) => $"{(v >> 24) & 255}.{(v >> 16) & 255}.{(v >> 8) & 255}.{v & 255}";
    }

    // ==== 系统刷新与状态展示 ====
    /// <summary>
    /// 通知 WinINet：代理设置已变化，并请求刷新，使设置即时生效。
    /// </summary>
    private static void RefreshWinInet()
    {
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH,          IntPtr.Zero, 0);
    }

    /// <summary>
    /// 根据当前注册表状态更新托盘图标与提示文本：
    ///  - PAC 开启：显示红点 + "PAC Split: ON → host:port"
    ///  - 全局代理开启：显示红点 + "Proxy: ON (host:port)"
    ///  - 均关闭：显示黑点 + "Proxy: OFF"
    /// </summary>
    private static void UpdateVisual()
    {
        if (_ni == null) return;

        using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: false)!;
        bool pacOn = key.GetValue("AutoConfigURL") is string s && !string.IsNullOrWhiteSpace(s);
        bool proxyOn = IsProxyOn();

        if (pacOn)
        {
            _ni.Icon = _iconOn ?? SystemIcons.Application;
            _ni.Text = $"PAC Split: ON → {ProxyServer}";
            return;
        }
        if (proxyOn)
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
    /// 动态绘制一个小圆点图标（红/黑），避免外置 .ico 资源依赖。
    /// 注意：GetHicon 产生的句柄需使用 DestroyIcon 释放（本方法在克隆后即释放原句柄）。
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
        finally { DestroyIcon(hIcon); } // 释放原始句柄，避免 GDI 句柄泄漏
    }
}
