using System.Windows;

namespace Guardian.App.Services;

/// <summary>界面本地化管理：切换 Strings.zh-CN.xaml / Strings.en-US.xaml 合并字典，并提供键值查询。</summary>
public sealed class LocalizationManager
{
    private readonly AppSettingsService _settings;
    private ResourceDictionary? _currentDictionary;
    private string _currentLanguage = string.Empty;

    /// <summary>全局实例（供代码后置文案查询使用）。</summary>
    public static LocalizationManager? Instance { get; private set; }

    /// <summary>语言切换后触发（用于刷新以字符串呈现的动态内容）。</summary>
    public event EventHandler? LanguageChanged;

    public string CurrentLanguage => _currentLanguage;

    public LocalizationManager(AppSettingsService settings)
    {
        _settings = settings;
        Instance = this;
    }

    /// <summary>挂载初始语言字典。</summary>
    public void Initialize() => Apply();

    /// <summary>按当前设置应用语言（无变化时为空操作）。</summary>
    public void Apply()
    {
        var lang = string.IsNullOrEmpty(_settings.Settings.Language) ? "zh-CN" : _settings.Settings.Language;
        if (lang == _currentLanguage && _currentDictionary is not null)
        {
            return;
        }

        var merged = Application.Current.Resources.MergedDictionaries;
        if (_currentDictionary is not null)
        {
            merged.Remove(_currentDictionary);
        }

        var path = lang == "en-US"
            ? "Localization/Strings.en-US.xaml"
            : "Localization/Strings.zh-CN.xaml";

        var dict = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Guardian.App;component/{path}", UriKind.Absolute),
        };
        merged.Add(dict);

        _currentDictionary = dict;
        _currentLanguage = lang;
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>按键获取本地化文案；缺失时回退为键名本身。</summary>
    public string L(string key)
        => Application.Current.TryFindResource(key) as string ?? key;
}
