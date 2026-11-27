using System.Windows;
using System.Windows.Controls;
using BDTool.Presentation.States;

namespace BDTool.Desktop.Controls;

/// <summary>统一状态徽标，颜色和文字都来自 Presentation 语义。</summary>
public partial class StatusBadge : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(StatusBadge),
        new FrameworkPropertyMetadata(string.Empty));

    public static readonly DependencyProperty SemanticProperty = DependencyProperty.Register(
        nameof(Semantic),
        typeof(StatusSemantic),
        typeof(StatusBadge),
        new FrameworkPropertyMetadata(StatusSemantic.Neutral));

    public StatusBadge()
    {
        InitializeComponent();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public StatusSemantic Semantic
    {
        get => (StatusSemantic)GetValue(SemanticProperty);
        set => SetValue(SemanticProperty, value);
    }
}
