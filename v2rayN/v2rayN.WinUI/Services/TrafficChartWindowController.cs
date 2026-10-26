using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using v2rayN.WinUI.Controls;

namespace v2rayN.WinUI.Services;

internal sealed class TrafficChartWindowController : IDisposable
{
    private readonly Window _window;
    private readonly AppWindow _appWindow;
    private readonly SmoothTrafficChart _chart;
    private readonly Func<bool> _pageVisible;
    private readonly Action<bool>? _setAdditionalAnimation;

    public TrafficChartWindowController(Window window, AppWindow appWindow, SmoothTrafficChart chart, Func<bool> pageVisible,
        Action<bool>? setAdditionalAnimation = null)
    {
        _window = window;
        _appWindow = appWindow;
        _chart = chart;
        _pageVisible = pageVisible;
        _setAdditionalAnimation = setAdditionalAnimation;
        _window.Activated += WindowActivated;
        _appWindow.Changed += WindowChanged;
        Refresh();
    }

    public void Refresh()
    {
        var minimized = _appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        // Losing focus does not make a visible chart stop animating.
        var enabled = _pageVisible() && _appWindow.IsVisible && !minimized;
        _chart.SetAnimationEnabled(enabled);
        _setAdditionalAnimation?.Invoke(enabled);
    }

    private void WindowActivated(object sender, WindowActivatedEventArgs args) => Refresh();
    private void WindowChanged(AppWindow sender, AppWindowChangedEventArgs args) => Refresh();

    public void Dispose()
    {
        _window.Activated -= WindowActivated;
        _appWindow.Changed -= WindowChanged;
        _chart.SetAnimationEnabled(false);
        _setAdditionalAnimation?.Invoke(false);
    }
}
