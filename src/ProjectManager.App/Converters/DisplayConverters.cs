using System.Globalization;
using System.IO;
using System.Windows.Data;

namespace ProjectManager.App.Converters;

/// <summary>取路径末级目录名，用于卡片与下拉里的副标题（不暴露完整路径）。</summary>
public sealed class PathTailConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmed.Length == 0)
        {
            return path;
        }

        var index = trimmed.LastIndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
        return index >= 0 && index + 1 < trimmed.Length ? trimmed[(index + 1)..] : trimmed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// 相对时间：刚刚 / n 分钟前 / n 小时前 / n 天前 / 具体日期。
/// 只做展示，比较基准取本机当前时间；未来时间（时钟回拨）按"刚刚"处理，不显示负数。
/// </summary>
public sealed class RelativeTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        DateTimeOffset moment = value switch
        {
            DateTimeOffset offset => offset,
            DateTime dateTime => new DateTimeOffset(dateTime),
            _ => DateTimeOffset.MinValue,
        };

        if (moment == DateTimeOffset.MinValue)
        {
            return string.Empty;
        }

        var delta = DateTimeOffset.Now - moment;
        if (delta < TimeSpan.FromMinutes(1))
        {
            return "刚刚";
        }

        if (delta < TimeSpan.FromHours(1))
        {
            return $"{(int)delta.TotalMinutes} 分钟前";
        }

        if (delta < TimeSpan.FromDays(1))
        {
            return $"{(int)delta.TotalHours} 小时前";
        }

        if (delta < TimeSpan.FromDays(7))
        {
            return $"{(int)delta.TotalDays} 天前";
        }

        return moment.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
