using System.Windows;

namespace BDTool.Desktop;

/// <summary>Shell 窗口只承载数据绑定和快捷键，不包含模块业务逻辑。</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
