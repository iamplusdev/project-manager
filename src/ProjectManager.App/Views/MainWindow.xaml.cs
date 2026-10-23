using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using ProjectManager.App.Services;
using ProjectManager.App.ViewModels;

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
