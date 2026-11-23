using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Guardian.App.ViewModels;

namespace Guardian.App.Services;

/// <summary>WinForms 托盘图标与菜单（图标由代码绘制盾牌，无需图片资源）。</summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly MainViewModel _viewModel;
    private readonly Action _showMain;
    private readonly Action _exit;
    private bool _disposed;
    private bool _balloonShown;

    public TrayIconService(MainViewModel viewModel, Action showMain, Action exit)
    {
        _viewModel = viewModel;
        _showMain = showMain;
        _exit = exit;

        _notifyIcon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = LocalizationManager.Instance?.L("Str.AppName") ?? "Guardian",
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => _showMain();
        _notifyIcon.ContextMenuStrip = BuildMenu();
    }

    /// <summary>首次最小化时提示一次气泡。</summary>
    public void ShowBalloonOnce()
    {
        if (_balloonShown)
        {
            return;
        }
        _balloonShown = true;
        _notifyIcon.BalloonTipTitle = LocalizationManager.Instance?.L("Str.AppName") ?? "Guardian";
        _notifyIcon.BalloonTipText = LocalizationManager.Instance?.L("Str.TrayBalloon") ?? "已最小化到托盘";
        _notifyIcon.ShowBalloonTip(2500);
    }

    private ContextMenuStrip BuildMenu()
    {
        var l = LocalizationManager.Instance!;
        var menu = new ContextMenuStrip();
        menu.Items.Add(l.L("Str.TrayShow"), null, (_, _) => _showMain());
        menu.Items.Add(l.L("Str.TrayEnablePrivacy"), null, (_, _) => _ = _viewModel.EnablePrivacyFromTrayAsync());
        menu.Items.Add(l.L("Str.TrayDisable"), null, (_, _) => _ = _viewModel.DisableProtectionAsync());
        menu.Items.Add(l.L("Str.TrayRecover"), null, (_, _) => _ = _viewModel.EmergencyRecoverAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(l.L("Str.TrayExit"), null, (_, _) => _exit());
        return menu;
    }

    /// <summary>代码绘制 32x32 蓝底白盾图标。</summary>
    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var blue = new SolidBrush(Color.FromArgb(10, 132, 255));
            g.FillEllipse(blue, 1, 1, 30, 30);

            using var white = new SolidBrush(Color.White);
            // 简易盾牌轮廓。
            var points = new PointF[]
            {
                new(16, 5),
                new(24, 8),
                new(24, 15),
                new(16, 27),
                new(8, 15),
                new(8, 8),
            };
            g.FillPolygon(white, points);
        }

        // 进程生命周期内保留一个 HICON，泄漏可接受。
        return Icon.FromHandle(bitmap.GetHicon());
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
