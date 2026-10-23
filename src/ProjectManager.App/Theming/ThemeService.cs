using System.IO;
using System.Windows;
using Microsoft.Win32;
using ProjectManager.Storage;

namespace ProjectManager.App.Theming;

/// <summary>
/// 主题服务：把「跟随系统 / 浅色 / 深色」解析成具体的资源字典并替换。
/// </summary>
/// <remarks>
/// 视图不直接引用任何第三方主题库；替换点只有 <see cref="Application.Resources"/> 的索引 0。
/// 将来接 WPF-UI 时，只需把这里的字典换掉，视图 XAML 不变。
/// </remarks>
public sealed class ThemeService
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";

    private readonly System.Windows.Application _application;

    public ThemeService(System.Windows.Application application) => _application = application;

    public ThemePreference Current { get; private set; } = ThemePreference.System;

    public bool IsDark { get; private set; }

    public event EventHandler? Changed;

    public void Apply(ThemePreference preference)
    {
        Current = preference;
        var dark = preference switch
        {
            ThemePreference.Dark => true,
            ThemePreference.Light => false,
            _ => IsSystemDark(),
        };

        // 用绝对 pack URI 而不是相对路径：相对 URI 会按「入口程序集」解析，
        // 一旦宿主程序集变化（拆分控件库、被其他进程加载）就会找不到资源。
        var assemblyName = typeof(ThemeService).Assembly.GetName().Name;
        var source = new Uri(
            $"pack://application:,,,/{assemblyName};component/Themes/{(dark ? "Dark" : "Light")}.xaml",
            UriKind.Absolute);
        var dictionary = new ResourceDictionary { Source = source };

        if (_application.Resources.MergedDictionaries.Count == 0)
        {
            _application.Resources.MergedDictionaries.Add(dictionary);
        }
        else
        {
            _application.Resources.MergedDictionaries[0] = dictionary;
        }

        IsDark = dark;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>系统深色模式检测。读不到（受策略限制/远程桌面）时按浅色处理，不抛异常。</summary>
    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue(AppsUseLightThemeValue) is int value && value == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
