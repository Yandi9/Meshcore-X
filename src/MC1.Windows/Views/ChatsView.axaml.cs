using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MC1.Windows.ViewModels;

namespace MC1.Windows.Views;

public partial class ChatsView : UserControl
{
    public ChatsView()
    {
        InitializeComponent();
        // Narrower list when the window is small (e.g. snapped to half the screen).
        Split.SizeChanged += (_, e) => Split.ColumnDefinitions[0].Width = new GridLength(e.NewSize.Width < 820 ? 272 : 340);
        // Section headings sit in the list like rows but can't be selected (containers are reused, so set both ways).
        ConversationList.ContainerPrepared += (_, e) =>
        {
            if (e.Container is not ListBoxItem item) return;
            var header = ConversationList.ItemFromContainer(item) is ChatSectionHeader;
            item.IsEnabled = !header;
            item.Classes.Set("sectionheader", header);
        };
        // Clicking the chat that is already open: a chat marked unread while open counts as opened again.
        ConversationList.AddHandler(TappedEvent, (_, e) =>
        {
            if (DataContext is ChatsViewModel vm && (e.Source as Control)?.DataContext is ConversationListItem row) vm.ReopenActive(row.Key);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }
}
