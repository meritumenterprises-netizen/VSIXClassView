using EnvDTE;
using EnvDTE80;
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
    private DTE2? _dte;
    private SolutionEvents? _solutionEvents;

    protected override async Task InitializeAsync(
        CancellationToken cancellationToken,
        IProgress<ServiceProgressData> progress)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        await ShowClassMembersCommand.InitializeAsync(this, cancellationToken);

        _dte = await GetServiceAsync(typeof(DTE)) as DTE2;
        if (_dte != null)
        {
            _solutionEvents = _dte.Events.SolutionEvents;
            _solutionEvents.Opened += SolutionOpened;
        }

        await EnsureTrackerForRestoredToolWindowAsync();
    }

    private void SolutionOpened()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        JoinableTaskFactory
            .RunAsync(EnsureTrackerForRestoredToolWindowAsync)
            .FileAndForget("VSIXProject1/EnsureRestoredToolWindowTracker");
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
            await EnsureTrackerInitializedAsync(membersWindow);

            if (membersWindow.Frame is IVsWindowFrame frame)
            {
                Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(frame.Show());
            }
        }
    }

    private async Task EnsureTrackerForRestoredToolWindowAsync()
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);

        try
        {
            var window = await FindToolWindowAsync(
                typeof(MembersToolWindow),
                0,
                create: false,
                DisposalToken);

            if (window is MembersToolWindow membersWindow)
            {
                await EnsureTrackerInitializedAsync(membersWindow);
            }
        }
        catch
        {
            // A restored tool window should never make package load fail.
        }
    }

    private async Task EnsureTrackerInitializedAsync(MembersToolWindow membersWindow)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);

        if (_tracker != null)
        {
            return;
        }

        _tracker = new ActiveDocumentTracker(this, membersWindow.Control);
        await _tracker.InitializeAsync();
    }
}
