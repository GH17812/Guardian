using System.Windows;
using Microsoft.Win32;

namespace Guardian.App.Services;

/// <summary>按主题设置切换 Light.xaml / Dark.xaml；System 时跟随系统外观并监听变化。</summary>
public sealed class ThemeManager
{
    private readonly AppSettingsService _settings;
    private ResourceDictionary? _appliedThemeDictionary;
    private string _appliedTheme = string.Empty;

    /// <summary>主题实际切换后触发（用于刷新以画刷呈现的动态内容）。</summary>
    public event EventHandler? ThemeChanged;

    /// <summary>当前实际解析出的主题（Light 或 Dark，System 已解析）。</summary>
    public string ResolvedTheme { get; private set; } = "Light";

    public ThemeManager(AppSettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>挂载初始主题字典并监听系统外观变化。</summary>
    public void Initialize()
    {
        Apply();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // 仅当设置为“跟随系统”时才响应系统外观变化。
        if (_settings.Settings.Theme != "System")
        {
            return;
        }
        // 系统事件可能在非 UI 线程触发，确保字典切换在 UI 线程执行。
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.Invoke(Apply);
            return;
        }
        Apply();
    }

    /// <summary>根据当前设置重新应用主题（无变化时为空操作）。</summary>
    public void Apply()
    {
        var resolved = ResolveTheme();
        if (resolved == _appliedTheme)
        {
            return;
        }

        var merged = Application.Current.Resources.MergedDictionaries;
        if (_appliedThemeDictionary is not null)
        {
            merged.Remove(_appliedThemeDictionary);
        }

        var dict = LoadDictionary(resolved == "Dark" ? "Themes/Dark.xaml" : "Themes/Light.xaml");
        merged.Insert(0, dict);

        _appliedThemeDictionary = dict;
        _appliedTheme = resolved;
        ResolvedTheme = resolved;
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>将主题设置解析为具体 Light/Dark。</summary>
    public string ResolveTheme()
    {
        return _settings.Settings.Theme switch
        {
            "Light" => "Light",
            "Dark" => "Dark",
            _ => IsSystemDark() ? "Dark" : "Light",
        };
    }

    /// <summary>读取注册表判断系统是否为深色外观。</summary>
    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    private static ResourceDictionary LoadDictionary(string path)
    {
        return new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Guardian.App;component/{path}", UriKind.Absolute),
        };
    }
}
