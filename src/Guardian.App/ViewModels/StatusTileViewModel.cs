using System.Windows.Media;

namespace Guardian.App.ViewModels;

/// <summary>状态磁贴：图标字形 + 标题 + 状态值（值颜色随状态变化）。</summary>
public sealed class StatusTileViewModel : ObservableObject
{
    /// <summary>Segoe MDL2 Assets 图标字形。</summary>
    public string Glyph { get; set; } = string.Empty;

    /// <summary>标题的本地化键（用于语言切换时刷新）。</summary>
    public string TitleKey { get; set; } = string.Empty;

    private string _title = string.Empty;
    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    private string _value = string.Empty;
    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    private Brush _valueBrush = Brushes.Gray;
    public Brush ValueBrush
    {
        get => _valueBrush;
        set => SetProperty(ref _valueBrush, value);
    }
}
