using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeshCore;

namespace MC1.Windows.ViewModels;

/// <summary>"Remote commands" for a contact's companion radio (port of the iPhone's Remote CLI contact action).</summary>
public sealed partial class ContactDetailViewModel
{
    /// <summary>Offered for a chat contact that isn't blocked, while a firmware 1.18 radio is connected (a sensor stored
    /// with its own raw type stays hidden).</summary>
    [ObservableProperty] private bool _showsRemoteCli;
    [ObservableProperty] private bool _canRemoteCli;

    private void UpdateRemoteCliState()
    {
        ShowsRemoteCli = Core.Device.SupportsRadioCli && Contact.TypeRaw == (int)ContactType.Chat && !Contact.IsBlocked;
        CanRemoteCli = Core.IsReady;
    }

    [RelayCommand]
    private async Task RemoteCommands()
    {
        if (!Core.IsConnected) { await AppHost.Dialogs.ShowError(L.T("Not connected"), L.T("Connect to your radio first.")); return; }
        var vm = new RemoteCliConsoleViewModel(Contact);
        await AppHost.Dialogs.ShowDialog(vm, L.F("Remote commands · {0}", Contact.DisplayName), 760, 560);
        // Closing the console stops a command that's still waiting, so the contact is free for the next one.
        vm.CancelCurrent();
    }
}
