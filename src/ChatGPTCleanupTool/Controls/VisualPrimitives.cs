using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace ChatGPTCleanupTool.Controls;

// Keep existing page indices stable; visually place the overview first, including circular keyboard order.
public sealed class NavigationPanel : Panel
{
    private IEnumerable<UIElement> Ordered => InternalChildren.Cast<UIElement>().OrderBy(GetZIndex);
    protected override Size MeasureOverride(Size available)
    {
        double height = 0, width = 0;
        foreach (var child in Ordered) { child.Measure(new(available.Width, double.PositiveInfinity)); height += child.DesiredSize.Height; width = Math.Max(width, child.DesiredSize.Width); }
        return new(width, height);
    }
    protected override Size ArrangeOverride(Size final)
    { double y = 0; foreach (var child in Ordered) { child.Arrange(new Rect(0, y, final.Width, child.DesiredSize.Height)); y += child.DesiredSize.Height; } return final; }
}

public sealed class StatusBrushConverter : IValueConverter
{
    public bool Soft { get; set; }
    public object Convert(object value, Type type, object parameter, CultureInfo culture)
    {
        var text = value?.ToString() ?? "";
        var kind = text.Contains("失败") || text.Contains("禁止") || text.Contains("高风险") || text == "高" ? 2 :
            text.Contains("未确认") || text.Contains("未知") || text.Contains("待") || text.Contains("保护") || text.Contains("不可") || text == "中" ? 1 :
            text.Contains("通过") || text.Contains("完成") || text.Contains("可勾选") || text.Contains("已回收") || text == "低" ? 0 : 3;
        return BrushesByKind[Soft ? 1 : 0][kind];
    }
    private static readonly Brush[][] BrushesByKind = [Create(["#087B6D", "#946013", "#B63E50", "#4547BB"]), Create(["#E4F6F1", "#FFF5DF", "#FFF0F1", "#EEEDFF"])];
    private static Brush[] Create(string[] colors) => colors.Select(c => { var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(c)!; brush.Freeze(); return (Brush)brush; }).ToArray();
    public object ConvertBack(object value, Type type, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class DiskGauge : FrameworkElement
{
    public static readonly DependencyProperty UsedPercentProperty = DependencyProperty.Register(nameof(UsedPercent), typeof(double), typeof(DiskGauge), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty GaugeBrushProperty = DependencyProperty.Register(nameof(GaugeBrush), typeof(Brush), typeof(DiskGauge), new FrameworkPropertyMetadata(Brushes.Teal, FrameworkPropertyMetadataOptions.AffectsRender));
    public double UsedPercent { get => (double)GetValue(UsedPercentProperty); set => SetValue(UsedPercentProperty, value); }
    public Brush GaugeBrush { get => (Brush)GetValue(GaugeBrushProperty); set => SetValue(GaugeBrushProperty, value); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); double percent = double.IsFinite(UsedPercent) ? Math.Clamp(UsedPercent, 0, 100) : 0;
        var center = new Point(ActualWidth / 2, ActualHeight / 2); double radius = Math.Max(0, Math.Min(ActualWidth, ActualHeight) / 2 - 7);
        var track = new SolidColorBrush(Color.FromRgb(234, 239, 247)); dc.DrawEllipse(null, new Pen(track, 9), center, radius, radius);
        var pen = new Pen(GaugeBrush, 9) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (percent >= 100) dc.DrawEllipse(null, pen, center, radius, radius);
        else if (percent > 0)
        {
            double angle = percent * Math.PI / 50 - Math.PI / 2; var shape = new StreamGeometry();
            using (var ctx = shape.Open()) { ctx.BeginFigure(new(center.X, center.Y - radius), false, false); ctx.ArcTo(new(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle)), new(radius, radius), 0, percent > 50, SweepDirection.Clockwise, true, false); }
            dc.DrawGeometry(null, pen, shape);
        }
        var label = new FormattedText($"{percent:0}%", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 22, new SolidColorBrush(Color.FromRgb(36, 51, 79)), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(label, new(center.X - label.Width / 2, center.Y - label.Height / 2));
    }
}
