using Avalonia.Controls;
using Avalonia.Input;
using MC1.Windows.ViewModels;

namespace MC1.Windows.Views;

public partial class CoreScopeLinkView : UserControl
{
    public CoreScopeLinkView() => InitializeComponent();

    /// <summary>Enter in the link box links it.</summary>
    private void LinkBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not CoreScopeLinkViewModel vm) return;
        e.Handled = true;
        vm.LinkCommand.Execute(null);
    }
}
