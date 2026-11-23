using System.Windows;
using Guardian.App.ViewModels;

namespace Guardian.App.Views;

/// <summary>主界面窗口。</summary>
public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>显示并激活窗口（供托盘与单实例唤醒调用）。</summary>
    public void ShowAndActivate()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
    }
}
