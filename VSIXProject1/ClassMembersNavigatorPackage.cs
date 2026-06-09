using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Task = System.Threading.Tasks.Task;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[ProvideToolWindow(typeof(MembersToolWindow))]
[ProvideMenuResource("Menus.ctmenu", 1)]
[Guid("1CB85797-9814-4F4A-B7D6-4A4E4DD06B45")]
public sealed class ClassMembersNavigatorPackage : AsyncPackage
{
    private ActiveDocumentTracker? _tracker;

    protected override async Task InitializeAsync(
        CancellationToken cancellationToken,
        IProgress<ServiceProgressData> progress)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        await ShowClassMembersCommand.InitializeAsync(this, cancellationToken);
    }

    public async Task ShowMembersToolWindowAsync()
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);

        var window = await FindToolWindowAsync(
            typeof(MembersToolWindow),
            0,
            create: true,
            DisposalToken);

        if (window is MembersToolWindow membersWindow)
        {
            if (_tracker == null)
            {
                _tracker = new ActiveDocumentTracker(this, membersWindow.Control);
                await _tracker.InitializeAsync();
            }

            if (membersWindow.Frame is IVsWindowFrame frame)
            {
                Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(frame.Show());
            }
        }
    }
}
