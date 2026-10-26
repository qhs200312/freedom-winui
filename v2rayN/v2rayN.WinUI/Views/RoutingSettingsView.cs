using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ServiceLib.Models.Entities;
using ServiceLib.ViewModels;

namespace v2rayN.WinUI.Views;

public sealed class RoutingSettingsView : UserControl
{
    public RoutingSettingsView(Func<ServiceLib.Enums.EViewAction, object?, Task<bool>> updateView)
    {
        var customRules = new RoutingRuleSettingViewModel(new RoutingItem(), updateView, independentRules: true);
        var root = new Grid { RowSpacing = 8 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TextBlock { Text = "自定义分流规则", FontSize = 20 });
        var hint = new TextBlock
        {
            Text = "单独添加网站的直连、代理或阻断规则，优先于所选基础方案。修改后自动保存并重新加载，对新连接生效。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75
        };
        Grid.SetRow(hint, 1);
        root.Children.Add(hint);
        var editor = new RoutingRuleEditorView(customRules);
        Grid.SetRow(editor, 2);
        root.Children.Add(editor);
        var presets = new Expander
        {
            Header = "基础路由方案与高级设置",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new DynamicCollectionView(new RoutingSettingViewModel(updateView), "RoutingItems", "SelectedSource")
            {
                Height = 220
            }
        };
        Grid.SetRow(presets, 3);
        root.Children.Add(presets);
        Content = root;
        Loaded += async (_, _) => await customRules.LoadNodeNamesAsync();
    }
}
