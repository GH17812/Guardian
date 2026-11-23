using System.Windows;
using Guardian.App.Services;
using Guardian.Contracts;
using Guardian.Contracts.Models;

namespace Guardian.App.ViewModels;

/// <summary>设置窗口视图模型：应用设置（语言/主题）与服务端保护设置。</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly GuardianClientService _client;
    private readonly AppSettingsService _appSettings;
    private readonly LocalizationManager _localization;
    private readonly ThemeManager _theme;

    private GuardianSettings? _loaded;

    private int _languageIndex;
    private int _themeIndex;
    private string _emergencyHotkeyKey = "F12";
    private int _uiLossActionIndex;
    private int _resumePolicyIndex;
    private int _clientLostThresholdSeconds = 15;
    private bool _onlyOnAcPower;
    private int _maxManualProtectionMinutes;
    private bool _allowIsolationDuringRemoteSession;
    private int _logLevelIndex = 2;
    private string _streamingEnvOfflineDirectory = string.Empty;

    public SettingsViewModel(
        GuardianClientService client,
        AppSettingsService appSettings,
        LocalizationManager localization,
        ThemeManager theme)
    {
        _client = client;
        _appSettings = appSettings;
        _localization = localization;
        _theme = theme;

        _languageIndex = appSettings.Settings.Language == "en-US" ? 1 : 0;
        _themeIndex = appSettings.Settings.Theme switch { "Dark" => 1, "System" => 2, _ => 0 };

        SaveCommand = new RelayCommand(_ => { _ = SaveAsync(); });
        CancelCommand = new RelayCommand(_ => CloseRequested?.Invoke());
    }

    public event Action? CloseRequested;

    public RelayCommand SaveCommand { get; }
    public RelayCommand CancelCommand { get; }

    // ---------- 应用设置 ----------

    public int LanguageIndex
    {
        get => _languageIndex;
        set
        {
            if (SetProperty(ref _languageIndex, value))
            {
                ApplyAppSettings();
            }
        }
    }

    public int ThemeIndex
    {
        get => _themeIndex;
        set
        {
            if (SetProperty(ref _themeIndex, value))
            {
                ApplyAppSettings();
            }
        }
    }

    // ---------- 服务端设置 ----------

    public string EmergencyHotkeyKey
    {
        get => _emergencyHotkeyKey;
        set => SetProperty(ref _emergencyHotkeyKey, value);
    }

    public int UiLossActionIndex
    {
        get => _uiLossActionIndex;
        set => SetProperty(ref _uiLossActionIndex, value);
    }

    public int ResumePolicyIndex
    {
        get => _resumePolicyIndex;
        set => SetProperty(ref _resumePolicyIndex, value);
    }

    public int ClientLostThresholdSeconds
    {
        get => _clientLostThresholdSeconds;
        set => SetProperty(ref _clientLostThresholdSeconds, value);
    }

    public bool OnlyOnAcPower
    {
        get => _onlyOnAcPower;
        set => SetProperty(ref _onlyOnAcPower, value);
    }

    public int MaxManualProtectionMinutes
    {
        get => _maxManualProtectionMinutes;
        set => SetProperty(ref _maxManualProtectionMinutes, value);
    }

    public bool AllowIsolationDuringRemoteSession
    {
        get => _allowIsolationDuringRemoteSession;
        set => SetProperty(ref _allowIsolationDuringRemoteSession, value);
    }

    public int LogLevelIndex
    {
        get => _logLevelIndex;
        set => SetProperty(ref _logLevelIndex, value);
    }

    /// <summary>一键启用的离线安装包目录（空 = 直接联网下载）。</summary>
    public string StreamingEnvOfflineDirectory
    {
        get => _streamingEnvOfflineDirectory;
        set => SetProperty(ref _streamingEnvOfflineDirectory, value);
    }

    /// <summary>从服务端加载保护设置。</summary>
    public async Task LoadAsync()
    {
        try
        {
            var s = await _client.GetSettingsAsync();
            if (s is null)
            {
                return;
            }
            _loaded = s;
            EmergencyHotkeyKey = s.EmergencyHotkeyKey;
            UiLossActionIndex = s.UiLossAction == UiLossAction.KeepProtection ? 1 : 0;
            ResumePolicyIndex = s.ResumePolicy == ResumePolicy.ResumeLast ? 1 : 0;
            ClientLostThresholdSeconds = s.ClientLostThresholdSeconds;
            OnlyOnAcPower = s.OnlyOnAcPower;
            MaxManualProtectionMinutes = s.MaxManualProtectionMinutes;
            AllowIsolationDuringRemoteSession = s.AllowIsolationDuringRemoteSession;
            LogLevelIndex = (int)s.LogLevel;
            StreamingEnvOfflineDirectory = s.StreamingEnvOfflineDirectory;
        }
        catch (GuardianClientException ex)
        {
            MessageBox.Show(ex.Message, _localization.L("Str.ErrorTitle"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApplyAppSettings()
    {
        _appSettings.Update(s =>
        {
            s.Language = _languageIndex == 1 ? "en-US" : "zh-CN";
            s.Theme = _themeIndex switch { 1 => "Dark", 2 => "System", _ => "Light" };
        });
        _localization.Apply();
        _theme.Apply();
    }

    private async Task SaveAsync()
    {
        _loaded ??= new GuardianSettings();
        _loaded.EmergencyHotkeyKey = EmergencyHotkeyKey;
        _loaded.UiLossAction = UiLossActionIndex == 1 ? UiLossAction.KeepProtection : UiLossAction.Recover;
        _loaded.ResumePolicy = ResumePolicyIndex == 1 ? ResumePolicy.ResumeLast : ResumePolicy.Restore;
        _loaded.ClientLostThresholdSeconds = ClientLostThresholdSeconds;
        _loaded.OnlyOnAcPower = OnlyOnAcPower;
        _loaded.MaxManualProtectionMinutes = MaxManualProtectionMinutes;
        _loaded.AllowIsolationDuringRemoteSession = AllowIsolationDuringRemoteSession;
        _loaded.LogLevel = (GuardianLogLevel)LogLevelIndex;
        _loaded.StreamingEnvOfflineDirectory = StreamingEnvOfflineDirectory?.Trim() ?? string.Empty;

        try
        {
            await _client.UpdateSettingsAsync(_loaded);
            CloseRequested?.Invoke();
        }
        catch (GuardianClientException ex)
        {
            MessageBox.Show(ex.Message, _localization.L("Str.ErrorTitle"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
