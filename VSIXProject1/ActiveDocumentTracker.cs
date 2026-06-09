using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using System;
using System.Threading.Tasks;
using VSIXProject1;

public sealed class ActiveDocumentTracker
{
    private readonly AsyncPackage _package;
    private readonly MembersToolWindowControl _control;
    private DTE2? _dte;

    public ActiveDocumentTracker(
        AsyncPackage package,
        MembersToolWindowControl control)
    {
        _package = package;
        _control = control;
        _control.MemberDoubleClicked += NavigateToMember;
    }

    public async Task InitializeAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        _dte = await _package.GetServiceAsync(typeof(DTE)) as DTE2;
        if (_dte == null)
        {
            _control.SetMembers(Array.Empty<MemberItem>());
            return;
        }

        // Simple first version: refresh when selection changes.
        _dte.Events.SelectionEvents.OnChange += Refresh;

        Refresh();
    }

    private void Refresh()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            var doc = _dte?.ActiveDocument;
            if (doc == null || !doc.FullName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                _control.SetMembers(Array.Empty<MemberItem>());
                return;
            }

            var textDoc = doc.Object("TextDocument") as TextDocument;
            if (textDoc == null)
                return;

            var editPoint = textDoc.StartPoint.CreateEditPoint();
            string text = editPoint.GetText(textDoc.EndPoint);

            int caretOffset = GetCaretOffset(textDoc);

            var members = MemberScanner.GetMembersForClassAtCaret(text, caretOffset);
            _control.SetMembers(members);
        }
        catch
        {
            _control.SetMembers(Array.Empty<MemberItem>());
        }
    }

    private static int GetCaretOffset(TextDocument textDoc)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var point = textDoc.Selection.ActivePoint;
        var start = textDoc.StartPoint.CreateEditPoint();

        return start.GetText(point).Length;
    }

    private void NavigateToMember(MemberItem item)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var doc = _dte?.ActiveDocument;
        if (doc == null)
            return;

        var textDoc = doc.Object("TextDocument") as TextDocument;
        if (textDoc == null)
            return;

        textDoc.Selection.MoveToLineAndOffset(
            item.NameStartLine + 1,
            item.NameStartColumn + 1);

        textDoc.Selection.MoveToLineAndOffset(
            item.NameEndLine + 1,
            item.NameEndColumn + 1,
            Extend: true);
    }
}
