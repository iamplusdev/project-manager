using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ProjectManager.Application.Mvvm;

/// <summary>
/// 最小 MVVM 基类。刻意不引第三方 MVVM 库：整个 UI 层保持零 NuGet 依赖，
/// 任何机器 <c>dotnet build</c> 即可构建。
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>同名属性在外层维护（如计算属性），用于显式通知。</summary>
    protected void Raise(params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            OnPropertyChanged(name);
        }
    }
}
