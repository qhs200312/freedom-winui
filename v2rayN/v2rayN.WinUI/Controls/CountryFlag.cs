using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace v2rayN.WinUI.Controls;

public sealed class CountryFlag : UserControl
{
    public string? CountryCode { get; private set; }

    public CountryFlag()
    {
        Width = 28;
        Height = 21;
        IsTabStop = false;
        SetCountry(null);
    }

    public void SetCountry(string? code)
    {
        CountryCode = code?.Trim().ToUpperInvariant();
        var valid = CountryCode is { Length: 2 } iso && iso.All(c => c is >= 'A' and <= 'Z');
        var file = valid ? $"{CountryCode!.ToLowerInvariant()}.svg" : null;
        if (file != null && File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "Flags", file)))
        {
            Content = new Border
            {
                Width = 28, Height = 21, CornerRadius = new CornerRadius(2),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(70, 128, 128, 128)),
                Child = new Image
                {
                    Stretch = Stretch.Uniform,
                    Source = new SvgImageSource(new Uri($"ms-appx:///Assets/Flags/{file}"))
                }
            };
            ToolTipService.SetToolTip(this, CountryCode);
        }
        else
        {
            Content = new SymbolIcon(Symbol.World) { Width = 20, Height = 20 };
            ToolTipService.SetToolTip(this, "地区未知");
        }
    }
}
