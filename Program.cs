using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayAppContext()); // 关键：不显示窗体，跑托盘上下文
    }
}

internal sealed class TrayAppContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private Icon _iconBlack;
    private Icon _iconRed;
    private bool _isRed = false;

    public TrayAppContext()
    {
        _iconBlack = CreateCircleIcon(Color.Black);
        _iconRed   = CreateCircleIcon(Color.Red);

        _tray = new NotifyIcon
        {
            Icon = _iconBlack,
            Visible = true,
            Text = "jailbreak — double-click to toggle"
        };

        // 双击切换
        _tray.DoubleClick += (_, __) => Toggle();

        // 右键菜单
        var menu = new ContextMenuStrip();
        var toggleItem = new ToolStripMenuItem("Toggle (Double-Click)");
        toggleItem.Click += (_, __) => Toggle();
        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, __) => Exit();
        menu.Items.Add(toggleItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);
        _tray.ContextMenuStrip = menu;

        Application.ApplicationExit += (_, __) => Cleanup();
    }

    private void Toggle()
    {
        _isRed = !_isRed;
        _tray.Icon = _isRed ? _iconRed : _iconBlack;
        _tray.Text = _isRed ? "jailbreak: RED" : "jailbreak: BLACK";
    }

    private void Exit()
    {
        Cleanup();
        Application.Exit();
    }

    private void Cleanup()
    {
        _tray.Visible = false;
        _tray.Dispose();
        _iconBlack?.Dispose();
        _iconRed?.Dispose();
    }

    // 生成 32x32 圆点图标
    private static Icon CreateCircleIcon(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 2, 2, 28, 28);
        }
        IntPtr hIcon = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(hIcon).Clone(); }
        finally { DestroyIcon(hIcon); }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
