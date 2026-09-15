using System.Collections;
using System.ComponentModel;
using System.Drawing.Text;
using System.Reflection;
using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using ServiceLib;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Models.Entities;
using ServiceLib.ViewModels;

namespace v2rayN.WinUI.Views;

public sealed class DynamicFormView : UserControl
{
    private const double TwoColumnBreakpoint = 720;

    private static readonly HashSet<string> HiddenProperties =
    [
        "IsModified", "BlIsWindows", "BlIsLinux", "BlIsIsMacOS", "BlIsNonWindows",
        "BindingRoot", "ViewModelActivator", "ThrownExceptions", "IndexId", "ConfigType",
        "ConfigVersion", "Subid", "IsSub", "PreSocksPort", "Sort"
    ];

    private static readonly Dictionary<string, int> SectionOrder = new()
    {
        ["基础信息"] = 0,
        ["身份验证"] = 10,
        ["本地监听"] = 20,
        ["传输设置"] = 30,
        ["TLS 与安全"] = 40,
        ["性能与测试"] = 50,
        ["系统代理"] = 60,
        ["隐私保护"] = 65,
        ["TUN 模式"] = 70,
        ["界面与行为"] = 80,
        ["更新与数据源"] = 90,
        ["核心选择"] = 100,
        ["高级选项"] = 110
    };

    private readonly bool _collapseSections;

    public DynamicFormView(object viewModel, bool includeCommands = true, bool collapseSections = false)
    {
        _collapseSections = collapseSections;
        DataContext = viewModel;
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        if (includeCommands && viewModel is not OptionSettingViewModel)
        {
            root.Children.Add(BuildCommandBar(viewModel));
        }

        var formPanel = new StackPanel
        {
            Spacing = 16,
            Padding = new Thickness(4, 8, 16, 24),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AddObjectFields(formPanel, viewModel, null, 0, _collapseSections);

        var scrollViewer = new ScrollViewer
        {
            Content = formPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        Grid.SetRow(scrollViewer, 1);
        root.Children.Add(scrollViewer);
        Content = root;

        if (viewModel is OptionSettingViewModel settings)
        {
            EnableAutoSave(settings, scrollViewer);
        }
    }

    private static void EnableAutoSave(OptionSettingViewModel settings, FrameworkElement owner)
    {
        if (settings is not INotifyPropertyChanged observable)
        {
            return;
        }

        var ready = false;
        var loadVersion = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            await settings.AutoSaveAsync();
        };
        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (!ready || string.IsNullOrWhiteSpace(args.PropertyName) || args.PropertyName.StartsWith("Bl", StringComparison.Ordinal))
            {
                return;
            }

            timer.Stop();
            timer.Start();
        };
        owner.Loaded += async (_, _) =>
        {
            var version = ++loadVersion;
            await Task.Delay(300);
            if (version != loadVersion || !owner.IsLoaded)
            {
                return;
            }

            observable.PropertyChanged -= handler;
            observable.PropertyChanged += handler;
            ready = true;
        };
        owner.Unloaded += (_, _) =>
        {
            loadVersion++;
            ready = false;
            timer.Stop();
            observable.PropertyChanged -= handler;
        };
    }

    private static CommandBar BuildCommandBar(object source)
    {
        var bar = new CommandBar
        {
            DefaultLabelPosition = CommandBarDefaultLabelPosition.Right,
            IsOpen = false,
            IsDynamicOverflowEnabled = false,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
        };

        foreach (var property in source.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!typeof(ICommand).IsAssignableFrom(property.PropertyType) || property.GetValue(source) is not ICommand command)
            {
                continue;
            }
            if (source is AddServerViewModel && property.Name is nameof(AddServerViewModel.FetchCertCmd) or nameof(AddServerViewModel.FetchCertChainCmd))
            {
                continue;
            }

            bar.PrimaryCommands.Add(new AppBarButton
            {
                Label = CommandLabels.GetValueOrDefault(property.Name, Humanize(property.Name.Replace("Cmd", ""))),
                Icon = new SymbolIcon(GetCommandSymbol(property.Name)),
                Command = command
            });
        }
        return bar;
    }

    private static void AddObjectFields(StackPanel panel, object source, string? heading, int depth, bool collapseSections)
    {
        if (heading is not null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = PropertyLabels.GetValueOrDefault(heading, Humanize(heading)),
                FontSize = depth == 0 ? 20 : 17,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 8, 0, 0)
            });
        }

        var fields = new List<FormField>();
        var nestedObjects = new List<(string Name, object Value)>();
        var order = 0;
        foreach (var property in source.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length > 0 || HiddenProperties.Contains(property.Name))
            {
                continue;
            }
            if (!ShouldIncludeProperty(source, property.Name))
            {
                continue;
            }

            if (source is AddServerViewModel server && property.Name is nameof(AddServerViewModel.Cert) or nameof(AddServerViewModel.CertTip) or nameof(AddServerViewModel.CertSha))
            {
                if (property.Name == nameof(AddServerViewModel.Cert)
                    && server.SelectedSource.StreamSecurity == Global.StreamSecurity)
                {
                    fields.Add(new FormField(
                        () => BuildCertificatePinningField(server),
                        GetFieldMetadata(nameof(AddServerViewModel.Cert), true, order++)));
                }
                continue;
            }

            var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (typeof(IList<string>).IsAssignableFrom(property.PropertyType))
            {
                var metadata = GetFieldMetadata(property.Name, true, order++);
                fields.Add(new FormField(() => BuildStringListField(source, property), metadata));
                continue;
            }
            if (typeof(ICommand).IsAssignableFrom(propertyType) || typeof(IEnumerable).IsAssignableFrom(propertyType) && propertyType != typeof(string))
            {
                continue;
            }
            if (IsScalar(propertyType))
            {
                var metadata = GetFieldMetadata(property.Name, IsFullWidth(property.Name), order++);
                fields.Add(new FormField(() => BuildField(source, property, propertyType, Nullable.GetUnderlyingType(property.PropertyType) is not null), metadata));
                continue;
            }
            if (depth < 1 && property.GetValue(source) is { } nested)
            {
                nestedObjects.Add((property.Name, nested));
            }
        }

        foreach (var nested in nestedObjects.Where(item => item.Name == "SelectedSource"))
        {
            AddObjectFields(panel, nested.Value, null, depth + 1, collapseSections);
        }

        foreach (var section in fields
                     .GroupBy(field => field.Metadata.Section)
                     .OrderBy(group => SectionOrder.GetValueOrDefault(group.Key, int.MaxValue)))
        {
            panel.Children.Add(BuildSection(
                section.Key,
                section.OrderBy(field => field.Metadata.Order).ToList(),
                !collapseSections));
        }

        foreach (var nested in nestedObjects.Where(item => item.Name != "SelectedSource"))
        {
            var nestedPanel = new StackPanel { Spacing = 16, HorizontalAlignment = HorizontalAlignment.Stretch };
            var expander = new Expander
            {
                Header = PropertyLabels.GetValueOrDefault(nested.Name, Humanize(nested.Name)),
                IsExpanded = !collapseSections,
                Content = nestedPanel,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            var populated = false;
            void PopulateNested()
            {
                if (populated)
                {
                    return;
                }
                populated = true;
                AddObjectFields(nestedPanel, nested.Value, null, depth + 1, collapseSections);
            }
            expander.Expanding += (_, _) => PopulateNested();
            if (!collapseSections)
            {
                PopulateNested();
            }
            panel.Children.Add(expander);
        }
    }

    private static FrameworkElement BuildCertificatePinningField(AddServerViewModel server)
    {
        var status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72
        };
        status.SetBinding(TextBlock.TextProperty, new Binding
        {
            Source = server,
            Path = new PropertyPath(nameof(AddServerViewModel.CertTip)),
            Mode = BindingMode.OneWay
        });

        var actions = new CommandBar
        {
            DefaultLabelPosition = CommandBarDefaultLabelPosition.Right,
            IsOpen = false,
            IsDynamicOverflowEnabled = false,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Padding = new Thickness(0)
        };
        var fetchCert = new AppBarButton
        {
            Label = "获取证书",
            Icon = new SymbolIcon(Symbol.Download),
            Command = server.FetchCertCmd
        };
        ToolTipService.SetToolTip(fetchCert, "获取服务器叶证书");
        actions.PrimaryCommands.Add(fetchCert);

        var fetchChain = new AppBarButton
        {
            Label = "获取证书链",
            Icon = new SymbolIcon(Symbol.Download),
            Command = server.FetchCertChainCmd
        };
        ToolTipService.SetToolTip(fetchChain, "获取服务器完整证书链");
        actions.PrimaryCommands.Add(fetchChain);

        var certSha = new TextBox
        {
            Header = "证书 SHA-256",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        certSha.SetBinding(TextBox.TextProperty, new Binding
        {
            Source = server,
            Path = new PropertyPath(nameof(AddServerViewModel.CertSha)),
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
        });

        var cert = new TextBox
        {
            Header = "完整 PEM 证书",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            MinHeight = 112,
            FontFamily = new FontFamily("Cascadia Mono"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        cert.SetBinding(TextBox.TextProperty, new Binding
        {
            Source = server,
            Path = new PropertyPath(nameof(AddServerViewModel.Cert)),
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
        });

        var content = new StackPanel
        {
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        content.Children.Add(status);
        content.Children.Add(actions);
        content.Children.Add(certSha);
        content.Children.Add(cert);

        return new Expander
        {
            Header = new TextBlock { Text = "证书固定（高级）", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
            IsExpanded = !string.IsNullOrWhiteSpace(server.Cert) || !string.IsNullOrWhiteSpace(server.CertSha),
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
    }

    private static Expander BuildSection(string title, IReadOnlyList<FormField> fields, bool isExpanded)
    {
        var grid = new Grid
        {
            ColumnSpacing = 16,
            RowSpacing = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var populated = false;
        var isWide = false;
        void UpdateLayout(double width)
        {
            if (!populated)
            {
                return;
            }
            var nextIsWide = width >= TwoColumnBreakpoint;
            if (grid.ColumnDefinitions.Count > 0 && nextIsWide == isWide)
            {
                return;
            }

            isWide = nextIsWide;
            ArrangeFields(grid, fields, isWide);
        }

        grid.Loaded += (_, _) => UpdateLayout(grid.ActualWidth);
        grid.SizeChanged += (_, args) => UpdateLayout(args.NewSize.Width);
        void Populate()
        {
            if (populated)
            {
                return;
            }
            populated = true;
            foreach (var field in fields)
            {
                grid.Children.Add(field.Editor);
            }
            UpdateLayout(grid.ActualWidth);
        }

        var expander = new Expander
        {
            Header = new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
            IsExpanded = isExpanded,
            Content = grid,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        expander.Expanding += (_, _) => Populate();
        if (isExpanded)
        {
            Populate();
        }
        return expander;
    }

    private static void ArrangeFields(Grid grid, IReadOnlyList<FormField> fields, bool twoColumns)
    {
        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();
        var columnCount = twoColumns ? 2 : 1;
        for (var column = 0; column < columnCount; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        var row = 0;
        var columnIndex = 0;
        foreach (var field in fields)
        {
            var fullWidth = !twoColumns || field.Metadata.FullWidth;
            if (fullWidth && columnIndex > 0)
            {
                row++;
                columnIndex = 0;
            }
            while (grid.RowDefinitions.Count <= row)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            Grid.SetRow(field.Editor, row);
            Grid.SetColumn(field.Editor, columnIndex);
            Grid.SetColumnSpan(field.Editor, fullWidth ? columnCount : 1);
            if (fullWidth || ++columnIndex >= columnCount)
            {
                row++;
                columnIndex = 0;
            }
        }
    }

    private static FrameworkElement BuildStringListField(object source, PropertyInfo property)
    {
        var propertyName = property.Name;
        if (propertyName == "DestOverride")
        {
            var panel = new StackPanel { Spacing = 6 };
            panel.Children.Add(new TextBlock { Text = GetLabel(propertyName) });
            var list = new ListView
            {
                ItemsSource = Global.destOverrideProtocols,
                SelectionMode = ListViewSelectionMode.Multiple,
                MaxHeight = 168
            };
            var initializing = true;
            list.Loaded += (_, _) =>
            {
                var values = property.GetValue(source) as IList<string> ?? [];
                foreach (var value in values)
                {
                    if (Global.destOverrideProtocols.Contains(value))
                    {
                        list.SelectedItems.Add(value);
                    }
                }
                initializing = false;
            };
            list.SelectionChanged += (_, _) =>
            {
                if (initializing)
                {
                    return;
                }

                property.SetValue(source, list.SelectedItems.Cast<string>().ToList());
            };
            panel.Children.Add(list);
            return panel;
        }

        var textBox = new TextBox
        {
            Header = GetLabel(propertyName),
            PlaceholderText = "使用逗号分隔多个值"
        };
        var initializingText = true;
        textBox.Loaded += (_, _) =>
        {
            textBox.Text = string.Join(", ", property.GetValue(source) as IList<string> ?? []);
            initializingText = false;
        };
        textBox.TextChanged += (_, _) =>
        {
            if (initializingText)
            {
                return;
            }

            property.SetValue(source, textBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList());
        };
        return textBox;
    }

    private static FrameworkElement BuildField(object source, PropertyInfo property, Type propertyType, bool nullable)
    {
        var label = GetLabel(property.Name);
        var binding = new Binding
        {
            Source = source,
            Path = new PropertyPath(property.Name),
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
        };

        FrameworkElement editor;
        if (BuildChoiceField(source, property, binding, label) is { } choiceEditor)
        {
            editor = choiceEditor;
        }
        else if (propertyType == typeof(bool) && nullable)
        {
            var checkBox = new CheckBox { Content = label, MinHeight = 40, VerticalAlignment = VerticalAlignment.Center };
            checkBox.SetBinding(CheckBox.IsCheckedProperty, binding);
            editor = checkBox;
        }
        else if (propertyType == typeof(bool))
        {
            var toggle = new ToggleSwitch { Header = label, MinHeight = 48 };
            toggle.SetBinding(ToggleSwitch.IsOnProperty, binding);
            editor = toggle;
        }
        else if (propertyType.IsEnum)
        {
            var combo = new ComboBox { Header = label, ItemsSource = Enum.GetValues(propertyType) };
            combo.SetBinding(ComboBox.SelectedItemProperty, binding);
            editor = combo;
        }
        else
        {
            var multiline = IsMultiline(property.Name);
            var textBox = new TextBox
            {
                Header = label,
                AcceptsReturn = multiline,
                TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                MinHeight = multiline ? 112 : 0
            };
            if (multiline)
            {
                textBox.FontFamily = new FontFamily("Cascadia Mono");
            }

            textBox.SetBinding(TextBox.TextProperty, binding);
            editor = textBox;
        }

        editor.HorizontalAlignment = HorizontalAlignment.Stretch;
        return editor;
    }

    private static FrameworkElement? BuildChoiceField(object source, PropertyInfo property, Binding binding, string label)
    {
        if (source is not OptionSettingViewModel)
        {
            return null;
        }

        if (property.Name == "MainGirdOrientation")
        {
            var combo = new ComboBox
            {
                Header = label,
                ItemsSource = Utils.GetEnumNames<EGirdOrientation>(),
                SelectedIndex = Math.Max(0, Convert.ToInt32(property.GetValue(source)))
            };
            combo.SelectionChanged += (_, _) => property.SetValue(source, combo.SelectedIndex);
            return combo;
        }

        if (property.Name == "CurrentFontFamily")
        {
            var combo = new ComboBox
            {
                Header = label,
                IsEditable = true,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            combo.SetBinding(ComboBox.TextProperty, binding);

            var fontsLoaded = false;
            combo.DropDownOpened += async (_, _) =>
            {
                if (fontsLoaded)
                {
                    return;
                }

                fontsLoaded = true;
                combo.ItemsSource = await Task.Run(() => InstalledFonts.Value);
            };
            return combo;
        }

        var definition = GetSettingChoices(property.Name);
        if (definition is null)
        {
            return null;
        }

        var editor = new ComboBox
        {
            Header = label,
            ItemsSource = definition.Items,
            IsEditable = definition.Editable,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        if (definition.Editable)
        {
            editor.SetBinding(ComboBox.TextProperty, binding);
        }
        else
        {
            editor.SetBinding(ComboBox.SelectedItemProperty, binding);
        }
        return editor;
    }

    private static ChoiceDefinition? GetSettingChoices(string name)
    {
        return name switch
        {
            "Loglevel" => new(Global.LogLevels, false),
            "DefFingerprint" => new(Global.Fingerprints.Prepend(string.Empty).Distinct().ToList(), false),
            "DefUserAgent" => new(Global.UserAgent.Prepend(string.Empty).Distinct().ToList(), false),
            "Mux4SboxProtocol" => new(Global.SingboxMuxs, false),
            "FragmentPackets" => new(Global.FragmentPacketsOptions, false),
            "SpeedTestTimeout" => new(Enumerable.Range(2, 5).Select(value => value * 5).ToList(), false),
            "MixedConcurrencyCount" => new(Enumerable.Range(2, 7).ToList(), false),
            "SpeedTestUrl" => new(Global.SpeedTestUrls, true),
            "SpeedPingTestUrl" => new(Global.SpeedPingTestUrls, true),
            "UdpTestTarget" => new(Global.UdpTestTargets, true),
            "SubConvertUrl" => new(Global.SubConvertUrls, true),
            "GeoFileSourceUrl" => new(Global.GeoFilesSources, true),
            "SrsFileSourceUrl" => new(Global.SingboxRulesetSources, true),
            "RoutingRulesSourceUrl" => new(Global.RoutingRulesSources, true),
            "IPAPIUrl" => new(Global.IPAPIUrls, true),
            "RootCertProvider" => new(Global.RootCertProviders, false),
            "SystemProxyAdvancedProtocol" => new(Global.IEProxyProtocols, false),
            "TunStack" => new(Global.TunStacks, false),
            "TunMtu" => new(Global.TunMtus, false),
            "TunIcmpRouting" => new(Global.TunIcmpRoutingPolicies, false),
            _ when name.StartsWith("CoreType", StringComparison.Ordinal) => new(Global.CoreTypes, false),
            _ => null
        };
    }

    private static readonly Lazy<IReadOnlyList<string>> InstalledFonts = new(() =>
    {
        try
        {
            using var collection = new InstalledFontCollection();
            return collection.Families.Select(font => font.Name).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name).ToList();
        }
        catch
        {
            return [string.Empty];
        }
    });

    private static FieldMetadata GetFieldMetadata(string name, bool fullWidth, int order)
    {
        var section = name switch
        {
            "ProxyStunTraffic" or "EnableUdpInterception" or "UdpInterceptionApplications" => "隐私保护",
            _ when name.StartsWith("Tun", StringComparison.OrdinalIgnoreCase) => "TUN 模式",
            _ when name.Contains("SystemProxy", StringComparison.OrdinalIgnoreCase) || name.StartsWith("NotProxy", StringComparison.OrdinalIgnoreCase) => "系统代理",
            _ when name.StartsWith("CoreType", StringComparison.OrdinalIgnoreCase) => "核心选择",
            "LocalPort" or "SecondLocalPortEnabled" or "UdpEnabled" or "SniffingEnabled" or "DestOverride" or "RouteOnly" or "AllowLANConn" or "NewPort4LAN" or "User" or "Pass" => "本地监听",
            _ when IsAuthenticationField(name) => "身份验证",
            _ when IsSecurityField(name) => "TLS 与安全",
            _ when IsTransportField(name) => "传输设置",
            _ when IsPerformanceField(name) => "性能与测试",
            _ when IsUiField(name) => "界面与行为",
            _ when IsSourceField(name) => "更新与数据源",
            "Remarks" or "Address" or "Port" or "Url" or "Enabled" or "CoreType" or "DirName" or "UserName" => "基础信息",
            _ => "高级选项"
        };
        return new FieldMetadata(section, order, fullWidth);
    }

    private static bool IsAuthenticationField(string name)
    {
        return name is "Id" or "AlterId" or "Password" or "Security" or "VmessSecurity" or "VlessEncryption" or "SsMethod" or "Flow" or "SalamanderPass" or "WgPublicKey" or "WgPresharedKey";
    }

    private static bool IsSecurityField(string name)
    {
        return name.Contains("Tls", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Cert", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Fingerprint", StringComparison.OrdinalIgnoreCase) ||
        name is "StreamSecurity" or "Sni" or "Alpn" or "AllowInsecure" or "PublicKey" or "ShortId" or "SpiderX";
    }

    private static bool IsTransportField(string name)
    {
        return name.Contains("Grpc", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Kcp", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Xhttp", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Header", StringComparison.OrdinalIgnoreCase) ||
        name is "Network" or "Host" or "Path" or "Ports" or "HopInterval" or "RawHeaderType";
    }

    private static bool IsPerformanceField(string name)
    {
        return name.Contains("Speed", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Mbps", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Mux", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Fragment", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Concurrency", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Packet", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Mtu", StringComparison.OrdinalIgnoreCase) ||
        name is "EnableCacheFile4Sbox" or "CongestionControl" or "UdpTestTarget";
    }

    private static bool IsUiField(string name)
    {
        return name is
        "AutoRun" or "EnableStatistics" or "KeepOlderDedupl" or "DisplayRealTimeSpeed" or
        "EnableAutoAdjustMainLvColWidth" or "AutoHideStartup" or "Hide2TrayWhenClose" or
        "MacOSShowInDock" or "EnableDragDropSort" or "DoubleClick2Activate" or
        "TrayMenuServersLimit" or "CurrentFontFamily" or "EnableHWA" or "MainGirdOrientation";
    }

    private static bool IsSourceField(string name)
    {
        return name.Contains("SourceUrl", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
        name is "SubConvertUrl" or "IPAPIUrl" or "RootCertProvider";
    }

    private static bool IsFullWidth(string name)
    {
        return IsMultiline(name) ||
        name.EndsWith("Url", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("Path", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Exceptions", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Address", StringComparison.OrdinalIgnoreCase) && name.Length > "Address".Length;
    }

    private static bool ShouldIncludeProperty(object source, string name)
    {
        if (source is AddServerViewModel server)
        {
            return ShouldIncludeServerEditorProperty(server, name);
        }
        if (source is ProfileItem profile)
        {
            return ShouldIncludeProfileProperty(profile, name);
        }
        return true;
    }

    private static bool ShouldIncludeServerEditorProperty(AddServerViewModel server, string name)
    {
        var type = server.SelectedSource.ConfigType;
        if (name is "SelectedSource" or "CoreType")
        {
            return true;
        }

        if (name is "AllowInsecure" or "Cert" or "CertSha")
        {
            return type != EConfigType.WireGuard;
        }

        if (name is "TransportHeaderType" or "TransportHost" or "TransportPath" or "TransportExtraText")
        {
            return type is EConfigType.VMess or EConfigType.Shadowsocks or EConfigType.SOCKS or EConfigType.HTTP or EConfigType.VLESS or EConfigType.Trojan;
        }

        return type switch
        {
            EConfigType.VMess => name is "AlterId" or "VmessSecurity" or "MuxEnabled",
            EConfigType.Shadowsocks => name is "SsMethod" or "Uot" or "MuxEnabled",
            EConfigType.SOCKS => false,
            EConfigType.HTTP => name is "HttpHeadersJson",
            EConfigType.VLESS => name is "Flow" or "VlessEncryption" or "MuxEnabled",
            EConfigType.Trojan => name is "Flow" or "MuxEnabled",
            EConfigType.Hysteria2 => name is "SalamanderPass" or "Ports" or "UpMbps" or "DownMbps" or "HopInterval" or "Hy2RealmUrl" or "GeckoMinPacketSize" or "GeckoMaxPacketSize",
            EConfigType.TUIC => name is "CongestionControl",
            EConfigType.WireGuard => name is "WgPublicKey" or "WgPresharedKey" or "WgInterfaceAddress" or "WgReserved" or "WgMtu",
            EConfigType.Anytls => false,
            EConfigType.Naive => name is "NaiveQuic" or "CongestionControl" or "InsecureConcurrency" or "Uot",
            _ => true
        };
    }

    private static bool ShouldIncludeProfileProperty(ProfileItem profile, string name)
    {
        if (name is "Remarks" or "Address" or "Port" or "Password" or "DisplayLog")
        {
            return true;
        }

        if (name == "Username")
        {
            return profile.ConfigType is EConfigType.SOCKS or EConfigType.HTTP or EConfigType.TUIC or EConfigType.Naive;
        }
        if (name == "Network")
        {
            return profile.ConfigType is EConfigType.VMess or EConfigType.Shadowsocks or EConfigType.SOCKS or EConfigType.HTTP or EConfigType.VLESS or EConfigType.Trojan;
        }
        if (profile.ConfigType == EConfigType.WireGuard)
        {
            return false;
        }

        return name is "StreamSecurity" or "Sni" or "Alpn" or "Fingerprint" or "PublicKey" or
            "ShortId" or "SpiderX" or "Mldsa65Verify" or "EchConfigList" or "VerifyPeerCertByName" or "Finalmask";
    }

    private static bool IsScalar(Type type)
    {
        return type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(DateTime);
    }

    private static bool IsMultiline(string name)
    {
        return name.Contains("Json", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Content", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Template", StringComparison.OrdinalIgnoreCase) ||
        name is "Hosts" or "Domain" or "IP" or "Process" or "SystemProxyExceptions" or "TunRouteExcludeAddress" or "UdpInterceptionApplications";
    }

    private static Symbol GetCommandSymbol(string name)
    {
        if (name.Contains("Save", StringComparison.OrdinalIgnoreCase))
        {
            return Symbol.Save;
        }

        if (name.Contains("Add", StringComparison.OrdinalIgnoreCase))
        {
            return Symbol.Add;
        }

        if (name.Contains("Delete", StringComparison.OrdinalIgnoreCase) || name.Contains("Remove", StringComparison.OrdinalIgnoreCase))
        {
            return Symbol.Delete;
        }

        if (name.Contains("Import", StringComparison.OrdinalIgnoreCase))
        {
            return Symbol.Download;
        }

        if (name.Contains("Export", StringComparison.OrdinalIgnoreCase) || name.Contains("Share", StringComparison.OrdinalIgnoreCase))
        {
            return Symbol.Share;
        }

        if (name.Contains("Check", StringComparison.OrdinalIgnoreCase) || name.Contains("Refresh", StringComparison.OrdinalIgnoreCase))
        {
            return Symbol.Refresh;
        }

        return Symbol.Edit;
    }

    private static string GetLabel(string name)
    {
        return PropertyLabels.GetValueOrDefault(name, Humanize(name));
    }

    private static string Humanize(string value)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var character in value)
        {
            if (builder.Length > 0 && char.IsUpper(character))
            {
                builder.Append(' ');
            }

            builder.Append(character);
        }
        return builder.ToString();
    }

    private sealed record FieldMetadata(string Section, int Order, bool FullWidth);
    private sealed record FormField(Func<FrameworkElement> CreateEditor, FieldMetadata Metadata)
    {
        private FrameworkElement? _editor;
        public FrameworkElement Editor => _editor ??= CreateEditor();
    }
    private sealed record ChoiceDefinition(IEnumerable Items, bool Editable);

    private static readonly Dictionary<string, string> CommandLabels = new()
    {
        ["SaveCmd"] = "保存", ["SaveServerCmd"] = "保存", ["BrowseServerCmd"] = "浏览",
        ["EditServerCmd"] = "编辑", ["FetchCertCmd"] = "获取证书", ["FetchCertChainCmd"] = "获取证书链",
        ["ImportDefConfig4V2rayCompatibleCmd"] = "导入 Xray 默认值",
        ["ImportDefConfig4SingboxCompatibleCmd"] = "导入 sing-box 默认值",
        ["WebDavCheckCmd"] = "检查 WebDAV", ["RemoteBackupCmd"] = "远程备份",
        ["RemoteRestoreCmd"] = "远程还原", ["CheckOnlyCmd"] = "仅检查", ["CheckUpdateCmd"] = "检查并更新"
    };

    private static readonly Dictionary<string, string> PropertyLabels = new()
    {
        ["ProxyStunTraffic"] = "STUN 服务器优先走代理",
        ["EnableUdpInterception"] = "普通代理 UDP 接管（实验性，需要驱动）",
        ["UdpInterceptionApplications"] = "UDP 接管进程名（分号或换行分隔）",
        ["SelectedSource"] = "配置内容", ["Remarks"] = "备注", ["Address"] = "服务器地址", ["Port"] = "端口",
        ["Id"] = "用户 ID", ["Password"] = "密码", ["Security"] = "加密方式", ["Network"] = "传输协议",
        ["StreamSecurity"] = "传输层安全", ["Sni"] = "服务器名称 SNI", ["Alpn"] = "ALPN", ["Fingerprint"] = "指纹",
        ["AllowInsecure"] = "允许不安全连接", ["MuxEnabled"] = "启用 Mux", ["CoreType"] = "核心类型",
        ["Url"] = "地址", ["UserName"] = "用户名", ["User"] = "局域网用户名", ["Pass"] = "局域网密码",
        ["DirName"] = "目录名", ["Enabled"] = "启用", ["AutoUpdateInterval"] = "自动更新间隔（小时）",
        ["UserAgent"] = "User-Agent", ["LocalPort"] = "本地监听端口", ["SecondLocalPortEnabled"] = "启用第二本地端口",
        ["UdpEnabled"] = "启用 UDP", ["SniffingEnabled"] = "启用流量探测", ["DestOverride"] = "流量探测目标",
        ["RouteOnly"] = "仅路由不改写目标", ["AllowLANConn"] = "允许局域网连接", ["NewPort4LAN"] = "为局域网使用独立端口",
        ["LogEnabled"] = "启用日志", ["Loglevel"] = "日志等级", ["DisplayLog"] = "显示日志",
        ["DefFingerprint"] = "默认 TLS 指纹", ["DefUserAgent"] = "默认 User-Agent", ["SendThrough"] = "发送源地址",
        ["BindInterface"] = "绑定网络接口", ["Mux4SboxProtocol"] = "sing-box Mux 协议", ["EnableCacheFile4Sbox"] = "启用 sing-box 缓存文件",
        ["HyUpMbps"] = "Hysteria 上传带宽（Mbps）", ["HyDownMbps"] = "Hysteria 下载带宽（Mbps）",
        ["EnableFragment"] = "启用 TLS 分片", ["EnableFinalFragment"] = "启用最终分片", ["FragmentPackets"] = "分片数据包",
        ["FragmentLength"] = "分片长度", ["FragmentInterval"] = "分片间隔", ["FragmentMaxSplit"] = "最大分片数",
        ["AutoRun"] = "开机启动", ["EnableStatistics"] = "启用流量统计", ["KeepOlderDedupl"] = "去重时保留较旧配置",
        ["DisplayRealTimeSpeed"] = "显示实时速度", ["EnableAutoAdjustMainLvColWidth"] = "自动调整列表列宽",
        ["AutoHideStartup"] = "启动后隐藏", ["Hide2TrayWhenClose"] = "关闭时最小化到托盘", ["MacOSShowInDock"] = "在 macOS Dock 中显示",
        ["EnableDragDropSort"] = "允许拖放排序", ["DoubleClick2Activate"] = "双击激活服务器", ["TrayMenuServersLimit"] = "托盘菜单服务器数量",
        ["CurrentFontFamily"] = "界面字体", ["SpeedTestTimeout"] = "测速超时（秒）", ["SpeedTestUrl"] = "测速文件地址",
        ["SpeedPingTestUrl"] = "延迟测试地址", ["UdpTestTarget"] = "UDP 测试目标", ["MixedConcurrencyCount"] = "混合测试并发数",
        ["EnableHWA"] = "启用硬件加速", ["SubConvertUrl"] = "订阅转换地址", ["MainGirdOrientation"] = "主界面布局方向",
        ["GeoFileSourceUrl"] = "Geo 数据源", ["SrsFileSourceUrl"] = "SRS 数据源", ["RoutingRulesSourceUrl"] = "路由规则数据源",
        ["IPAPIUrl"] = "出口 IP 检测 API", ["RootCertProvider"] = "根证书提供程序",
        ["NotProxyLocalAddress"] = "本地地址不使用代理", ["SystemProxyAdvancedProtocol"] = "系统代理协议",
        ["SystemProxyExceptions"] = "系统代理例外", ["CustomSystemProxyPacPath"] = "自定义 PAC 文件",
        ["CustomSystemProxyScriptPath"] = "自定义系统代理脚本", ["TunAutoRoute"] = "自动配置路由", ["TunStrictRoute"] = "严格路由",
        ["TunStack"] = "网络栈", ["TunMtu"] = "MTU", ["TunEnableIPv6Address"] = "启用 IPv6 地址",
        ["TunIcmpRouting"] = "ICMP 路由", ["TunEnableLegacyProtect"] = "启用旧版保护", ["TunRouteExcludeAddress"] = "排除的路由地址",
        ["Domain"] = "域名规则", ["IP"] = "IP 规则", ["Process"] = "进程规则", ["OutboundTag"] = "出站标签",
        ["Host"] = "主机", ["Path"] = "路径", ["Flow"] = "流控", ["VmessSecurity"] = "VMess 加密",
        ["VlessEncryption"] = "VLESS 加密", ["SsMethod"] = "Shadowsocks 加密", ["AlterId"] = "Alter ID",
        ["Cert"] = "证书", ["CertTip"] = "证书说明", ["CertSha"] = "证书 SHA", ["SalamanderPass"] = "Salamander 密码",
        ["Ports"] = "端口范围", ["UpMbps"] = "上传带宽（Mbps）", ["DownMbps"] = "下载带宽（Mbps）",
        ["HopInterval"] = "端口跳跃间隔", ["WgPublicKey"] = "WireGuard 公钥", ["WgPresharedKey"] = "WireGuard 预共享密钥",
        ["WgInterfaceAddress"] = "WireGuard 接口地址", ["WgReserved"] = "WireGuard 保留字节", ["WgMtu"] = "WireGuard MTU",
        ["Uot"] = "启用 UDP over TCP", ["CongestionControl"] = "拥塞控制", ["InsecureConcurrency"] = "不安全并发数",
        ["NaiveQuic"] = "启用 Naive QUIC", ["HttpHeadersJson"] = "HTTP 请求头 JSON", ["Hy2RealmUrl"] = "Hysteria2 Realm 地址",
        ["GeckoMinPacketSize"] = "Gecko 最小包大小", ["GeckoMaxPacketSize"] = "Gecko 最大包大小",
        ["RawHeaderType"] = "原始请求头类型", ["XhttpMode"] = "XHTTP 模式", ["XhttpExtra"] = "XHTTP 扩展配置",
        ["GrpcAuthority"] = "gRPC Authority", ["GrpcServiceName"] = "gRPC 服务名", ["GrpcMode"] = "gRPC 模式",
        ["KcpHeaderType"] = "KCP 请求头类型", ["KcpSeed"] = "KCP Seed", ["KcpMtu"] = "KCP MTU",
        ["Username"] = "用户名", ["TransportHeaderType"] = "传输请求头类型", ["TransportHost"] = "传输主机",
        ["TransportPath"] = "传输路径 / 服务名", ["TransportExtraText"] = "传输扩展配置",
        ["Mldsa65Verify"] = "ML-DSA-65 验证值", ["EchConfigList"] = "ECH 配置列表",
        ["VerifyPeerCertByName"] = "按名称验证对端证书", ["Finalmask"] = "Final mask",
        ["CoreType1"] = "VMess 核心", ["CoreType2"] = "自定义配置核心", ["CoreType3"] = "Shadowsocks 核心",
        ["CoreType4"] = "SOCKS 核心", ["CoreType5"] = "VLESS 核心", ["CoreType6"] = "Trojan 核心",
        ["CoreType7"] = "Hysteria2 核心", ["CoreType9"] = "WireGuard 核心"
    };
}
