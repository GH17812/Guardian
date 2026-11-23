using System.Collections.ObjectModel;
using System.Windows;
using Guardian.App.Services;
using Guardian.App.ViewModels;

namespace Guardian.App.Views;

/// <summary>日志窗口：加载最近操作日志。</summary>
public partial class LogsWindow : Window
{
    private readonly GuardianClientService _client;

    public ObservableCollection<LogEntryViewModel> Logs { get; } = new();

    public LogsWindow(GuardianClientService client)
    {
        InitializeComponent();
        _client = client;
        DataContext = this;
    }

    /// <summary>加载最近 500 条日志。</summary>
    public async Task LoadAsync()
    {
        try
        {
            var list = await _client.GetLogsAsync(500);
            Logs.Clear();
            foreach (var entry in list)
            {
                Logs.Add(new LogEntryViewModel(entry));
            }
        }
        catch (GuardianClientException ex)
        {
            MessageBox.Show(
                ex.Message,
                LocalizationManager.Instance?.L("Str.ErrorTitle") ?? "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
