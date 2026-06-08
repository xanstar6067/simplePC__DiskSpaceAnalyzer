using System.Globalization;
using System.Windows.Data;
using DiskSpaceAnalyzer.Models;
using DiskSpaceAnalyzer.Services;

namespace DiskSpaceAnalyzer.Converters;

public sealed class ScanNodeSizeConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not ScanNode node)
        {
            return "";
        }

        var useSizeOnDisk = values[1] is true;
        var size = useSizeOnDisk ? node.SizeOnDisk : node.LogicalSize;
        return FileSizeFormatter.Format(size);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
