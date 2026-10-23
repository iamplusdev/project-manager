using System.IO;
using System.Diagnostics;
using System.Windows;
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
        TemplateSourceText.Text = context.TemplateSourceText;
        TemplateWarningText.Text = context.TemplateWarning ?? string.Empty;

        _loading = false;
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
        MessageBox.Show(this,
            "模板在应用启动时加载。修改配置后请重启 Project_Manager 使新模板生效。",
            "重新加载模板",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        _context.Settings.OpenFolderAfterGeneration = OpenFolderCheck.IsChecked == true;
        DialogResult = true;
        Close();
    }
}
