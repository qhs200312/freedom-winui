using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ServiceLib.Models.Dto;

namespace v2rayN.WinUI.Views;

public static class AppUpdateDialog
{
    public static ContentDialog Create(XamlRoot root, string currentVersion, AppUpdateRelease release, bool ready)
    {
        var content = new StackPanel { Spacing = 14, MaxWidth = 420 };
        content.Children.Add(new TextBlock
        {
            Text = ready ? "更新已下载，可以安装。" : "有新的软件版本可用。",
            TextWrapping = TextWrapping.Wrap
        });
        var versions = new Grid { ColumnSpacing = 20, RowSpacing = 8 };
        versions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        versions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        versions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        versions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddVersion(versions, 0, "当前版本", currentVersion);
        AddVersion(versions, 1, "新版本", release.Version);
        content.Children.Add(versions);
        content.Children.Add(new TextBlock { Text = "更新日志", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        content.Children.Add(new ScrollViewer
        {
            MaxHeight = 260,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(release.ReleaseNotes) ? "此版本未提供更新日志。" : release.ReleaseNotes,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            }
        });
        return new ContentDialog
        {
            XamlRoot = root,
            Title = "freedom 更新",
            Content = content,
            PrimaryButtonText = ready ? "安装并重启" : "下载更新",
            SecondaryButtonText = ready ? "退出后安装" : "静默下载",
            CloseButtonText = "稍后",
            DefaultButton = ContentDialogButton.Primary
        };
    }

    private static void AddVersion(Grid grid, int row, string label, string version)
    {
        var name = new TextBlock { Text = label, Opacity = 0.7 };
        var value = new TextBlock { Text = version, TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(name, row);
        Grid.SetRow(value, row);
        Grid.SetColumn(value, 1);
        grid.Children.Add(name);
        grid.Children.Add(value);
    }
}
