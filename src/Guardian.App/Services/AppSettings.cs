namespace Guardian.App.Services;

/// <summary>客户端本地应用设置（语言、主题）。保护引擎相关设置由服务端负责，不在此列。</summary>
public sealed class AppSettings
{
    /// <summary>界面语言：zh-CN 或 en-US。</summary>
    public string Language { get; set; } = "zh-CN";

    /// <summary>主题：Light / Dark / System。</summary>
    public string Theme { get; set; } = "Light";
}
