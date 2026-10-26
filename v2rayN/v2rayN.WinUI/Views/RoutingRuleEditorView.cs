using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using ServiceLib;
using ServiceLib.ViewModels;

namespace v2rayN.WinUI.Views;

public sealed class RoutingRuleEditorView : UserControl
{
    private readonly RoutingRuleSettingViewModel _viewModel;
    private readonly ListView _rulesList;

    public RoutingRuleEditorView(RoutingRuleSettingViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        if (!viewModel.IndependentRules)
        {
            root.Children.Add(BuildRoutingInfo());
        }

        var commandBar = BuildCommandBar();
        Grid.SetRow(commandBar, 1);
        root.Children.Add(commandBar);

        _rulesList = new ListView
        {
            SelectionMode = ListViewSelectionMode.Extended,
            IsItemClickEnabled = true,
            DisplayMemberPath = "DisplayName",
            Margin = new Thickness(0, 4, 0, 0)
        };
        _rulesList.SetBinding(ItemsControl.ItemsSourceProperty, Bind(viewModel, nameof(viewModel.RulesItems), BindingMode.OneWay));
        _rulesList.SetBinding(ListView.SelectedItemProperty, Bind(viewModel, nameof(viewModel.SelectedSource), BindingMode.TwoWay));
        _rulesList.SelectionChanged += (_, _) => _viewModel.SelectedSources = _rulesList.SelectedItems.Cast<ServiceLib.Models.Dto.RulesItemModel>().ToList();
        _rulesList.DoubleTapped += async (_, _) => await _viewModel.RuleEditAsync(false);
        Grid.SetRow(_rulesList, 2);
        root.Children.Add(_rulesList);

        Content = root;
    }

    private Expander BuildRoutingInfo()
    {
        var routing = _viewModel.SelectedRouting;
        var grid = new Grid { RowSpacing = 8, ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < 4; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        AddField(grid, CreateTextBox("规则集名称", routing, nameof(routing.Remarks)), 0, 0);
        AddField(grid, CreateTextBox("排序", routing, nameof(routing.Sort)), 0, 1);
        AddField(grid, CreateComboBox("Xray 域名策略", Global.DomainStrategies, routing, nameof(routing.DomainStrategy)), 1, 0);
        AddField(grid, CreateComboBox("sing-box 域名策略", Global.DomainStrategies4Sbox, routing, nameof(routing.DomainStrategy4Singbox)), 1, 1);
        AddField(grid, CreateTextBox("订阅地址", routing, nameof(routing.Url)), 2, 0);
        AddField(grid, CreateTextBox("自定义图标", routing, nameof(routing.CustomIcon)), 2, 1);
        var rulesetPath = CreateTextBox("sing-box 自定义规则集目录", routing, nameof(routing.CustomRulesetPath4Singbox));
        AddField(grid, rulesetPath, 3, 0, 2);

        return new Expander
        {
            Header = "规则集信息",
            IsExpanded = false,
            Content = grid,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 4, 8, 8)
        };
    }

    private CommandBar BuildCommandBar()
    {
        var bar = new CommandBar
        {
            DefaultLabelPosition = CommandBarDefaultLabelPosition.Right,
            IsOpen = true,
            IsDynamicOverflowEnabled = true,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
        };
        bar.PrimaryCommands.Add(Command("添加域名", Symbol.Add, _viewModel.WebsiteAddCmd));
        bar.PrimaryCommands.Add(Command("编辑", Symbol.Edit, _viewModel.RuleEditCmd));
        bar.PrimaryCommands.Add(Command("删除", Symbol.Delete, _viewModel.RuleRemoveCmd));
        if (!_viewModel.IndependentRules)
        {
            bar.PrimaryCommands.Add(Command("默认节点", Symbol.Forward, _viewModel.DefaultNodeCmd));
            bar.PrimaryCommands.Add(Command("保存规则集", Symbol.Save, _viewModel.SaveCmd));
        }
        bar.SecondaryCommands.Add(Command("应用分流", Symbol.AllApps, _viewModel.ApplicationAddCmd));
        bar.SecondaryCommands.Add(Command("新增高级规则", Symbol.Add, _viewModel.RuleAddCmd));
        bar.SecondaryCommands.Add(Command("从文件导入", Symbol.Download, _viewModel.ImportRulesFromFileCmd));
        bar.SecondaryCommands.Add(Command("从剪贴板导入", Symbol.Paste, _viewModel.ImportRulesFromClipboardCmd));
        if (!_viewModel.IndependentRules)
        {
            bar.SecondaryCommands.Add(Command("从订阅地址导入", Symbol.World, _viewModel.ImportRulesFromUrlCmd));
        }
        bar.SecondaryCommands.Add(Command("导出选中", Symbol.Share, _viewModel.RuleExportSelectedCmd));
        bar.SecondaryCommands.Add(Command("移到顶部", Symbol.Up, _viewModel.MoveTopCmd));
        bar.SecondaryCommands.Add(Command("上移", Symbol.Up, _viewModel.MoveUpCmd));
        bar.SecondaryCommands.Add(Command("下移", Symbol.Download, _viewModel.MoveDownCmd));
        bar.SecondaryCommands.Add(Command("移到底部", Symbol.Download, _viewModel.MoveBottomCmd));
        return bar;
    }

    private static AppBarButton Command(string label, Symbol symbol, System.Windows.Input.ICommand command)
    {
        return new()
        {
            Label = label,
            Icon = new SymbolIcon(symbol),
            Command = command
        };
    }

    private static TextBox CreateTextBox(string header, object source, string property)
    {
        var textBox = new TextBox { Header = header, HorizontalAlignment = HorizontalAlignment.Stretch };
        textBox.SetBinding(TextBox.TextProperty, Bind(source, property, BindingMode.TwoWay));
        return textBox;
    }

    private static ComboBox CreateComboBox(string header, object items, object source, string property)
    {
        var comboBox = new ComboBox
        {
            Header = header,
            ItemsSource = items,
            IsEditable = true,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        comboBox.SetBinding(ComboBox.TextProperty, Bind(source, property, BindingMode.TwoWay));
        return comboBox;
    }

    private static Binding Bind(object source, string property, BindingMode mode)
    {
        return new()
        {
            Source = source,
            Path = new PropertyPath(property),
            Mode = mode,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
        };
    }

    private static void AddField(Grid grid, FrameworkElement field, int row, int column, int columnSpan = 1)
    {
        Grid.SetRow(field, row);
        Grid.SetColumn(field, column);
        Grid.SetColumnSpan(field, columnSpan);
        grid.Children.Add(field);
    }
}
