using System.Collections;
using System.Reflection;
using System.Reactive.Linq;
using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Markup;
using ServiceLib.Common;
using ServiceLib.Events;
using ServiceLib.ViewModels;

namespace v2rayN.WinUI.Views;

public sealed class DynamicCollectionView : UserControl
{
    public ListView List { get; }
    private IDisposable? _subscriptionRefresh;
    private bool _subscriptionViewLoaded;

    public DynamicCollectionView(object viewModel, string collectionProperty, string selectedProperty, string displayPath = "Remarks")
    {
        DataContext = viewModel;
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var bar = new CommandBar
        {
            DefaultLabelPosition = CommandBarDefaultLabelPosition.Right,
            IsOpen = false,
            IsDynamicOverflowEnabled = false,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent)
        };
        foreach (var property in viewModel.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (typeof(ICommand).IsAssignableFrom(property.PropertyType) && property.GetValue(viewModel) is ICommand command)
            {
                bar.PrimaryCommands.Add(new AppBarButton
                {
                    Label = GetCommandLabel(property.Name),
                    Icon = new SymbolIcon(GetSymbol(property.Name)),
                    Command = command
                });
            }
        }
        root.Children.Add(bar);

        List = new ListView
        {
            SelectionMode = ListViewSelectionMode.Extended,
            IsItemClickEnabled = true,
            DisplayMemberPath = displayPath,
            Margin = new Thickness(0, 8, 0, 0)
        };
        List.SetBinding(ItemsControl.ItemsSourceProperty, new Binding { Source = viewModel, Path = new PropertyPath(collectionProperty) });
        List.SetBinding(ListView.SelectedItemProperty, new Binding { Source = viewModel, Path = new PropertyPath(selectedProperty), Mode = BindingMode.TwoWay });
        List.SelectionChanged += (_, _) => UpdateMultiSelection(viewModel);
        if (viewModel is ServiceLib.ViewModels.RoutingSettingViewModel routing)
        {
            List.DoubleTapped += async (_, _) => await routing.RoutingAdvancedEditAsync(false);
        }
        if (viewModel is SubSettingViewModel subscriptions)
        {
            List.DisplayMemberPath = string.Empty;
            List.ItemTemplate = (DataTemplate)XamlReader.Load("""
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                    <StackPanel Padding="8,10" Spacing="4">
                        <TextBlock Text="{Binding Remarks}" FontWeight="SemiBold" TextTrimming="CharacterEllipsis" />
                        <TextBlock Text="{Binding Url}" FontSize="12" Opacity="0.75" TextTrimming="CharacterEllipsis"
                            IsTextSelectionEnabled="True" ToolTipService.ToolTip="{Binding Url}" />
                    </StackPanel>
                </DataTemplate>
                """);
            List.ItemContainerStyle = new Style { TargetType = typeof(ListViewItem) };
            List.ItemContainerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            List.DoubleTapped += async (_, _) => await subscriptions.EditSubAsync(false);
            var refresh = new AppBarButton { Label = "刷新", Icon = new SymbolIcon(Symbol.Refresh) };
            refresh.Click += async (_, _) => await RefreshSubscriptionsAsync(subscriptions);
            bar.PrimaryCommands.Add(refresh);
            Loaded += async (_, _) =>
            {
                _subscriptionViewLoaded = true;
                _subscriptionRefresh?.Dispose();
                _subscriptionRefresh = AppEvents.SubscriptionsRefreshRequested.AsObservable().Subscribe(_ =>
                    DispatcherQueue.TryEnqueue(async () =>
                    {
                        if (_subscriptionViewLoaded) await RefreshSubscriptionsAsync(subscriptions);
                    }));
                await RefreshSubscriptionsAsync(subscriptions);
            };
            Unloaded += (_, _) =>
            {
                _subscriptionViewLoaded = false;
                _subscriptionRefresh?.Dispose();
                _subscriptionRefresh = null;
            };
        }
        Grid.SetRow(List, 1);
        root.Children.Add(List);
        Content = root;
    }

    private static async Task RefreshSubscriptionsAsync(SubSettingViewModel subscriptions)
    {
        try { await subscriptions.RefreshSubItems(); }
        catch (Exception ex) { Logging.SaveLog("Refresh subscription management", ex); }
    }

    private void UpdateMultiSelection(object viewModel)
    {
        var property = viewModel.GetType().GetProperty("SelectedSources", BindingFlags.Instance | BindingFlags.Public);
        var elementType = property?.PropertyType.IsGenericType == true ? property.PropertyType.GetGenericArguments()[0] : null;
        if (property?.CanWrite != true || elementType is null)
        {
            return;
        }

        var listType = typeof(List<>).MakeGenericType(elementType);
        if (Activator.CreateInstance(listType) is not IList selected)
        {
            return;
        }

        foreach (var item in List.SelectedItems)
        {
            selected.Add(item);
        }

        property.SetValue(viewModel, selected);
    }

    private static string GetCommandLabel(string name)
    {
        return name switch
        {
            "SubAddCmd" or "RuleAddCmd" or "RoutingAdvancedAddCmd" => "添加",
            "SubEditCmd" => "编辑",
            "RoutingAdvancedEditCmd" => "编辑规则",
            "SubDeleteCmd" or "RuleRemoveCmd" or "RoutingAdvancedRemoveCmd" => "删除",
            "SubShareCmd" => "分享",
            "SaveCmd" => "保存",
            "RoutingAdvancedSetDefaultCmd" => "设为默认",
            "RoutingAdvancedImportRulesCmd" => "一键导入规则集",
            "ConnectionCloseCmd" => "关闭连接",
            "ConnectionCloseAllCmd" => "关闭全部",
            _ => name.Replace("Cmd", "")
        };
    }

    private static Symbol GetSymbol(string name)
    {
        if (name.Contains("Add"))
        {
            return Symbol.Add;
        }

        if (name.Contains("Delete") || name.Contains("Remove") || name.Contains("Close"))
        {
            return Symbol.Delete;
        }

        if (name.Contains("Share") || name.Contains("Export"))
        {
            return Symbol.Share;
        }

        if (name.Contains("Save"))
        {
            return Symbol.Save;
        }

        return Symbol.Edit;
    }
}
