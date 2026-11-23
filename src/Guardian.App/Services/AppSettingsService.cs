using System.IO;
using System.Text.Json;

namespace Guardian.App.Services;

/// <summary>本地应用设置的线程安全读写，文件位于 %LOCALAPPDATA%\Guardian\app.settings.json。</summary>
public sealed class AppSettingsService
{
    private readonly string _filePath;
    private readonly object _lock = new();
    private AppSettings _settings;

    public AppSettingsService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Guardian");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "app.settings.json");
        _settings = Load();
    }

    /// <summary>当前设置（可变对象；修改后需调用 <see cref="Save"/> 持久化）。</summary>
    public AppSettings Settings
    {
        get
        {
            lock (_lock)
            {
                return _settings;
            }
        }
    }

    /// <summary>原子地修改设置并立即持久化。</summary>
    public void Update(Action<AppSettings> mutate)
    {
        lock (_lock)
        {
            mutate(_settings);
            PersistCore();
        }
    }

    /// <summary>立即持久化当前设置。</summary>
    public void Save()
    {
        lock (_lock)
        {
            PersistCore();
        }
    }

    private void PersistCore()
    {
        try
        {
            var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
        catch
        {
            // 持久化失败不阻塞应用运行。
        }
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch
        {
            // 解析失败回退默认值。
        }
        return new AppSettings();
    }
}
