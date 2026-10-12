using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ServiceLib.Models.Dto;
using Windows.Foundation;

namespace v2rayN.WinUI.Services;

internal static class ProfileContextMenuHelper
{
    public static bool TryShow(
        ListView list,
        MenuFlyout menu,
        DependencyObject? source,
        Point? position,
        Action<ProfileItemModel> selectProfile)
    {
        ListViewItem? container = null;
        for (var element = source; element is not null && element != list; element = VisualTreeHelper.GetParent(element))
        {
            if (element is ListViewItem item && list.ItemFromContainer(item) is ProfileItemModel)
            {
                container = item;
                break;
            }
        }
        var profile = container is null ? null : list.ItemFromContainer(container) as ProfileItemModel;

        // Keyboard requests can originate from the list itself, without a pointer position.
        if (profile is null && position is null)
        {
            profile = list.SelectedItem as ProfileItemModel;
            container = profile is null ? null : list.ContainerFromItem(profile) as ListViewItem;
        }

        if (profile is null || container is null)
        {
            return false;
        }

        if (!list.SelectedItems.Contains(profile))
        {
            list.SelectedItems.Clear();
            list.SelectedItem = profile;
        }

        selectProfile(profile);
        if (position is { } point)
        {
            menu.ShowAt(list, point);
        }
        else
        {
            menu.ShowAt(container);
        }

        return true;
    }
}
