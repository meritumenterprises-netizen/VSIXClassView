using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using System;
using System.ComponentModel.Design;
using System.Threading;
using System.Threading.Tasks;

internal sealed class ShowClassMembersCommand
{
    private readonly ClassMembersNavigatorPackage _package;
    private readonly DTE2? _dte;
    private readonly OleMenuCommand _command;

    private ShowClassMembersCommand(
        ClassMembersNavigatorPackage package,
        OleMenuCommandService commandService,
        DTE2? dte)
    {
        _package = package;
        _dte = dte;

        var commandId = new CommandID(CommandIds.CommandSet, CommandIds.ShowClassMembersCommandId);
        _command = new OleMenuCommand(Execute, commandId);
        _command.BeforeQueryStatus += BeforeQueryStatus;
        commandService.AddCommand(_command);
    }

    public static async Task InitializeAsync(
        ClassMembersNavigatorPackage package,
        CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
        var dte = await package.GetServiceAsync(typeof(DTE)) as DTE2;
        if (commandService != null)
        {
            _ = new ShowClassMembersCommand(package, commandService, dte);
        }
    }

    private void BeforeQueryStatus(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        _command.Visible = true;
        _command.Enabled = true;
    }

    private void Execute(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _package.JoinableTaskFactory
            .RunAsync(_package.ShowMembersToolWindowAsync)
            .FileAndForget("VSIXProject1/ShowClassMembers");
    }
}
