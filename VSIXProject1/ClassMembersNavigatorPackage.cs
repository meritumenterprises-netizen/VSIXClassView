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

        await ShowClassMembersCommand.InitializeAsync(this, DisposalToken);

        _dte = await GetServiceAsync(typeof(DTE)) as DTE2;
        if (_dte != null)
        {
            _solutionEvents = _dte.Events.SolutionEvents;
            _solutionEvents.Opened += SolutionOpened;
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

            if (await MembersToolWindowIsVisibleAsync())
            {
                return;
            }

            var solutionFilePathToCheck = solutionFilePath!;
            var solutionHasCSharpProject = await System.Threading.Tasks.Task.Run(
                () => SolutionFileReferencesCSharpProject(solutionFilePathToCheck),
                DisposalToken);

            await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);

            if (solutionHasCSharpProject && !await MembersToolWindowIsVisibleAsync())
            {
                await ShowMembersToolWindowAsync();
            }
        }
        catch
        {
            // Auto-display should not make package load or solution load fail.
        }
    }

    private async System.Threading.Tasks.Task<bool> MembersToolWindowIsVisibleAsync()
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
            await EnsureTrackerInitializedAsync(membersWindow);

            if (membersWindow.Frame is IVsWindowFrame frame)
            {
                Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(frame.Show());
                _tracker?.RefreshActiveDocument();
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
