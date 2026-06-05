using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DiskSpaceAnalyzer.Models;
using DiskSpaceAnalyzer.Services;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;
using WpfToolTip = System.Windows.Controls.ToolTip;

namespace DiskSpaceAnalyzer.Controls;

public sealed class DonutChart : FrameworkElement
{
    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(
            nameof(ItemsSource),
            typeof(IEnumerable),
            typeof(DonutChart),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnItemsSourceChanged));

    public static readonly DependencyProperty SliceCommandProperty =
        DependencyProperty.Register(
            nameof(SliceCommand),
            typeof(ICommand),
            typeof(DonutChart),
            new PropertyMetadata(null));

    private static readonly WpfColor[] Palette =
    [
        WpfColor.FromRgb(45, 128, 116),
        WpfColor.FromRgb(68, 105, 175),
        WpfColor.FromRgb(211, 129, 53),
        WpfColor.FromRgb(181, 75, 91),
        WpfColor.FromRgb(105, 121, 62),
        WpfColor.FromRgb(139, 92, 146),
        WpfColor.FromRgb(50, 142, 183),
        WpfColor.FromRgb(160, 105, 64),
        WpfColor.FromRgb(96, 96, 96),
        WpfColor.FromRgb(188, 154, 52)
    ];

    private readonly List<SliceHitArea> _hitAreas = [];
    private readonly WpfToolTip _sliceToolTip;
    private INotifyCollectionChanged? _collectionChanged;
    private ScanNode? _hoveredNode;

    public DonutChart()
    {
        _sliceToolTip = new WpfToolTip
        {
            Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse,
            PlacementTarget = this
        };
    }

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public ICommand? SliceCommand
    {
        get => (ICommand?)GetValue(SliceCommandProperty);
        set => SetValue(SliceCommandProperty, value);
    }

    protected override WpfSize MeasureOverride(WpfSize availableSize)
    {
        var side = Math.Min(
            double.IsInfinity(availableSize.Width) ? 260 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 260 : availableSize.Height);

        return new WpfSize(Math.Max(180, side), Math.Max(180, side));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        _hitAreas.Clear();

        var center = new WpfPoint(RenderSize.Width / 2, RenderSize.Height / 2);
        var radius = Math.Max(20, Math.Min(RenderSize.Width, RenderSize.Height) / 2 - 10);
        var innerRadius = radius * 0.58;
        var nodes = GetNodes().ToList();
        var total = nodes.Sum(node => Math.Max(0, node.SizeOnDisk));

        if (total <= 0)
        {
            drawingContext.DrawEllipse(new WpfSolidColorBrush(WpfColor.FromRgb(234, 237, 241)), null, center, radius, radius);
            drawingContext.DrawEllipse(WpfBrushes.White, null, center, innerRadius, innerRadius);
            DrawCenteredText(drawingContext, "Нет данных", center, 14, WpfBrushes.Gray);
            return;
        }

        if (nodes.Count == 1)
        {
            var brush = new WpfSolidColorBrush(Palette[0]);
            drawingContext.DrawEllipse(brush, null, center, radius, radius);
            drawingContext.DrawEllipse(WpfBrushes.White, null, center, innerRadius, innerRadius);
            _hitAreas.Add(new SliceHitArea(nodes[0], 0, 360, innerRadius, radius, center));
            DrawCenteredText(drawingContext, nodes[0].SizeOnDiskText, center, 15, WpfBrushes.Black);
            return;
        }

        var angle = -90d;
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            var sweep = Math.Max(0.35, node.SizeOnDisk / (double)total * 360d);
            var geometry = CreateDonutSlice(center, radius, innerRadius, angle, angle + sweep);
            drawingContext.DrawGeometry(new WpfSolidColorBrush(Palette[i % Palette.Length]), null, geometry);
            _hitAreas.Add(new SliceHitArea(node, NormalizeAngle(angle), NormalizeAngle(angle + sweep), innerRadius, radius, center));
            angle += sweep;
        }

        drawingContext.DrawEllipse(WpfBrushes.White, null, center, innerRadius, innerRadius);
        DrawCenteredText(drawingContext, FileSizeFormatter.Format(total), center, 15, WpfBrushes.Black);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        var area = FindHitArea(e.GetPosition(this));
        if (area is not null && SliceCommand?.CanExecute(area.Node) == true)
        {
            SliceCommand.Execute(area.Node);
        }
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var area = FindHitArea(e.GetPosition(this));
        var node = area?.Node;
        if (ReferenceEquals(node, _hoveredNode))
        {
            return;
        }

        _hoveredNode = node;
        Cursor = node is null ? null : System.Windows.Input.Cursors.Hand;
        if (node is null)
        {
            HideSliceToolTip();
            return;
        }

        ShowSliceToolTip(node);
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        ClearHover();
    }

    private IEnumerable<ScanNode> GetNodes()
    {
        return ItemsSource?.OfType<ScanNode>()
            .Where(node => node.SizeOnDisk > 0)
            .OrderByDescending(node => node.SizeOnDisk)
            .Take(10) ?? [];
    }

    private SliceHitArea? FindHitArea(WpfPoint position)
    {
        return _hitAreas.FirstOrDefault(area => area.Contains(position));
    }

    private void ShowSliceToolTip(ScanNode node)
    {
        _sliceToolTip.Content = $"{node.DisplayName}{Environment.NewLine}{node.SizeOnDiskText}";
        _sliceToolTip.IsOpen = true;
    }

    private void HideSliceToolTip()
    {
        _sliceToolTip.IsOpen = false;
    }

    private void ClearHover()
    {
        _hoveredNode = null;
        Cursor = null;
        HideSliceToolTip();
    }

    private static Geometry CreateDonutSlice(WpfPoint center, double radius, double innerRadius, double startAngle, double endAngle)
    {
        var outerStart = PointOnCircle(center, radius, startAngle);
        var outerEnd = PointOnCircle(center, radius, endAngle);
        var innerEnd = PointOnCircle(center, innerRadius, endAngle);
        var innerStart = PointOnCircle(center, innerRadius, startAngle);
        var isLargeArc = endAngle - startAngle > 180;

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(outerStart, true, true);
            context.ArcTo(outerEnd, new WpfSize(radius, radius), 0, isLargeArc, SweepDirection.Clockwise, true, false);
            context.LineTo(innerEnd, true, false);
            context.ArcTo(innerStart, new WpfSize(innerRadius, innerRadius), 0, isLargeArc, SweepDirection.Counterclockwise, true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    private static WpfPoint PointOnCircle(WpfPoint center, double radius, double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180;
        return new WpfPoint(center.X + Math.Cos(radians) * radius, center.Y + Math.Sin(radians) * radius);
    }

    private static double NormalizeAngle(double angle)
    {
        angle %= 360;
        return angle < 0 ? angle + 360 : angle;
    }

    private static void DrawCenteredText(DrawingContext drawingContext, string text, WpfPoint center, double size, WpfBrush brush)
    {
        var formatted = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            System.Windows.FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            size,
            brush,
            VisualTreeHelper.GetDpi(System.Windows.Application.Current.MainWindow).PixelsPerDip);

        drawingContext.DrawText(formatted, new WpfPoint(center.X - formatted.Width / 2, center.Y - formatted.Height / 2));
    }

    private static void OnItemsSourceChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not DonutChart chart)
        {
            return;
        }

        if (chart._collectionChanged is not null)
        {
            chart._collectionChanged.CollectionChanged -= chart.OnCollectionChanged;
        }

        chart._collectionChanged = e.NewValue as INotifyCollectionChanged;
        if (chart._collectionChanged is not null)
        {
            chart._collectionChanged.CollectionChanged += chart.OnCollectionChanged;
        }

        chart.ClearHover();
        chart.InvalidateVisual();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ClearHover();
        InvalidateVisual();
    }

    private sealed record SliceHitArea(ScanNode Node, double StartAngle, double EndAngle, double InnerRadius, double OuterRadius, WpfPoint Center)
    {
        public bool Contains(WpfPoint point)
        {
            var dx = point.X - Center.X;
            var dy = point.Y - Center.Y;
            var distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance < InnerRadius || distance > OuterRadius)
            {
                return false;
            }

            var angle = NormalizeAngle(Math.Atan2(dy, dx) * 180 / Math.PI);
            if (StartAngle <= EndAngle)
            {
                return angle >= StartAngle && angle <= EndAngle;
            }

            return angle >= StartAngle || angle <= EndAngle;
        }
    }
}
