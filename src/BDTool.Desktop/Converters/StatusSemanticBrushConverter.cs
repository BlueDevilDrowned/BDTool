using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using BDTool.Presentation.States;

namespace BDTool.Desktop.Converters;

/// <summary>把表现层状态语义映射到当前主题资源，View 不保存颜色常量。</summary>
public sealed class StatusSemanticBrushConverter : IValueConverter
{
    public object Convert(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture)
    {
        var key = value switch
        {
            StatusSemantic.Success => "brush.success",
            StatusSemantic.Running => "brush.info",
            StatusSemantic.Warning => "brush.warning",
            StatusSemantic.Failed => "brush.danger",
            StatusSemantic.Uncertain => "brush.uncertain",
            StatusSemantic.Unsupported => "brush.text.tertiary",
            StatusSemantic.Cancelled => "brush.text.secondary",
            _ => "brush.border.strong"
        };
        return System.Windows.Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        Binding.DoNothing;
}
