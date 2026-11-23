using System.Windows;

namespace Guardian.App.Views;

/// <summary>输入隔离双重确认对话框：需勾选“已知悉风险”后方可确认。</summary>
public partial class IsolationConfirmDialog : Window
{
    public IsolationConfirmDialog(bool remoteRejected)
    {
        InitializeComponent();
        RemoteWarning.Visibility = remoteRejected ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (AcknowledgeCheck.IsChecked == true)
        {
            DialogResult = true;
        }
    }
}
