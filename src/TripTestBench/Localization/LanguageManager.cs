// ============================================================================
// 文件：LanguageManager.cs
// 作用：运行时中英文切换。
//       实现方式：把 App.Resources 的合并字典整体替换为“主题 + 对应语言包”，
//       界面中所有 DynamicResource 会自动刷新，无需重启软件。
//       同时抛 LanguageChanged 事件，供 ViewModel 刷新运行时拼接的文字。
// ============================================================================

using System.Windows; // Application / ResourceDictionary

namespace TripTestBench.Localization;

/// <summary>语言管理器：切换语言包并广播事件。</summary>
public static class LanguageManager
{
    /// <summary>中文语言代码。</summary>
    public const string Chinese = "zh-CN";

    /// <summary>英文语言代码。</summary>
    public const string English = "en-US";

    private static string _current = Chinese; // 当前语言

    /// <summary>语言切换完成事件（界面与 ViewModel 据此刷新文字）。</summary>
    public static event EventHandler? LanguageChanged;

    /// <summary>当前语言代码。</summary>
    public static string Current => _current;

    /// <summary>是否英文界面。</summary>
    public static bool IsEnglish => _current == English;

    /// <summary>读取当前语言资源字符串；找不到时返回键名，便于快速定位漏翻的键。</summary>
    public static string Get(string key)
    {
        var app = Application.Current;
        return app?.TryFindResource(key) as string ?? key;
    }

    /// <summary>按当前语言格式化字符串，例如 LanguageManager.Format("L.Msg.Completed", value)。</summary>
    public static string Format(string key, params object?[] args)
    {
        try
        {
            return string.Format(Get(key), args);
        }
        catch (FormatException)
        {
            // 资源里的占位符与参数个数不匹配时，退回原始文本，避免程序崩溃
            return Get(key);
        }
    }

    /// <summary>
    /// 应用指定语言：重建合并字典（主题 + 语言包），并触发 LanguageChanged。
    /// </summary>
    public static void Apply(string? language)
    {
        var target = string.Equals(language, English, StringComparison.OrdinalIgnoreCase)
            ? English
            : Chinese;

        var app = Application.Current;

        // 启动早期 Application 可能尚未创建，只记录语言即可
        if (app is null)
        {
            _current = target;
            return;
        }

        var dictionaries = app.Resources.MergedDictionaries;
        dictionaries.Clear();

        // 1) 主题样式（颜色、控件样式）
        dictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("Assets/Styles/Theme.xaml", UriKind.Relative)
        });

        // 2) 语言包（中英文资源键完全一致，切换后 DynamicResource 立即生效）
        dictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"Resources/Strings.{target}.xaml", UriKind.Relative)
        });

        _current = target;
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>在中英文之间切换（标题栏按钮调用）。</summary>
    public static void Toggle()
    {
        Apply(_current == Chinese ? English : Chinese);
    }
}
