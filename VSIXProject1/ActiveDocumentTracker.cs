using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using VSIXProject1;

public sealed class ActiveDocumentTracker
{
    private readonly AsyncPackage _package;
    private readonly MembersToolWindowControl _control;
    private DTE2? _dte;
    private SelectionEvents? _selectionEvents;
    private DocumentEvents? _documentEvents;

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
        _documentEvents = _dte.Events.DocumentEvents;
        _documentEvents.DocumentSaved += DocumentSaved;

        Refresh();
    }

    private void DocumentSaved(Document document)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            if (document.FullName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                RefreshFromDocument(document);
            }
        }
        catch
        {
            _control.SetMembers(Array.Empty<MemberItem>());
        }
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

            RefreshFromDocument(_dte?.ActiveDocument);
        }
        catch
        {
            _control.SetMembers(Array.Empty<MemberItem>());
        }
    }

    private void RefreshFromDocument(Document? doc)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

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
            var projectItem = FindOwningProjectItem(selectedItem);
            if (projectItem != null)
            {
                var filePath = GetProjectItemFilePath(projectItem);
                if (filePath != null)
                {
                    var className = GetSelectedClassNameFromFile(filePath, selectedItem.Name);
                    return (filePath, className);
                }
            }
        }

        return null;
    }

    private static ProjectItem? FindOwningProjectItem(UIHierarchyItem selectedItem)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (selectedItem.Object is ProjectItem projectItem)
        {
            return projectItem;
        }

        var parent = GetParentHierarchyItem(selectedItem);
        return parent == null ? null : FindOwningProjectItem(parent);
    }

    private static UIHierarchyItem? GetParentHierarchyItem(UIHierarchyItem selectedItem)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            var collection = selectedItem.Collection;
            var parent = collection
                .GetType()
                .InvokeMember("Parent", BindingFlags.GetProperty, null, collection, null);

            return parent as UIHierarchyItem;
        }
        catch
        {
            return null;
        }
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

    private static string? GetSelectedClassNameFromFile(string filePath, string selectedNodeName)
    {
        var sourceText = File.ReadAllText(filePath);
        return MemberScanner.GetClassNames(sourceText)
            .FirstOrDefault(className => string.Equals(className, selectedNodeName, StringComparison.Ordinal));
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
