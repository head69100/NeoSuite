using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using NeoBoost.Services;

namespace NeoBoost.Views;

/// <summary>Graphique processeur/mémoire des 30 dernières minutes, dessiné sur un Canvas.</summary>
public sealed partial class HistoryPage : Page
{
    private readonly DispatcherQueueTimer _timer;

    public HistoryPage()
    {
        InitializeComponent();
        ChartCanvas.SizeChanged += (_, _) => Draw();
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(5);
        _timer.Tick += (_, _) => RefreshStats();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        UsageHistory.Start();
        RefreshStats();
        _timer.Start();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => _timer.Stop();

    private void RefreshStats()
    {
        var points = UsageHistory.Snapshot();
        EmptyText.Visibility = points.Count < 2 ? Visibility.Visible : Visibility.Collapsed;

        var (peakCpu, _) = UsageHistory.Peak(points);
        var peakRam = points.Count == 0 ? null : points.OrderByDescending(p => p.RamPercent).First();
        PeakCpuText.Text = points.Count == 0 ? "—" : $"{peakCpu.CpuPercent:0} % · {peakCpu.Time:HH:mm}";
        PeakRamText.Text = peakRam is null ? "—" : $"{peakRam.RamPercent:0} % · {peakRam.Time:HH:mm}";
        CurrentText.Text = points.Count == 0 ? "—" : $"{points[^1].CpuPercent:0} % · {points[^1].RamPercent:0} %";

        Draw();
    }

    private void Draw()
    {
        ChartCanvas.Children.Clear();
        var points = UsageHistory.Snapshot();
        if (points.Count < 2 || ChartCanvas.ActualWidth < 50 || ChartCanvas.ActualHeight < 50)
            return;

        var width = ChartCanvas.ActualWidth;
        var height = ChartCanvas.ActualHeight;

        // Grille horizontale (25, 50, 75 %)
        foreach (var fraction in new[] { 0.25, 0.5, 0.75 })
        {
            var line = new Line
            {
                X1 = 0, X2 = width,
                Y1 = height * fraction, Y2 = height * fraction,
                Stroke = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                StrokeThickness = 1,
            };
            ChartCanvas.Children.Add(line);
        }

        AddSeries(points, width, height, p => p.CpuPercent, (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]);
        AddSeries(points, width, height, p => p.RamPercent, (Brush)Application.Current.Resources["SystemFillColorCautionBrush"]);
    }

    private void AddSeries(List<HistoryPoint> points, double width, double height, Func<HistoryPoint, double> value, Brush brush)
    {
        var polyline = new Polyline
        {
            Stroke = brush,
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
        };
        var from = points[0].Time;
        var span = Math.Max(1, (points[^1].Time - from).TotalSeconds);
        foreach (var point in points)
        {
            var x = (point.Time - from).TotalSeconds / span * width;
            var y = height - Math.Clamp(value(point), 0, 100) / 100 * height;
            polyline.Points.Add(new Windows.Foundation.Point(x, y));
        }
        ChartCanvas.Children.Add(polyline);
    }
}
