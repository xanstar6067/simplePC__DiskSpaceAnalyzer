using System.Globalization;
using System.Windows.Data;
using DiskSpaceAnalyzer.Models;

namespace DiskSpaceAnalyzer.Converters;

public sealed class ScanNodePercentConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not ScanNode node || node.Parent is null)
        {
            return "";
        }

        var useSizeOnDisk = values[1] is true;
        var size = useSizeOnDisk ? node.SizeOnDisk : node.LogicalSize;
        var parentSize = useSizeOnDisk ? node.Parent.SizeOnDisk : node.Parent.LogicalSize;
        return parentSize > 0 ? $"{size / (double)parentSize:P1}" : "";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
