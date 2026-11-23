using System.Windows;

namespace Guardian.App.Views;

/// <summary>显示隐私检查确认对话框。</summary>
public partial class DisplayPrivacyCheckDialog : Window
{
    public DisplayPrivacyCheckDialog()
    {
        InitializeComponent();
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
