using System.IO;
using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using ProjectManager.Application.Templates;
using ProjectManager.App.Services;
using ProjectManager.Storage;

namespace ProjectManager.App.Views;

public partial class SettingsWindow : Window
{
    private readonly ShellContext _context;
    private bool _loading = true;

    public SettingsWindow(ShellContext context)
    {
        InitializeComponent();

        _context = context;

        ThemeSystemRadio.IsChecked = context.Theme.Current == ThemePreference.System;
        ThemeLightRadio.IsChecked = context.Theme.Current == ThemePreference.Light;
        ThemeDarkRadio.IsChecked = context.Theme.Current == ThemePreference.Dark;

        OpenFolderCheck.IsChecked = context.Settings.OpenFolderAfterGeneration;
        RefreshTemplateSection();

        _loading = false;
    }

    /// <summary>把当前模板状态刷到界面（来源、清单、告警、按钮可用性）。</summary>
    private void RefreshTemplateSection()
    {
        TemplateSourceText.Text = _context.TemplateSourceText;
        TemplateWarningText.Text = _context.TemplateWarning ?? string.Empty;
        TemplateWarningText.Visibility = string.IsNullOrEmpty(_context.TemplateWarning)
            ? Visibility.Collapsed
            : Visibility.Visible;

        TemplateList.ItemsSource = _context.Templates.Values
            .OrderBy(template => template.Name, StringComparer.Ordinal)
            .Select(TemplateEntry.FromTemplate)
            .ToArray();

        DisableUserConfigButton.IsEnabled = _context.Library.HasUserConfig;
    }

    private void OnThemeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var preference = ThemeDarkRadio.IsChecked == true
            ? ThemePreference.Dark
            : ThemeLightRadio.IsChecked == true
                ? ThemePreference.Light
                : ThemePreference.System;

        _context.Settings.Theme = preference;
        _context.Theme.Apply(preference);
    }

    private void OnOpenTemplateFolder(object sender, RoutedEventArgs e)
    {
        var directory = System.IO.Path.GetDirectoryName(_context.Paths.TemplatesFile);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        try
        {
            System.IO.Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"\"{directory}\"", UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this,
                $"无法打开配置文件夹：\n{ex.Message}",
                "Project_Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OnReloadTemplates(object sender, RoutedEventArgs e)
    {
        var result = _context.ReloadTemplates();
        RefreshTemplateSection();

        if (result.Succeeded)
        {
            ShowBanner(
                "Success",
                $"已重新加载 {result.TemplateCount} 个模板",
                Array.Empty<TemplateIssue>(),
                "新建项目将使用新的模板列表，无需重启。");
            return;
        }

        ShowBanner(
            "Error",
            "重新加载失败",
            Array.Empty<TemplateIssue>(),
            $"{result.Failure}\n可点击「停用用户配置」回退到安装目录或内置模板。");
    }

    /// <summary>
    /// 导入一份模板配置：先校验，通过才落盘（旧文件备份为 .bak）。
    /// 校验不通过时一个字节都不写，用户手上的旧配置保持可用——这是"可回退"的前提。
    /// </summary>
    private void OnImportTemplates(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择模板配置文件",
            Filter = "模板配置 (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        ImportFrom(dialog.FileName);
    }

    /// <summary>导入指定文件并反馈结果（按钮与自检脚本共用同一条路径，避免两套文案）。</summary>
    private void ImportFrom(string path)
    {
        var outcome = _context.Library.Import(path);

        if (!outcome.Succeeded)
        {
            ShowBanner("Error", "导入失败，现有配置未改动", outcome.Inspection.Issues,
                "按上面的位置改好文件后可以再导入一次。");
            return;
        }

        var reload = _context.ReloadTemplates();
        RefreshTemplateSection();

        var hint = reload.Succeeded
            ? (outcome.BackupPath is null
                ? $"已替换用户模板（共 {outcome.Inspection.Entries.Count} 个），新建项目立即可用。"
                : $"已替换用户模板（共 {outcome.Inspection.Entries.Count} 个）。旧配置保留为 {System.IO.Path.GetFileName(outcome.BackupPath)}，可随时找回。")
            : $"文件已写入，但重新加载失败：{reload.Failure}";

        ShowBanner(
            outcome.Inspection.Warnings.Count > 0 ? "Warning" : "Success",
            $"已导入 {outcome.Inspection.Entries.Count} 个模板，检查通过",
            outcome.Inspection.Warnings,
            hint);
    }

    /// <summary>停用用户配置：改名而不是删除，用户反悔时还能找回。</summary>
    private void OnDisableUserConfig(object sender, RoutedEventArgs e)
    {
        try
        {
            var backup = _context.Library.DisableUserConfig();
            if (backup is null)
            {
                ShowBanner("Info", "当前没有用户配置", Array.Empty<TemplateIssue>(), "已经在使用安装目录或内置模板。");
                return;
            }

            var reload = _context.ReloadTemplates();
            RefreshTemplateSection();

            ShowBanner(
                reload.Succeeded ? "Success" : "Error",
                "已停用用户配置",
                Array.Empty<TemplateIssue>(),
                reload.Succeeded
                    ? $"已回退到 {_context.TemplateSourceText}。原文件保留为 {System.IO.Path.GetFileName(backup)}。"
                    : $"回退后仍无可用模板：{reload.Failure}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowBanner("Error", "停用失败", Array.Empty<TemplateIssue>(), ex.Message);
        }
    }

    private void ShowBanner(string severity, string title, IReadOnlyList<TemplateIssue> issues, string? hint)
    {
        ImportResultBar.Tag = severity;
        ImportResultTitle.Text = title;

        IssueList.ItemsSource = issues
            .Select(issue => new { Text = issue.Describe() })
            .ToArray();

        ImportResultHint.Text = hint ?? string.Empty;
        ImportResultHint.Visibility = string.IsNullOrWhiteSpace(hint) ? Visibility.Collapsed : Visibility.Visible;
        ImportResultBar.Visibility = Visibility.Visible;
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        _context.Settings.OpenFolderAfterGeneration = OpenFolderCheck.IsChecked == true;
        DialogResult = true;
        Close();
    }
}
