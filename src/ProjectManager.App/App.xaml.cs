using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using ProjectManager.App.Services;
using ProjectManager.App.Views;

namespace ProjectManager.App;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = @"Local\ProjectManager.App.SingleInstance";
    private const int SwRestore = 9;

    private Mutex? _instanceMutex;
    private ShellContext? _context;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 单实例：生成过程中重复启动会让两个进程写同一目录，必须挡在最前面
        _instanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            ActivateExistingInstance();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        _context = Bootstrapper.Create(this);
        var window = new MainWindow(_context);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>UI 线程未处理异常：提示并继续，不让一次点击把整个应用带走。</summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"发生未预期的错误：\n\n{e.Exception.Message}\n\n应用将继续运行；若问题持续，请重启。",
            "Project_Manager",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
    }

    private static void ActivateExistingInstance()
    {
        var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName(current.ProcessName))
        {
            if (process.Id == current.Id)
            {
                continue;
            }

            var handle = process.MainWindowHandle;
            if (handle == IntPtr.Zero)
            {
                continue;
            }

            ShowWindow(handle, SwRestore);
            SetForegroundWindow(handle);
            return;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
