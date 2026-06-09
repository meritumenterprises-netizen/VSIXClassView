using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using VSIXProject1;

public sealed class ActiveDocumentTracker : IVsRunningDocTableEvents
{
    private readonly AsyncPackage _package;
    private readonly MembersToolWindowControl _control;
    private DTE2? _dte;
    private SelectionEvents? _selectionEvents;
    private DocumentEvents? _documentEvents;
    private IVsRunningDocumentTable? _runningDocumentTable;
    private uint _runningDocumentTableEventsCookie;
    private string? _loadedSourceFilePath;
    private string? _loadedClassName;
    private DateTime _lastSolutionExplorerRefreshUtc = DateTime.MinValue;

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

        _runningDocumentTable = await _package.GetServiceAsync(typeof(SVsRunningDocumentTable)) as IVsRunningDocumentTable;
        _runningDocumentTable?.AdviseRunningDocTableEvents(this, out _runningDocumentTableEventsCookie);

        Refresh();
    }

    public void RefreshActiveDocument()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (ActiveWindowIsDesigner())
        {
            return;
        }

        try
        {
            RefreshFromDocument(_dte?.ActiveDocument);
        }
        catch
        {
            _control.SetMembers(Array.Empty<MemberItem>());
        }
    }

    private void DocumentSaved(Document document)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            if (document.FullName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                RefreshFromDocument(document, force: true);
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
            if (ActiveWindowIsDesigner())
            {
                return;
            }

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

    private void RefreshFromDocument(Document? doc, bool force = false)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!force && ActiveWindowIsDesigner())
        {
            return;
        }

        if (doc == null || !doc.FullName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            _control.SetMembers(Array.Empty<MemberItem>());
            ClearLoadedSource();
            return;
        }

        var textDoc = doc.Object("TextDocument") as TextDocument;
        if (textDoc == null)
        {
            return;
        }

        if (!force && WasJustLoadedFromSolutionExplorer(doc.FullName))
        {
            return;
        }

        var editPoint = textDoc.StartPoint.CreateEditPoint();
        var text = editPoint.GetText(textDoc.EndPoint);
        var caretOffset = GetCaretOffset(textDoc);

        var classNameAtCaret = MemberScanner.GetClassNameAtCaret(text, caretOffset);
        if (!force && IsCurrentlyLoaded(doc.FullName, classNameAtCaret))
        {
            return;
        }

        var members = MemberScanner.GetMembersForClassAtCaret(text, caretOffset, doc.FullName);

        _control.SetMembers(members);
        SetLoadedSource(members, doc.FullName, classNameAtCaret);
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
        if (IsCurrentlyLoaded(selection.Value.FilePath, preferredClassName))
        {
            return true;
        }

        var members = MemberScanner.GetMembersForClassNameOrFirst(sourceText, preferredClassName, selection.Value.FilePath);
        _control.SetMembers(members);
        SetLoadedSource(members, selection.Value.FilePath, preferredClassName);
        _lastSolutionExplorerRefreshUtc = DateTime.UtcNow;

        return true;
    }

    private bool WasJustLoadedFromSolutionExplorer(string sourceFilePath)
    {
        return !string.IsNullOrEmpty(_loadedSourceFilePath)
            && string.Equals(_loadedSourceFilePath, sourceFilePath, StringComparison.OrdinalIgnoreCase)
            && DateTime.UtcNow - _lastSolutionExplorerRefreshUtc < TimeSpan.FromSeconds(2);
    }

    private bool IsCurrentlyLoaded(string sourceFilePath, string? className)
    {
        return !string.IsNullOrEmpty(_loadedSourceFilePath)
            && !string.IsNullOrEmpty(_loadedClassName)
            && string.Equals(_loadedSourceFilePath, sourceFilePath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_loadedClassName, className, StringComparison.Ordinal);
    }

    private void ClearLoadedSource()
    {
        _loadedSourceFilePath = null;
        _loadedClassName = null;
    }

    private void SetLoadedSource(
        IReadOnlyList<MemberItem> members,
        string sourceFilePath,
        string? requestedClassName)
    {
        _loadedSourceFilePath = sourceFilePath;
        _loadedClassName = members.FirstOrDefault()?.DeclaringClassName ?? requestedClassName;
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

    private bool ActiveWindowIsDesigner()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var activeWindow = _dte?.ActiveWindow;
        if (activeWindow == null || activeWindow.Type != vsWindowType.vsWindowTypeDocument)
        {
            return false;
        }

        var kind = activeWindow.Kind ?? string.Empty;
        var caption = activeWindow.Caption ?? string.Empty;

        return kind.IndexOf("Designer", StringComparison.OrdinalIgnoreCase) >= 0 ||
            caption.EndsWith(" [Design]", StringComparison.OrdinalIgnoreCase) ||
            caption.EndsWith(" (Design)", StringComparison.OrdinalIgnoreCase) ||
            caption.IndexOf("Designer", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public int OnAfterAttributeChange(uint docCookie, uint grfAttribs)
    {
        return VSConstants.S_OK;
    }

    public int OnAfterDocumentWindowHide(uint docCookie, IVsWindowFrame pFrame)
    {
        return VSConstants.S_OK;
    }

    public int OnAfterFirstDocumentLock(
        uint docCookie,
        uint dwRDTLockType,
        uint dwReadLocksRemaining,
        uint dwEditLocksRemaining)
    {
        return VSConstants.S_OK;
    }

    public int OnAfterSave(uint docCookie)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        RefreshActiveDocumentSoon(force: true);
        return VSConstants.S_OK;
    }

    public int OnBeforeDocumentWindowShow(uint docCookie, int fFirstShow, IVsWindowFrame pFrame)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        RefreshActiveDocumentSoon();
        return VSConstants.S_OK;
    }

    public int OnBeforeLastDocumentUnlock(
        uint docCookie,
        uint dwRDTLockType,
        uint dwReadLocksRemaining,
        uint dwEditLocksRemaining)
    {
        return VSConstants.S_OK;
    }

    private void RefreshActiveDocumentSoon(bool force = false)
    {
        _package.JoinableTaskFactory
            .RunAsync(async () =>
            {
                await System.Threading.Tasks.Task.Delay(100);
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!force && ActiveWindowIsDesigner())
                {
                    return;
                }

                if (force)
                {
                    try
                    {
                        RefreshFromDocument(_dte?.ActiveDocument, force: true);
                    }
                    catch
                    {
                        _control.SetMembers(Array.Empty<MemberItem>());
                    }
                }
                else
                {
                    RefreshActiveDocument();
                }
            })
            .FileAndForget("VSIXProject1/RefreshActiveDocumentSoon");
    }
}
