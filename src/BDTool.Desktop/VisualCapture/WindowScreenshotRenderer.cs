using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BDTool.Desktop.VisualCapture;

/// <summary>
/// 以指定 DIP 尺寸离屏渲染真实 WPF Shell。用于 DPI 受限环境下的可重复视觉回归，
/// 复用正常 Application 资源、DataTemplate 和绑定，不生成模拟页面。
/// </summary>
internal static class WindowScreenshotRenderer
{
    public static void Capture(
        Window window,
        string outputPath,
        int width,
        int height,
        double dpi = 96)
    {
        if (window.Content is not FrameworkElement content)
        {
            throw new InvalidOperationException("WPF 窗口没有可渲染的 FrameworkElement 内容。");
        }

        content.Width = width;
        content.Height = height;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();

        var pixelWidth = (int)Math.Ceiling(width * dpi / 96d);
        var pixelHeight = (int)Math.Ceiling(height * dpi / 96d);
        var bitmap = new RenderTargetBitmap(
            pixelWidth,
            pixelHeight,
            dpi,
            dpi,
            PixelFormats.Pbgra32);
        bitmap.Render(content);

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(outputPath);
        encoder.Save(stream);
    }
}
