using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using System;
using System.IO;
using System.Threading.Tasks;
using VSIXProject1;

public sealed class ActiveDocumentTracker
{
    private readonly AsyncPackage _package;
    private readonly MembersToolWindowControl _control;
    private DTE2? _dte;
    private SelectionEvents? _selectionEvents;

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

        _selectionEvents = _dte.Events.SelectionEvents;
        _selectionEvents.OnChange += Refresh;

        Refresh();
    }

    private void Refresh()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            if (TryRefreshFromSolutionExplorerSelection())
            {
                return;
            }

            var doc = _dte?.ActiveDocument;
            if (doc == null || !doc.FullName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                _control.SetMembers(Array.Empty<MemberItem>());
                return;
            }

            var textDoc = doc.Object("TextDocument") as TextDocument;
            if (textDoc == null)
            {
                return;
            }

            var editPoint = textDoc.StartPoint.CreateEditPoint();
            var text = editPoint.GetText(textDoc.EndPoint);
            var caretOffset = GetCaretOffset(textDoc);
            var members = MemberScanner.GetMembersForClassAtCaret(text, caretOffset, doc.FullName);

            _control.SetMembers(members);
        }
        catch
        {
            _control.SetMembers(Array.Empty<MemberItem>());
        }
    }

    private bool TryRefreshFromSolutionExplorerSelection()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_dte?.ActiveWindow?.Type != vsWindowType.vsWindowTypeSolutionExplorer)
        {
            return false;
        }

        var selection = GetSelectedSolutionExplorerCodeSelection();
        if (selection == null)
        {
            return false;
        }

        var sourceText = File.ReadAllText(selection.Value.FilePath);
        var preferredClassName = selection.Value.ClassName ?? Path.GetFileNameWithoutExtension(selection.Value.FilePath);
        var members = MemberScanner.GetMembersForClassNameOrFirst(sourceText, preferredClassName, selection.Value.FilePath);
        _control.SetMembers(members);

        return true;
    }

    private (string FilePath, string? ClassName)? GetSelectedSolutionExplorerCodeSelection()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var selectedItems = _dte?.ToolWindows.SolutionExplorer.SelectedItems as Array;
        if (selectedItems == null || selectedItems.Length == 0)
        {
            return null;
        }

        foreach (UIHierarchyItem selectedItem in selectedItems)
        {
            if (selectedItem.Object is CodeClass codeClass)
            {
                var filePath = GetProjectItemFilePath(codeClass.ProjectItem);
                if (filePath != null)
                {
                    return (filePath, codeClass.Name);
                }
            }

            if (selectedItem.Object is ProjectItem projectItem)
            {
                if (projectItem.Object is CodeClass projectItemClass)
                {
                    var classFilePath = GetProjectItemFilePath(projectItemClass.ProjectItem);
                    if (classFilePath != null)
                    {
                        return (classFilePath, projectItemClass.Name);
                    }
                }

                var filePath = GetProjectItemFilePath(projectItem);
                if (filePath != null)
                {
                    var selectedClassName = GetClassNameFromProjectItem(projectItem, selectedItem.Name);
                    if (selectedClassName != null)
                    {
                        return (filePath, selectedClassName);
                    }

                    return (filePath, null);
                }
            }
        }

        return null;
    }

    private static string? GetClassNameFromProjectItem(ProjectItem projectItem, string selectedItemName)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var fileCodeModel = projectItem.FileCodeModel;
        if (fileCodeModel == null)
        {
            return null;
        }

        return FindSelectedClassName(fileCodeModel.CodeElements, selectedItemName);
    }

    private static string? FindSelectedClassName(CodeElements codeElements, string selectedItemName)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        foreach (CodeElement codeElement in codeElements)
        {
            if (codeElement is CodeClass codeClass &&
                string.Equals(codeClass.Name, selectedItemName, StringComparison.Ordinal))
            {
                return codeClass.Name;
            }

            var childClassName = codeElement.Children == null
                ? null
                : FindSelectedClassName(codeElement.Children, selectedItemName);

            if (childClassName != null)
            {
                return childClassName;
            }
        }

        return null;
    }

    private static string? GetProjectItemFilePath(ProjectItem projectItem)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        for (short index = 1; index <= projectItem.FileCount; index++)
        {
            var filePath = projectItem.FileNames[index];
            if (filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && File.Exists(filePath))
            {
                return filePath;
            }
        }

        return null;
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

        if (!string.IsNullOrEmpty(item.SourceFilePath) &&
            !string.Equals(_dte?.ActiveDocument?.FullName, item.SourceFilePath, StringComparison.OrdinalIgnoreCase))
        {
            _dte?.ItemOperations.OpenFile(item.SourceFilePath);
        }

        var doc = _dte?.ActiveDocument;
        if (doc == null)
        {
            return;
        }

        var textDoc = doc.Object("TextDocument") as TextDocument;
        if (textDoc == null)
        {
            return;
        }

        textDoc.Selection.MoveToLineAndOffset(
            item.NameStartLine + 1,
            item.NameStartColumn + 1);

        textDoc.Selection.MoveToLineAndOffset(
            item.NameEndLine + 1,
            item.NameEndColumn + 1,
            Extend: true);
    }
}
