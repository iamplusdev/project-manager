using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using ProjectManager.App.Services;
using ProjectManager.App.ViewModels;
using ProjectManager.Application.Projects;
using ProjectManager.Storage;

namespace ProjectManager.App.Views;

public partial class MainWindow : Window
{
    private readonly ShellContext _context;
    private readonly MainViewModel _viewModel;
    private NewProjectWindow? _wizard;

    public MainWindow(ShellContext context)
    {
        InitializeComponent();

        _context = context;
        _viewModel = new MainViewModel(context);
        DataContext = _viewModel;

        NewProjectButton.IsEnabled = context.CanScaffold;
        UpdateThemeButton();

        Loaded += OnLoaded;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.ReloadAsync();
            if (_viewModel.Projects.Projects.Count > 0)
            {
                ProjectListBox.SelectedIndex = 0;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this,
                $"读取项目清单失败：\n\n{ex.Message}",
                "Project_Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            _ = OpenWizardAsync();
        }
    }

    private async void OnNewProjectClick(object sender, RoutedEventArgs e) => await OpenWizardAsync();

    private async Task OpenWizardAsync()
    {
        if (!_context.CanScaffold)
        {
            MessageBox.Show(this,
                "模板配置未成功加载，无法新建项目。\n\n请在「设置」中查看模板来源，或修复模板配置文件后重启。",
                "Project_Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        // 同刻只允许一个向导：生成过程中重复 Ctrl+N 只把现有窗口提到前台
        if (_wizard is not null)
        {
            _wizard.Activate();
            return;
        }

        var wizard = new NewProjectWindow(_context) { Owner = this };
        _wizard = wizard;
        try
        {
            var generated = wizard.ShowDialog();
            if (generated == true)
            {
                await _viewModel.ReloadAsync();

                // 生成成功后选中新项目，让"生成即注册"在界面上立即可见
                var newest = _viewModel.Projects.Projects.FirstOrDefault();
                if (newest is not null)
                {
                    _viewModel.Selected = newest;
                    ProjectListBox.SelectedItem = newest;
                }
            }
        }
        finally
        {
            _wizard = null;
            await _context.SaveSettingsAsync();
        }
    }

    private void OnProjectDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.Selected is { } project)
        {
            RevealInExplorer(project.Path);
        }
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Selected is { } project)
        {
            RevealInExplorer(project.Path);
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_context) { Owner = this };
        window.ShowDialog();

        // 设置里也能改主题，回来要对齐顶栏按钮
        UpdateThemeButton();
    }

    /// <summary>「打开已有项目」：只登记，不复制/移动目录内容。</summary>
    private async void OnOpenExistingClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择要加入项目清单的目录",
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        ImportResult result;
        try
        {
            result = await _viewModel.ImportAsync(dialog.FolderName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this,
                $"读取项目清单失败：\n\n{ex.Message}",
                "Project_Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        // 成功与"已在清单中"的可见结果相同：该条出现在列表顶部并被选中，故不再弹框打断
        if (result.Succeeded)
        {
            ProjectListBox.ScrollIntoView(_viewModel.Selected);
            return;
        }

        MessageBox.Show(this,
            result.Message,
            "Project_Manager",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    /// <summary>顶栏主题按钮：跟随系统 → 浅色 → 深色 → 跟随系统。</summary>
    private async void OnThemeToggleClick(object sender, RoutedEventArgs e)
    {
        var next = _context.Theme.Current switch
        {
            ThemePreference.System => ThemePreference.Light,
            ThemePreference.Light => ThemePreference.Dark,
            _ => ThemePreference.System,
        };

        _context.Theme.Apply(next);
        UpdateThemeButton();
        await _context.SaveSettingsAsync();
    }

    private void UpdateThemeButton()
    {
        var (glyph, hint) = _context.Theme.Current switch
        {
            ThemePreference.Light => ("○", "主题：浅色（点击切换）"),
            ThemePreference.Dark => ("●", "主题：深色（点击切换）"),
            _ => ("◐", "主题：跟随系统（点击切换）"),
        };

        ThemeButton.Content = glyph;
        ThemeButton.ToolTip = hint;
    }

    private void RevealInExplorer(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                MessageBox.Show(this,
                    $"目录不存在：\n{path}\n\n可能已被移动或删除。",
                    "Project_Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"\"{path}\"", UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this,
                $"无法打开目录：\n{ex.Message}",
                "Project_Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
