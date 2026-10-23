using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using ProjectManager.App.Services;
using ProjectManager.App.ViewModels;
using ProjectManager.Application.Projects;
using ProjectManager.Application.Repositories;
using ProjectManager.Storage;

namespace ProjectManager.App.Views;

public partial class MainWindow : Window
{
    private readonly ShellContext _context;
    private readonly MainViewModel _viewModel;
    private NewProjectWindow? _wizard;
    private DispatcherTimer? _snackTimer;

    public MainWindow(ShellContext context)
    {
        InitializeComponent();

        _context = context;
        _viewModel = new MainViewModel(context);
        DataContext = _viewModel;

        NewProjectButton.IsEnabled = context.CanScaffold;

        // 侧栏折叠态是用户偏好，跨会话保持
        _viewModel.IsSidebarCollapsed = context.Settings.SidebarCollapsed;
        ApplySidebarWidth();
        UpdateThemeButton();

        Loaded += OnLoaded;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // 启动时不自动选中：主区默认展示「最近项目」卡片，用户点谁才看谁的详情
            await _viewModel.ReloadAsync();
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
        if (Keyboard.Modifiers != ModifierKeys.Control)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.N:
                e.Handled = true;
                _ = OpenWizardAsync();
                break;

            case Key.F:
                e.Handled = true;
                SearchBox.Focus();
                SearchBox.SelectAll();
                break;
        }
    }

    // ── 侧栏 ────────────────────────────────────────────────────────────

    private void OnShowProjectsClick(object sender, RoutedEventArgs e) => _viewModel.ShowProjects();

    private void OnShowComponentsClick(object sender, RoutedEventArgs e) => _viewModel.ShowComponents();

    private void OnShowRepositoriesClick(object sender, RoutedEventArgs e) => _viewModel.ShowRepositories();

    private void OnShowChangesClick(object sender, RoutedEventArgs e) => _viewModel.ShowChanges();

    private void OnShowHistoryClick(object sender, RoutedEventArgs e) => _viewModel.ShowHistory();

    /// <summary>历史页「刷新」：别处可能刚 push 了一批提交。</summary>
    private async void OnRefreshHistoryClick(object sender, RoutedEventArgs e)
        => await _viewModel.RefreshHistoryAsync();

    /// <summary>历史页「加载更多」：追加更早的提交，不打断当前阅读位置。</summary>
    private async void OnLoadMoreHistoryClick(object sender, RoutedEventArgs e)
    {
        var before = HistoryList.Items.Count;
        await _viewModel.LoadMoreHistoryAsync();

        // 滚到**第一条新加载的**提交：滚到末尾会一下跳过整整一页，用户还得往回翻
        if (HistoryList.Items.Count > before)
        {
            HistoryList.ScrollIntoView(HistoryList.Items[before]);
        }
    }

    /// <summary>变更页「刷新」：工作区可能刚在别的工具里被改过。</summary>
    private async void OnRefreshChangesClick(object sender, RoutedEventArgs e)
        => await _viewModel.RefreshChangesAsync();

    /// <summary>暂存 / 取消暂存：都是显式动作，成功后 SnackBar 说明动了几个文件。</summary>
    private async void OnStageSelectedClick(object sender, RoutedEventArgs e)
        => await RunWorkspaceActionAsync(() => _viewModel.SetStagedAsync(staged: true));

    private async void OnUnstageSelectedClick(object sender, RoutedEventArgs e)
        => await RunWorkspaceActionAsync(() => _viewModel.SetStagedAsync(staged: false));

    /// <summary>提交。失败时必须把 git / hook 的原文透给用户——那是唯一能告诉他怎么改的信息。</summary>
    private async void OnCommitClick(object sender, RoutedEventArgs e)
        => await RunWorkspaceActionAsync(_viewModel.CommitAsync, successPrefix: null);

    private async Task RunWorkspaceActionAsync(Func<Task<WorkspaceFeedback>> action, string? successPrefix = null)
    {
        try
        {
            var feedback = await action();
            ShowSnack(feedback.Succeeded && successPrefix is not null
                ? $"{successPrefix}{feedback.Message}"
                : feedback.Message);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 兜底：异步事件处理器里漏出去的异常会直接崩进程
            ShowSnack($"操作失败：{ex.Message}");
        }
    }

    /// <summary>仓库页「刷新」：清单是当下事实，用户手动重查一次。</summary>
    private async void OnRefreshRepositoriesClick(object sender, RoutedEventArgs e)
        => await _viewModel.RefreshRepositoriesAsync();

    /// <summary>仓库页每行的「查看」：回到项目页并选中它（不是新开窗口）。</summary>
    private void OnOpenRepositoryProjectClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RepositoryEntry entry })
        {
            return;
        }

        _viewModel.ShowProjects();

        var project = _viewModel.Projects.Projects.FirstOrDefault(item => item.Id == entry.ProjectId);
        if (project is null)
        {
            ShowSnack("该项目已不在项目清单中，请刷新。");
            return;
        }

        _viewModel.Selected = project;
    }

    /// <summary>「补新增目录」：只加不删，执行完 SnackBar 说明补了什么。</summary>
    private void OnApplyUpgradeClick(object sender, RoutedEventArgs e)
    {
        var outcome = _viewModel.ApplyUpgrade();
        if (outcome is null)
        {
            ShowSnack("当前没有可补的目录。");
            return;
        }

        ShowSnack(outcome.Message);
    }

    private async void OnToggleSidebarClick(object sender, RoutedEventArgs e)
    {
        _viewModel.IsSidebarCollapsed = !_viewModel.IsSidebarCollapsed;
        ApplySidebarWidth();

        _context.Settings.SidebarCollapsed = _viewModel.IsSidebarCollapsed;
        await _context.SaveSettingsAsync();
    }

    private void ApplySidebarWidth()
    {
        var key = _viewModel.IsSidebarCollapsed ? "PmSidebarCollapsedWidth" : "PmSidebarColumnWidth";
        SidebarColumn.Width = (GridLength)FindResource(key);
    }

    // ── 项目切换 ────────────────────────────────────────────────────────

    private void OnSwitcherClick(object sender, RoutedEventArgs e) => SwitcherPopup.IsOpen = !SwitcherPopup.IsOpen;

    private void OnSwitcherSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SwitcherList.SelectedItem is not null)
        {
            SwitcherPopup.IsOpen = false;
        }
    }

    private void OnCardClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: StoredProject project })
        {
            _viewModel.Selected = project;
        }
    }

    // ── 新建 / 打开 ─────────────────────────────────────────────────────

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
                var newest = _viewModel.Projects.VisibleProjects.FirstOrDefault();
                if (newest is not null)
                {
                    _viewModel.Selected = newest;
                    ProjectListBox.SelectedItem = newest;
                }

                ShowSnack(newest is null ? "项目已生成。" : $"「{newest.Name}」已生成并加入项目清单。");
            }
        }
        finally
        {
            _wizard = null;
            await _context.SaveSettingsAsync();
        }
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

        // 成功与"已在清单中"的可见结果相同（该条出现在列表顶部并被选中），用浮层提示而不打断
        if (result.Succeeded)
        {
            ProjectListBox.ScrollIntoView(_viewModel.Selected);
            ShowSnack(result.Message);
            return;
        }

        MessageBox.Show(this,
            result.Message,
            "Project_Manager",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    // ── 主题 ────────────────────────────────────────────────────────────

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

        ShowSnack(next switch
        {
            ThemePreference.Light => "主题：浅色",
            ThemePreference.Dark => "主题：深色",
            _ => "主题：跟随系统",
        });
    }

    private void UpdateThemeButton()
    {
        var (glyph, hint) = _context.Theme.Current switch
        {
            ThemePreference.Light => ("\u25CB", "主题：浅色（点击切换）"),
            ThemePreference.Dark => ("\u25CF", "主题：深色（点击切换）"),
            _ => ("\u25D0", "主题：跟随系统（点击切换）"),
        };

        ThemeButton.Content = glyph;
        ThemeButton.ToolTip = hint;
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_context) { Owner = this };
        window.ShowDialog();

        // 设置里也能改主题，回来要对齐顶栏按钮
        UpdateThemeButton();

        // 模板可能被导入/停用/重载过：新建按钮的可用性与告警横幅要跟着变
        _viewModel.RefreshTemplateState();
    }

    // ── 其它 ────────────────────────────────────────────────────────────

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

    // ── SnackBar ────────────────────────────────────────────────────────

    /// <summary>非阻塞提示：淡入后停留 2.6s 再淡出。连续调用会重置计时，不排队。</summary>
    private void ShowSnack(string message)
    {
        SnackText.Text = message;
        SnackHost.Visibility = Visibility.Visible;
        SnackHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0d, 1d, TimeSpan.FromMilliseconds(120)));

        _snackTimer?.Stop();
        _snackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.6) };
        _snackTimer.Tick += (_, _) =>
        {
            _snackTimer?.Stop();

            var fade = new DoubleAnimation(1d, 0d, TimeSpan.FromMilliseconds(160));
            fade.Completed += (_, _) => SnackHost.Visibility = Visibility.Collapsed;
            SnackHost.BeginAnimation(OpacityProperty, fade);
        };
        _snackTimer.Start();
    }
}
