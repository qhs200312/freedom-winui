using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using v2rayN.WinUI.Controls;
using v2rayN.WinUI.Services;
using Windows.ApplicationModel.DataTransfer;

namespace v2rayN.WinUI.Views;

internal sealed class OutboundIpDetailsView : UserControl
{
    public OutboundIpDetailsView(OutboundIpResult result)
    {
        var info = result.Details;
        var body = new StackPanel { Spacing = 16, Padding = new Thickness(8) };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var flag = new CountryFlag();
        flag.SetCountry(info.CountryCode);
        header.Children.Add(flag);
        header.Children.Add(new TextBlock { Text = result.LocationText, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 });
        body.Children.Add(header);
        var fields = new Grid { RowSpacing = 12, ColumnSpacing = 16 };
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var entries = new (string Label, string? Value)[]
        {
            ("IP", info.Ip),
            ("网络路径", result.Route),
            ("国家 / 地区", $"{info.CountryName ?? "未知"} ({info.CountryCode ?? "--"})"),
            ("城市 / 区域", string.Join(", ", new[] { info.City, info.Region }.Where(v => !string.IsNullOrWhiteSpace(v)))),
            ("自治域", info.Asn is { } asn ? $"AS{asn}" : null),
            ("服务商", info.Provider),
            ("组织", info.Organization),
            ("时区", info.Timezone),
            ("纬度 / 经度", info.Latitude != null && info.Longitude != null ? $"{info.Latitude:0.0000}, {info.Longitude:0.0000}" : null),
            ("检测时间", result.CheckedAt.ToString("yyyy-MM-dd HH:mm:ss"))
        };
        for (var row = 0; row < entries.Length; row++)
        {
            fields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = entries[row].Label, Opacity = 0.7 };
            var value = new TextBlock { Text = string.IsNullOrWhiteSpace(entries[row].Value) ? "未知" : entries[row].Value,
                TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            Grid.SetRow(label, row);
            Grid.SetRow(value, row);
            Grid.SetColumn(value, 1);
            fields.Children.Add(label);
            fields.Children.Add(value);
        }
        body.Children.Add(fields);
        var copy = new Button { Content = new SymbolIcon(Symbol.Copy) };
        ToolTipService.SetToolTip(copy, "复制 IP 信息");
        copy.Click += (_, _) =>
        {
            var data = new DataPackage();
            data.SetText(string.Join(Environment.NewLine, entries.Select(field => $"{field.Label}: {field.Value ?? "未知"}")));
            Clipboard.SetContent(data);
        };
        body.Children.Add(copy);
        Content = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }
}
