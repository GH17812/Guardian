using System.Windows;
using Guardian.App.Services;
using Guardian.Contracts;
using Guardian.Contracts.Models;

namespace Guardian.App.Views;

/// <summary>新建会话对话框：收集会话参数并生成创建载荷。</summary>
public partial class CreateSessionDialog : Window
{
    /// <summary>确定后生成的创建载荷。</summary>
    public CreateSessionPayload? Payload { get; private set; }

    public CreateSessionDialog()
    {
        InitializeComponent();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            var l = LocalizationManager.Instance;
            MessageBox.Show(
                l?.L("Str.NameRequiredError") ?? "会话名称不能为空",
                l?.L("Str.ErrorTitle") ?? "错误",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        int? maxDuration = null;
        if (int.TryParse(MaxDurationBox.Text?.Trim(), out var minutes) && minutes > 0)
        {
            maxDuration = minutes;
        }

        Payload = new CreateSessionPayload
        {
            Name = name,
            WorkloadType = (WorkloadType)(WorkloadCombo.SelectedIndex >= 0 ? WorkloadCombo.SelectedIndex : 0),
            RequiredMode = ModeCombo.SelectedIndex == 1 ? ProtectionMode.InputIsolation : ProtectionMode.Privacy,
            Source = (SessionSource)(SourceCombo.SelectedIndex >= 0 ? SourceCombo.SelectedIndex : 0),
            AllowAutoEnd = AllowAutoEndCheck.IsChecked == true,
            MaxDurationMinutes = maxDuration,
            SourceDetail = "Guardian.App",
        };

        DialogResult = true;
    }
}
