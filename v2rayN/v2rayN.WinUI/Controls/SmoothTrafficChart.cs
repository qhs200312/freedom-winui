using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI.ViewManagement;
using ShapePath = Microsoft.UI.Xaml.Shapes.Path;

namespace v2rayN.WinUI.Controls;

public sealed class SmoothTrafficChart : UserControl
{
    private readonly Grid _viewport = new();
    private readonly Canvas _drawing = new()
    {
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top
    };
    private readonly ShapePath _upload = CreateLine("UploadChartBrush");
    private readonly ShapePath _download = CreateLine("DownloadChartBrush");
    private readonly UISettings _uiSettings = new();
    private long[] _uploads = [];
    private long[] _downloads = [];
    private double _maximum = 1;
    private double _previousMaximum = 1;
    private long _sampleStarted;
    private bool _animationEnabled = true;
    private Visual? _visual;

    public int VisibleSampleCount { get; set; } = 60;

    public SmoothTrafficChart()
    {
        IsHitTestVisible = false;
        _drawing.Children.Add(_upload);
        _drawing.Children.Add(_download);
        _viewport.Children.Add(_drawing);
        Content = _viewport;
        Loaded += (_, _) => Render();
        Unloaded += (_, _) => StopAnimations();
        SizeChanged += (_, _) => Render();
    }

    public void UpdateSeries(long[] uploads, long[] downloads, double maximum, TimeSpan sampleAge)
    {
        _uploads = uploads;
        _downloads = downloads;
        _previousMaximum = _maximum;
        _maximum = Math.Max(1, maximum);
        _sampleStarted = Stopwatch.GetTimestamp() - (long)(sampleAge.TotalSeconds * Stopwatch.Frequency);
        Render();
    }

    public void SetAnimationEnabled(bool enabled)
    {
        if (_animationEnabled == enabled) return;
        _animationEnabled = enabled;
        Render();
    }

    private static ShapePath CreateLine(string resource) => new()
    {
        Stroke = (Brush)Application.Current.Resources[
            Application.Current.Resources.ContainsKey(resource) ? resource
                : resource == "UploadChartBrush" ? "SuccessBrush" : "WarningBrush"],
        StrokeThickness = 2,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round
    };

    private void Render()
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (!IsLoaded || width <= 1 || height <= 4) return;

        _viewport.Clip = new RectangleGeometry { Rect = new Rect(0, 0, width, height) };
        var intervalCount = Math.Max(2, VisibleSampleCount);
        var step = width / intervalCount;
        _drawing.Width = width + step * 2;
        _drawing.Height = height;
        _upload.Data = BuildCurve(_uploads, intervalCount, step, height);
        _download.Data = BuildCurve(_downloads, intervalCount, step, height);

        ElementCompositionPreview.SetIsTranslationEnabled(_drawing, true);
        _visual ??= ElementCompositionPreview.GetElementVisual(_drawing);
        StopAnimations();
        _visual.CenterPoint = new Vector3(0, (float)(height - 2), 0);
        _visual.Scale = Vector3.One;

        var elapsed = _sampleStarted == 0 ? 1 : Stopwatch.GetElapsedTime(_sampleStarted).TotalSeconds;
        var phase = Math.Clamp(elapsed, 0, 1);
        var hasTraffic = _uploads.Any(value => value > 0) || _downloads.Any(value => value > 0);
        var animate = _animationEnabled && _uiSettings.AnimationsEnabled && hasTraffic && phase < 1;
        var offset = new Vector3((float)(-step * (animate ? phase : 1)), 0, 0);
        _visual.Properties.InsertVector3("Translation", offset);
        if (!animate) return;

        // Only sample changes rebuild geometry; the compositor moves it between samples.
        var movement = _visual.Compositor.CreateVector3KeyFrameAnimation();
        movement.InsertKeyFrame(0, offset);
        movement.InsertKeyFrame(1, new Vector3((float)-step, 0, 0),
            _visual.Compositor.CreateLinearEasingFunction());
        movement.Duration = TimeSpan.FromSeconds(1 - phase);
        _visual.StartAnimation("Translation", movement);

        if (_previousMaximum != _maximum && phase < 0.05 && _previousMaximum > 1)
        {
            var scale = _visual.Compositor.CreateVector3KeyFrameAnimation();
            scale.InsertKeyFrame(0, new Vector3(1, (float)(_maximum / _previousMaximum), 1));
            scale.InsertKeyFrame(1, Vector3.One);
            scale.Duration = TimeSpan.FromMilliseconds(240);
            _visual.StartAnimation("Scale", scale);
        }
    }

    private PathGeometry BuildCurve(long[] values, int intervalCount, double step, double height)
    {
        var geometry = new PathGeometry();
        if (values.Length < 2) return geometry;
        var count = Math.Min(values.Length, intervalCount + 3);
        var start = values.Length - count;
        var firstIndex = intervalCount + 1 - (count - 1);
        Point GetPoint(int index) => new((firstIndex + index) * step,
            height - 2 - (height - 4) * Math.Clamp(Math.Max(0, values[start + index]) / _maximum, 0, 1));

        var figure = new PathFigure { StartPoint = GetPoint(0), IsClosed = false, IsFilled = false };
        for (var index = 1; index < count; index++)
        {
            var current = GetPoint(index);
            var next = GetPoint(Math.Min(index + 1, count - 1));
            figure.Segments.Add(new QuadraticBezierSegment
            {
                Point1 = current,
                Point2 = new Point((current.X + next.X) / 2, (current.Y + next.Y) / 2)
            });
        }
        geometry.Figures.Add(figure);
        return geometry;
    }

    private void StopAnimations()
    {
        _visual?.StopAnimation("Translation");
        _visual?.StopAnimation("Scale");
    }
}
