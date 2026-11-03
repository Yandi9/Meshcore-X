using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using MC1.Windows.ViewModels;

namespace MC1.Windows.Views;

public partial class NodeManagementView : UserControl
{
    private RemoteAdminWorkspace? _watched;

    public NodeManagementView()
    {
        InitializeComponent();
        CliBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && DataContext is NodeManagementViewModel vm)
            {
                vm.Admin.SendCliCommand.Execute(null);
                e.Handled = true;
            }
        };
    }

    private void OnCliLinesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.UIThread.Post(() => CliScroll.ScrollToEnd(), DispatcherPriority.Background);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // The workspace outlives this view (it is kept per node), so unhook from the old one.
        if (_watched is not null) _watched.CliLines.CollectionChanged -= OnCliLinesChanged;
        _watched = (DataContext as NodeManagementViewModel)?.Admin;
        if (_watched is not null) _watched.CliLines.CollectionChanged += OnCliLinesChanged;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_watched is null) return;
        _watched.CliLines.CollectionChanged -= OnCliLinesChanged;
        _watched.CliLines.CollectionChanged += OnCliLinesChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_watched is not null) _watched.CliLines.CollectionChanged -= OnCliLinesChanged;
        (DataContext as NodeManagementViewModel)?.Detached();
    }
}
