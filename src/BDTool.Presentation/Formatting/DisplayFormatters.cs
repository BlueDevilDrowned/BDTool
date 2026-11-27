using System.Globalization;

namespace BDTool.Presentation.Formatting;

/// <summary>跨页面统一的时间和字节格式，避免各 View 自行拼接。</summary>
public static class DisplayFormatters
{
    public static string LocalTime(DateTimeOffset value) =>
        value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);

    public static string Duration(long? milliseconds)
    {
        if (milliseconds is null)
        {
            return "-";
        }

        if (milliseconds < 1000)
        {
            return milliseconds.Value.ToString(CultureInfo.CurrentCulture) + " ms";
        }

        return (milliseconds.Value / 1000d)
            .ToString("0.0", CultureInfo.CurrentCulture) + " s";
    }

    public static string Bytes(long bytes)
    {
        string[] units = { "B", "KiB", "MiB", "GiB" };
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString(unit == 0 ? "0" : "0.0", CultureInfo.CurrentCulture)
            + " " + units[unit];
    }
}
