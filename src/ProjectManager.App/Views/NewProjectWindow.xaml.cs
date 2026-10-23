using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ProjectManager.Application.Wizard;
using ProjectManager.App.Services;

namespace ProjectManager.App.Views;

public partial class NewProjectWindow : Window
{
    private readonly ShellContext _context;
    private readonly NewProjectWizard _wizard;
    private CancellationTokenSource? _generationCts;
    private bool _forceClose;
    private bool _closeAfterGeneration;

    public NewProjectWindow(ShellContext context)
    {
        InitializeComponent();

        _context = context;
        _wizard = new NewProjectWizard(context.Templates.Values, context.Generator, context.Coordinator, context.Settings);
        DataContext = _wizard;

        _wizard.PropertyChanged += OnWizardPropertyChanged;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += OnLoaded;

        UpdateStepBar();
    }

    public NewProjectWizard Wizard => _wizard;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 进入即可输入：Step 1 的唯一必填项是名称
        MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private void OnWizardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NewProjectWizard.Step))
        {
            UpdateStepBar();
        }
    }

    /// <summary>
    /// 步骤条视觉。放在 code-behind 而非 XAML 数据触发器里：
    /// 三个圆点各有「未开始 / 进行中 / 已完成」三态，用 6 段 DataTrigger 表达反而更难读。
    /// </summary>
    private void UpdateStepBar()
    {
        var current = _wizard.StepIndex;
        ApplyStep(Dot1, Num1, Label1, index: 0, current);
        ApplyStep(Dot2, Num2, Label2, index: 1, current);
        ApplyStep(Dot3, Num3, Label3, index: 2, current);
    }

    private void ApplyStep(Ellipse dot, System.Windows.Controls.TextBlock number, System.Windows.Controls.TextBlock label, int index, int current)
    {
        var accent = (Brush)FindResource("PmAccentBrush");
        var accentText = (Brush)FindResource("PmAccentTextBrush");
        var surfaceAlt = (Brush)FindResource("PmSurfaceAltBrush");
        var border = (Brush)FindResource("PmBorderStrongBrush");
        var primary = (Brush)FindResource("PmTextPrimaryBrush");
        var secondary = (Brush)FindResource("PmTextSecondaryBrush");

        if (index < current)
        {
            dot.Fill = accent;
            dot.Stroke = accent;
            number.Text = "✓";
            number.Foreground = accentText;
            label.Foreground = secondary;
            label.FontWeight = FontWeights.Normal;
        }
        else if (index == current)
        {
            dot.Fill = accent;
            dot.Stroke = accent;
            number.Text = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            number.Foreground = accentText;
            label.Foreground = primary;
            label.FontWeight = FontWeights.SemiBold;
        }
        else
        {
            dot.Fill = surfaceAlt;
            dot.Stroke = border;
            number.Text = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            number.Foreground = secondary;
            label.Foreground = secondary;
            label.FontWeight = FontWeights.Normal;
        }
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择生成位置",
            Multiselect = false,
        };

        if (Directory.Exists(_wizard.TargetDirectory))
        {
            dialog.InitialDirectory = _wizard.TargetDirectory;
        }

        if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            _wizard.TargetDirectory = dialog.FolderName;
        }
    }

    private void OnNext(object sender, RoutedEventArgs e) => _wizard.Next();

    private void OnBack(object sender, RoutedEventArgs e) => _wizard.Back();

    private void OnSelectAllModules(object sender, RoutedEventArgs e) => _wizard.SelectAllModules();

    private void OnInvertModules(object sender, RoutedEventArgs e) => _wizard.InvertModules();

    private void OnClearModules(object sender, RoutedEventArgs e) => _wizard.ClearAllModules();

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_wizard.IsBusy)
        {
            _generationCts?.Cancel();
            return;
        }

        _forceClose = true;
        DialogResult = false;
        Close();
    }

    private async void OnGenerate(object sender, RoutedEventArgs e)
    {
        if (!_wizard.CanGenerate || _generationCts is not null)
        {
            return;
        }

        _generationCts = new CancellationTokenSource();
        try
        {
            await _wizard.GenerateAsync(cancellationToken: _generationCts.Token);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this,
                $"无法开始生成：\n\n{ex.Message}",
                "Project_Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _generationCts.Dispose();
            _generationCts = null;

            if (_closeAfterGeneration)
            {
                _forceClose = true;
                DialogResult = false;
                Close();
            }
        }
    }

    private async void OnRetryRegistration(object sender, RoutedEventArgs e)
    {
        var ok = await _wizard.RetryRegistrationAsync();
        if (!ok)
        {
            MessageBox.Show(this,
                "重试注册仍未成功。文件已生成，可稍后在项目列表中重新添加，或检查设置目录是否可写。",
                "Project_Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OnOpenResultFolder(object sender, RoutedEventArgs e)
    {
        if (_wizard.Outcome?.ProjectPath is { } path)
        {
            OpenFolder(path);
        }
    }

    private void OnCopyPath(object sender, RoutedEventArgs e)
    {
        if (_wizard.Outcome?.ProjectPath is not { } path)
        {
            return;
        }

        try
        {
            Clipboard.SetText(path);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // 剪贴板被其他进程占用：不弹窗打断，用户可再次点击
        }
    }

    private void OnFinish(object sender, RoutedEventArgs e)
    {
        _forceClose = true;
        DialogResult = _wizard.Outcome?.Kind == GenerationOutcomeKind.Success;
        Close();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _wizard.IsBusy)
        {
            e.Handled = true;
            _generationCts?.Cancel();
        }
    }

    /// <summary>
    /// 关闭拦截（方案 6.4.0）：有填写内容先确认放弃；生成中则转为"取消生成"并等回滚完成再关。
    /// </summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_forceClose)
        {
            return;
        }

        if (_wizard.IsBusy)
        {
            e.Cancel = true;
            var answer = MessageBox.Show(this,
                "正在生成，确定取消？\n\n已创建的空目录与占位文件将被回滚，你原有的文件不会被删除。",
                "取消生成",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer == MessageBoxResult.Yes)
            {
                _closeAfterGeneration = true;
                _generationCts?.Cancel();
            }

            return;
        }

        if (_wizard.IsResultStep || !_wizard.HasAnyInput)
        {
            return;
        }

        var discard = MessageBox.Show(this,
            "放弃本次填写？",
            "新建项目",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (discard != MessageBoxResult.Yes)
        {
            e.Cancel = true;
        }
    }

    private void OpenFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                MessageBox.Show(this,
                    $"目录不存在：\n{path}",
                    "Project_Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("explorer.exe")
                {
                    Arguments = $"\"{path}\"",
                    UseShellExecute = true,
                });
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
