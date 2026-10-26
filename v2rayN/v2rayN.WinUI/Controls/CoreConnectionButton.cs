using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;
using ShapePath = Microsoft.UI.Xaml.Shapes.Path;

namespace v2rayN.WinUI.Controls;

public sealed class CoreConnectionButton : Button
{
    private readonly StackPanel _pause = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly FontIcon _play = new() { Glyph = "\uE768", FontSize = 28, Margin = new Thickness(3, 0, 0, 0) };
    private readonly UISettings _uiSettings = new();
    private Visual? _badgeVisual;
    private bool _animationEnabled = true;
    private bool _rotating;

    public bool IsRunning { get; private set; }
    public bool IsBusy { get; private set; }

    public CoreConnectionButton()
    {
        for (var index = 0; index < 2; index++)
        {
            _pause.Children.Add(new Border
            {
                Width = 5, Height = 21, CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(Microsoft.UI.Colors.White)
            });
        }
        var icon = new Grid();
        icon.Children.Add(_pause);
        icon.Children.Add(_play);
        Content = icon;
        Loaded += (_, _) => UpdatePresentation();
        Unloaded += (_, _) => StopRotation();
        UpdateState(false);
    }

    protected override void OnApplyTemplate()
    {
        StopRotation();
        base.OnApplyTemplate();
        _badgeVisual = GetTemplateChild("ConnectionBadge") is ShapePath badge
            ? ElementCompositionPreview.GetElementVisual(badge) : null;
        if (_badgeVisual is not null)
        {
            _badgeVisual.CenterPoint = new Vector3(48, 48, 0);
            _badgeVisual.RotationAxis = Vector3.UnitZ;
        }
        UpdatePresentation();
    }

    public void UpdateState(bool isRunning)
    {
        IsRunning = isRunning;
        UpdatePresentation();
    }

    public void SetAnimationEnabled(bool enabled)
    {
        _animationEnabled = enabled;
        UpdateRotation();
    }

    public async Task ToggleAsync(Func<bool> isRunning, Func<Task> start, Func<Task> pause)
    {
        if (IsBusy) return;
        IsBusy = true;
        var wasRunning = isRunning();
        UpdateState(wasRunning);
        try
        {
            await (wasRunning ? pause() : start());
        }
        finally
        {
            IsBusy = false;
            UpdateState(isRunning());
        }
    }

    private void UpdatePresentation()
    {
        _pause.Visibility = IsRunning ? Visibility.Visible : Visibility.Collapsed;
        _play.Visibility = IsRunning ? Visibility.Collapsed : Visibility.Visible;
        IsEnabled = !IsBusy;
        var action = IsRunning ? "暂停运行" : "启动运行";
        var label = IsBusy ? IsRunning ? "正在暂停" : "正在启动" : action;
        AutomationProperties.SetName(this, label);
        ToolTipService.SetToolTip(this, label);
        UpdateRotation();
    }

    private void UpdateRotation()
    {
        var shouldRotate = IsRunning && IsLoaded && _animationEnabled && _uiSettings.AnimationsEnabled;
        if (!shouldRotate)
        {
            StopRotation();
            return;
        }
        if (_rotating || _badgeVisual is null) return;
        var rotation = _badgeVisual.Compositor.CreateScalarKeyFrameAnimation();
        rotation.InsertKeyFrame(0, 0);
        rotation.InsertKeyFrame(1, MathF.Tau, _badgeVisual.Compositor.CreateLinearEasingFunction());
        rotation.Duration = TimeSpan.FromSeconds(14);
        rotation.IterationBehavior = AnimationIterationBehavior.Forever;
        // Rotate only the gear; the pause/play symbol stays upright.
        _badgeVisual.StartAnimation("RotationAngle", rotation);
        _rotating = true;
    }

    private void StopRotation()
    {
        _badgeVisual?.StopAnimation("RotationAngle");
        if (_badgeVisual is not null) _badgeVisual.RotationAngle = 0;
        _rotating = false;
    }
}
