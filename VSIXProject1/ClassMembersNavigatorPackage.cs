using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Task = System.Threading.Tasks.Task;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideToolWindow(typeof(MembersToolWindow))]
[ProvideMenuResource("Menus.ctmenu", 1)]
[Guid("1CB85797-9814-4F4A-B7D6-4A4E4DD06B45")]
public sealed class ClassMembersNavigatorPackage : AsyncPackage
{
    private ActiveDocumentTracker? _tracker;
    private DTE2? _dte;
    private SolutionEvents? _solutionEvents;
    private ShowClassMembersCommand? _showClassMembersCommand;
    private MembersToolWindow? _observedMembersWindow;

    protected override async Task InitializeAsync(
        CancellationToken cancellationToken,
        IProgress<ServiceProgressData> progress)
    {
        await base.InitializeAsync(cancellationToken, progress);

        JoinableTaskFactory
            .RunAsync(InitializeAfterShellIsIdleAsync)
            .FileAndForget("VSIXProject1/InitializeAfterShellIsIdle");
    }

    private async Task InitializeAfterShellIsIdleAsync()
    {
        await System.Threading.Tasks.Task.Delay(3000, DisposalToken);
        await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);

        _showClassMembersCommand = await ShowClassMembersCommand.InitializeAsync(this, DisposalToken);

        _dte = await GetServiceAsync(typeof(DTE)) as DTE2;
        if (_dte != null)
        {
            _solutionEvents = _dte.Events.SolutionEvents;
            _solutionEvents.Opened += SolutionOpened;
            _solutionEvents.AfterClosing += SolutionAfterClosing;
        }

        await EnsureTrackerForRestoredToolWindowAsync();
        await ShowMembersToolWindowForCSharpSolutionAsync();
    }

    private void SolutionOpened()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        JoinableTaskFactory
            .RunAsync(OnSolutionOpenedAsync)
            .FileAndForget("VSIXProject1/SolutionOpened");
    }

    private async Task OnSolutionOpenedAsync()
    {
        await EnsureTrackerForRestoredToolWindowAsync();
        await System.Threading.Tasks.Task.Delay(3000, DisposalToken);
        await ShowMembersToolWindowForCSharpSolutionAsync();
    }

    private void SolutionAfterClosing()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        JoinableTaskFactory
            .RunAsync(ResetVisibleMembersToolWindowAsync)
            .FileAndForget("VSIXProject1/SolutionAfterClosing");
    }

    private async Task ResetVisibleMembersToolWindowAsync()
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);

        var window = await FindToolWindowAsync(
            typeof(MembersToolWindow),
            0,
            create: false,
            DisposalToken);

        if (window is not MembersToolWindow membersWindow)
        {
            return;
        }

        ObserveMembersToolWindow(membersWindow);

        if (membersWindow.Frame is IVsWindowFrame frame &&
            frame.IsVisible() == VSConstants.S_OK)
        {
            await EnsureTrackerInitializedAsync(membersWindow);
            _tracker?.Reset();
        }
    }

    private async Task ShowMembersToolWindowForCSharpSolutionAsync()
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);

        try
        {
            var solutionFilePath = _dte?.Solution?.FullName;
            if (string.IsNullOrEmpty(solutionFilePath))
            {
                return;
            }

            if (await IsMembersToolWindowVisibleAsync())
            {
                return;
            }

            var solutionFilePathToCheck = solutionFilePath!;
            var solutionHasCSharpProject = await System.Threading.Tasks.Task.Run(
                () => SolutionFileReferencesCSharpProject(solutionFilePathToCheck),
                DisposalToken);

            await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);

            if (solutionHasCSharpProject && !await IsMembersToolWindowVisibleAsync())
            {
                await ShowMembersToolWindowAsync();
            }
        }
        catch
        {
            // Auto-display should not make package load or solution load fail.
        }
    }

    public async System.Threading.Tasks.Task<bool> IsMembersToolWindowVisibleAsync()
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);

        var window = await FindToolWindowAsync(
            typeof(MembersToolWindow),
            0,
            create: false,
            DisposalToken);

        if (window is not MembersToolWindow membersWindow)
        {
            return false;
        }

        ObserveMembersToolWindow(membersWindow);
        await EnsureTrackerInitializedAsync(membersWindow);

        if (membersWindow.Frame is not IVsWindowFrame frame)
        {
            return false;
        }

        return frame.IsVisible() == VSConstants.S_OK;
    }

    private static bool SolutionFileReferencesCSharpProject(string solutionFilePath)
    {
        try
        {
            return File.Exists(solutionFilePath) &&
                File.ReadAllText(solutionFilePath).IndexOf(".csproj", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch
        {
            return false;
        }
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
            ObserveMembersToolWindow(membersWindow);
            await EnsureTrackerInitializedAsync(membersWindow);

            if (membersWindow.Frame is IVsWindowFrame frame)
            {
                Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(frame.Show());
                RefreshShowClassMembersCommandStatus();
                _tracker?.RefreshActiveDocument();
            }
        }
    }

    public async Task FocusMembersToolWindowAsync()
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);

        var window = await FindToolWindowAsync(
            typeof(MembersToolWindow),
            0,
            create: false,
            DisposalToken);

        if (window is not MembersToolWindow membersWindow)
        {
            return;
        }

        ObserveMembersToolWindow(membersWindow);

        if (membersWindow.Frame is IVsWindowFrame frame &&
            frame.IsVisible() == VSConstants.S_OK)
        {
            Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(frame.Show());
            RefreshShowClassMembersCommandStatus();
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
                ObserveMembersToolWindow(membersWindow);
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

    private void ObserveMembersToolWindow(MembersToolWindow membersWindow)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (ReferenceEquals(_observedMembersWindow, membersWindow))
        {
            return;
        }

        if (_observedMembersWindow != null)
        {
            _observedMembersWindow.VisibilityChanged -= MembersToolWindowVisibilityChanged;
        }

        _observedMembersWindow = membersWindow;
        _observedMembersWindow.VisibilityChanged += MembersToolWindowVisibilityChanged;
    }

    private void MembersToolWindowVisibilityChanged(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        RefreshShowClassMembersCommandStatus();
    }

    private void RefreshShowClassMembersCommandStatus()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _showClassMembersCommand?.RefreshStatus();
    }
}
