using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Threading;
using VSIXProject1;

public sealed class ActiveDocumentTracker : IVsRunningDocTableEvents
{
    private readonly AsyncPackage _package;
    private readonly MembersToolWindowControl _control;
    private DTE2? _dte;
    private SelectionEvents? _selectionEvents;
    private DocumentEvents? _documentEvents;
    private WindowEvents? _windowEvents;
    private IVsMonitorSelection? _monitorSelection;
    private IVsRunningDocumentTable? _runningDocumentTable;
    private DispatcherTimer? _currentOpenTextEditorTimer;
    private uint _runningDocumentTableEventsCookie;
    private Document? _currentOpenTextEditorDocument;
    private string? _loadedSourceFilePath;
    private string? _loadedClassName;
    private DateTime _lastSolutionExplorerRefreshUtc = DateTime.MinValue;
    private DateTime _suppressEditorRefreshUntilUtc = DateTime.MinValue;
    private bool _solutionExplorerSelectionOwnsMembersToolWindow;

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
        _windowEvents = _dte.Events.WindowEvents;
        _windowEvents.WindowActivated += WindowActivated;

        _monitorSelection = await _package.GetServiceAsync(typeof(SVsShellMonitorSelection)) as IVsMonitorSelection;
        _runningDocumentTable = await _package.GetServiceAsync(typeof(SVsRunningDocumentTable)) as IVsRunningDocumentTable;
        _runningDocumentTable?.AdviseRunningDocTableEvents(this, out _runningDocumentTableEventsCookie);
        _currentOpenTextEditorTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _currentOpenTextEditorTimer.Tick += CurrentOpenTextEditorTimerTick;
        _currentOpenTextEditorTimer.Start();

        Refresh();
    }

    private void CurrentOpenTextEditorTimerTick(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            if (TryRefreshFromSolutionExplorerSelection())
            {
                return;
            }

            if (SolutionExplorerSelectionOwnsMembersToolWindow())
            {
                return;
            }

            var currentOpenTextEditorDocument = GetCurrentOpenCSharpTextEditorDocument();
            if (currentOpenTextEditorDocument != null)
            {
                RefreshFromDocument(currentOpenTextEditorDocument);
            }
        }
        catch
        {
            _control.SetMembers(Array.Empty<MemberItem>());
        }
    }

    public void RefreshActiveDocument()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            if (TryRefreshFromSolutionExplorerSelection())
            {
                return;
            }

            if (SolutionExplorerSelectionOwnsMembersToolWindow())
            {
                return;
            }

            var currentOpenTextEditorDocument = GetCurrentOpenCSharpTextEditorDocument();
            if (currentOpenTextEditorDocument != null)
            {
                RefreshFromDocument(currentOpenTextEditorDocument, force: true);
                return;
            }

            if (TryRefreshFromActiveDesigner())
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

    public void Reset()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        _currentOpenTextEditorDocument = null;
        _suppressEditorRefreshUntilUtc = DateTime.MinValue;
        _solutionExplorerSelectionOwnsMembersToolWindow = false;
        ClearLoadedSource();
        _control.SetMembers(Array.Empty<MemberItem>());
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

    private void WindowActivated(Window gotFocus, Window lostFocus)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var focusedDocument = GetCurrentDocumentFrameDocument() ?? gotFocus.Document;
        if (gotFocus.Type == vsWindowType.vsWindowTypeDocument &&
            DocumentIsCSharpTextEditor(focusedDocument))
        {
            _solutionExplorerSelectionOwnsMembersToolWindow = false;
        }

        RememberCurrentOpenTextEditorDocument(focusedDocument);
        RefreshDocumentSoon(focusedDocument, force: true);
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

            if (SolutionExplorerSelectionOwnsMembersToolWindow())
            {
                return;
            }

            var currentOpenTextEditorDocument = GetCurrentOpenCSharpTextEditorDocument();
            if (currentOpenTextEditorDocument != null)
            {
                RefreshFromDocument(currentOpenTextEditorDocument, force: true);
                return;
            }

            if (ActiveWindowIsDesigner())
            {
                TryRefreshFromActiveDesigner();
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

        RememberCurrentOpenTextEditorDocument(doc);

        if (SolutionExplorerSelectionOwnsMembersToolWindowFor(doc.FullName))
        {
            return;
        }

        if (EditorRefreshIsSuppressedFor(doc.FullName))
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

        var classNameAtCaret = MemberScanner.GetClassDisplayNameAtCaret(text, caretOffset);
        if (!force && IsCurrentlyLoaded(doc.FullName, classNameAtCaret))
        {
            return;
        }

        var members = MemberScanner.GetMembersForClassAtCaret(text, caretOffset, doc.FullName);

        _solutionExplorerSelectionOwnsMembersToolWindow = false;
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
        var classDisplayName = MemberScanner.GetClassDisplayNameForClassNameOrFirst(sourceText, preferredClassName);
        var focusMembersToolWindow = selection.Value.FocusMembersToolWindow ||
            MemberScanner.ClassInheritsWindowsFormOrUserControl(sourceText, preferredClassName);

        if (IsCurrentlyLoaded(selection.Value.FilePath, classDisplayName))
        {
            _solutionExplorerSelectionOwnsMembersToolWindow = focusMembersToolWindow;
            SuppressEditorRefreshForFocusHandoff(focusMembersToolWindow);
            FocusMembersToolWindowForDesignerBackedSelection(focusMembersToolWindow);
            return true;
        }

        var members = MemberScanner.GetMembersForClassNameOrFirst(sourceText, preferredClassName, selection.Value.FilePath);
        _solutionExplorerSelectionOwnsMembersToolWindow = focusMembersToolWindow;
        _control.SetMembers(members);
        SetLoadedSource(members, selection.Value.FilePath, classDisplayName);
        _lastSolutionExplorerRefreshUtc = DateTime.UtcNow;
        SuppressEditorRefreshForFocusHandoff(focusMembersToolWindow);
        FocusMembersToolWindowForDesignerBackedSelection(focusMembersToolWindow);

        return true;
    }

    private bool TryRefreshFromActiveDesigner()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!ActiveWindowIsDesigner())
        {
            return false;
        }

        var filePath = _dte?.ActiveDocument?.FullName;
        if (string.IsNullOrEmpty(filePath))
        {
            return false;
        }

        var designerSourceFilePath = filePath!;
        if (!designerSourceFilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(designerSourceFilePath))
        {
            return false;
        }

        var sourceText = File.ReadAllText(designerSourceFilePath);
        var preferredClassName = Path.GetFileNameWithoutExtension(designerSourceFilePath);
        var classDisplayName = MemberScanner.GetClassDisplayNameForClassNameOrFirst(sourceText, preferredClassName);
        if (IsCurrentlyLoaded(designerSourceFilePath, classDisplayName))
        {
            return true;
        }

        var members = MemberScanner.GetMembersForClassNameOrFirst(sourceText, preferredClassName, designerSourceFilePath);
        _control.SetMembers(members);
        SetLoadedSource(members, designerSourceFilePath, classDisplayName);

        return true;
    }

    private bool WasJustLoadedFromSolutionExplorer(string sourceFilePath)
    {
        return !string.IsNullOrEmpty(_loadedSourceFilePath)
            && string.Equals(_loadedSourceFilePath, sourceFilePath, StringComparison.OrdinalIgnoreCase)
            && DateTime.UtcNow - _lastSolutionExplorerRefreshUtc < TimeSpan.FromSeconds(2);
    }

    private bool EditorRefreshIsSuppressedFor(string sourceFilePath)
    {
        return DateTime.UtcNow < _suppressEditorRefreshUntilUtc
            && !string.IsNullOrEmpty(_loadedSourceFilePath)
            && !string.Equals(_loadedSourceFilePath, sourceFilePath, StringComparison.OrdinalIgnoreCase);
    }

    private void SuppressEditorRefreshForFocusHandoff(bool suppress)
    {
        if (suppress)
        {
            _suppressEditorRefreshUntilUtc = DateTime.UtcNow.AddSeconds(2);
        }
    }

    private bool SolutionExplorerSelectionOwnsMembersToolWindow()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        return _solutionExplorerSelectionOwnsMembersToolWindow;
    }

    private bool SolutionExplorerSelectionOwnsMembersToolWindowFor(string sourceFilePath)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        return _solutionExplorerSelectionOwnsMembersToolWindow
            && !string.IsNullOrEmpty(_loadedSourceFilePath)
            && !string.Equals(_loadedSourceFilePath, sourceFilePath, StringComparison.OrdinalIgnoreCase);
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
        _control.SetSelectedClassName(_loadedClassName);
    }

    private (string FilePath, string? ClassName, bool FocusMembersToolWindow)? GetSelectedSolutionExplorerCodeSelection()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var shellSelection = GetSelectedSolutionExplorerFileFromShell();
        if (shellSelection != null)
        {
            return shellSelection;
        }

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
                var selectedNodeIsDesignerSourceFile = IsGeneratedDesignerSourceFile(selectedItem.Name);
                var filePath = GetProjectItemFilePath(
                    projectItem,
                    preserveDesignerSourceFile: selectedNodeIsDesignerSourceFile);

                if (filePath != null)
                {
                    var className = GetSelectedClassNameFromFile(filePath, selectedItem.Name);
                    return (filePath, className, IsDesignerBackedSolutionExplorerSelection(selectedItem.Name, filePath, selectedNodeIsDesignerSourceFile));
                }
            }
        }

        return null;
    }

    private (string FilePath, string? ClassName, bool FocusMembersToolWindow)? GetSelectedSolutionExplorerFileFromShell()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_monitorSelection == null)
        {
            return null;
        }

        IntPtr hierarchyPointer = IntPtr.Zero;
        IntPtr selectionContainerPointer = IntPtr.Zero;

        try
        {
            Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(
                _monitorSelection.GetCurrentSelection(
                    out hierarchyPointer,
                    out var itemId,
                    out var multiItemSelect,
                    out selectionContainerPointer));

            if (hierarchyPointer == IntPtr.Zero ||
                itemId == VSConstants.VSITEMID_NIL ||
                itemId == VSConstants.VSITEMID_ROOT ||
                multiItemSelect != null)
            {
                return null;
            }

            var hierarchy = Marshal.GetObjectForIUnknown(hierarchyPointer) as IVsHierarchy;
            var selectedFilePath = hierarchy == null
                ? null
                : GetHierarchyItemFilePath(hierarchy, itemId);

            if (selectedFilePath == null)
            {
                return null;
            }

            var sourceFilePath = GetSolutionExplorerSourceFilePath(selectedFilePath);
            if (sourceFilePath == null)
            {
                return null;
            }

            return (
                sourceFilePath,
                GetSelectedClassNameFromFile(sourceFilePath, Path.GetFileNameWithoutExtension(sourceFilePath)),
                IsDesignerBackedSolutionExplorerSelection(selectedFilePath, sourceFilePath, selectedNodeIsDesignerSourceFile: false));
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hierarchyPointer != IntPtr.Zero)
            {
                Marshal.Release(hierarchyPointer);
            }

            if (selectionContainerPointer != IntPtr.Zero)
            {
                Marshal.Release(selectionContainerPointer);
            }
        }
    }

    private static string? GetHierarchyItemFilePath(IVsHierarchy hierarchy, uint itemId)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (Microsoft.VisualStudio.ErrorHandler.Succeeded(hierarchy.GetCanonicalName(itemId, out var canonicalName)) &&
            File.Exists(canonicalName))
        {
            return canonicalName;
        }

        if (hierarchy is IVsProject project &&
            Microsoft.VisualStudio.ErrorHandler.Succeeded(project.GetMkDocument(itemId, out var mkDocument)) &&
            File.Exists(mkDocument))
        {
            return mkDocument;
        }

        if (Microsoft.VisualStudio.ErrorHandler.Succeeded(hierarchy.GetProperty(
                itemId,
                (int)__VSHPROPID.VSHPROPID_SaveName,
                out var saveNameObject)) &&
            saveNameObject is string saveName &&
            File.Exists(saveName))
        {
            return saveName;
        }

        if (Microsoft.VisualStudio.ErrorHandler.Succeeded(hierarchy.GetProperty(
                itemId,
                (int)__VSHPROPID.VSHPROPID_ExtObject,
                out var extObject)) &&
            extObject is ProjectItem projectItem)
        {
            return GetProjectItemFilePath(projectItem, preserveDesignerSourceFile: false);
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

    private static string? GetProjectItemFilePath(
        ProjectItem projectItem,
        bool preserveDesignerSourceFile)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var generatedFilePath = default(string);

        for (short index = 1; index <= projectItem.FileCount; index++)
        {
            var filePath = GetSolutionExplorerSourceFilePath(
                projectItem.FileNames[index],
                preserveDesignerSourceFile);

            if (filePath == null)
            {
                continue;
            }

            if (!IsGeneratedDesignerSourceFile(filePath))
            {
                return filePath;
            }

            generatedFilePath = filePath;
        }

        return generatedFilePath;
    }

    private static string? GetSolutionExplorerSourceFilePath(
        string filePath,
        bool preserveDesignerSourceFile = true)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return null;
        }

        if (filePath.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
        {
            if (preserveDesignerSourceFile)
            {
                return File.Exists(filePath) ? filePath : null;
            }

            var sourceFilePath = filePath.Substring(0, filePath.Length - ".Designer.cs".Length) + ".cs";
            return File.Exists(sourceFilePath) ? sourceFilePath : filePath;
        }

        if (filePath.EndsWith(".resx", StringComparison.OrdinalIgnoreCase))
        {
            var sourceFilePath = Path.ChangeExtension(filePath, ".cs");
            return File.Exists(sourceFilePath) ? sourceFilePath : null;
        }

        if (filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && File.Exists(filePath))
        {
            return filePath;
        }

        return null;
    }

    private void FocusMembersToolWindowForDesignerBackedSelection(bool focusMembersToolWindow)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!focusMembersToolWindow)
        {
            return;
        }

        _package.JoinableTaskFactory
            .RunAsync(async () =>
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (_package is ClassMembersNavigatorPackage package)
                {
                    await package.FocusMembersToolWindowAsync();
                }
            })
            .FileAndForget("VSIXProject1/FocusMembersToolWindowForDesignerBackedSelection");
    }

    private static bool IsDesignerBackedSolutionExplorerSelection(
        string selectedPathOrName,
        string resolvedSourceFilePath,
        bool selectedNodeIsDesignerSourceFile)
    {
        if (selectedNodeIsDesignerSourceFile)
        {
            return false;
        }

        if (selectedPathOrName.EndsWith(".resx", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (selectedPathOrName.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
        {
            return !resolvedSourceFilePath.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase);
        }

        var selectedName = Path.GetFileNameWithoutExtension(selectedPathOrName);
        var resolvedName = Path.GetFileNameWithoutExtension(resolvedSourceFilePath);

        return !string.IsNullOrEmpty(selectedName) &&
            string.Equals(selectedName, resolvedName, StringComparison.OrdinalIgnoreCase) &&
            selectedPathOrName.IndexOf('.', selectedName.Length) < 0;
    }

    private static bool IsGeneratedDesignerSourceFile(string filePath)
    {
        return filePath.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetSelectedClassNameFromFile(string filePath, string selectedNodeName)
    {
        var sourceText = File.ReadAllText(filePath);
        var selectedClassName = Path.GetFileNameWithoutExtension(selectedNodeName);

        return MemberScanner.GetClassNames(sourceText)
            .FirstOrDefault(className => string.Equals(className, selectedClassName, StringComparison.Ordinal));
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

        var doc = GetTextViewDocument(item);
        if (doc == null)
        {
            return;
        }

        var textDoc = doc.Object("TextDocument") as TextDocument;
        if (textDoc == null)
        {
            return;
        }

        SelectMemberName(textDoc, item);
    }

    private Document? GetTextViewDocument(MemberItem item)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!string.IsNullOrEmpty(item.SourceFilePath))
        {
            try
            {
                var textWindow = _dte?.ItemOperations.OpenFile(
                    item.SourceFilePath,
                    EnvDTE.Constants.vsViewKindTextView);

                textWindow?.Activate();

                if (textWindow?.Document != null)
                {
                    return textWindow.Document;
                }
            }
            catch
            {
                return _dte?.ActiveDocument;
            }
        }

        return _dte?.ActiveDocument;
    }

    private static void SelectMemberName(TextDocument textDoc, MemberItem item)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            textDoc.Selection.MoveToLineAndOffset(
                item.NameStartLine + 1,
                item.NameStartColumn + 1);

            textDoc.Selection.MoveToLineAndOffset(
                item.NameEndLine + 1,
                item.NameEndColumn + 1,
                Extend: true);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            var start = textDoc.StartPoint.CreateEditPoint();
            start.MoveToLineAndOffset(item.NameStartLine + 1, item.NameStartColumn + 1);

            var end = textDoc.StartPoint.CreateEditPoint();
            end.MoveToLineAndOffset(item.NameEndLine + 1, item.NameEndColumn + 1);

            textDoc.Selection.MoveToPoint(start);
            textDoc.Selection.MoveToPoint(end, true);
        }
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

    private Document? GetCurrentOpenCSharpTextEditorDocument()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var currentFrameDocument = GetCurrentDocumentFrameDocument();
        if (DocumentIsCSharpTextEditor(currentFrameDocument))
        {
            _currentOpenTextEditorDocument = currentFrameDocument;
            return _currentOpenTextEditorDocument;
        }

        var activeDocument = _dte?.ActiveDocument;
        if (DocumentIsCSharpTextEditor(activeDocument))
        {
            _currentOpenTextEditorDocument = activeDocument;
            return _currentOpenTextEditorDocument;
        }

        if (DocumentIsCSharpTextEditor(_currentOpenTextEditorDocument))
        {
            return _currentOpenTextEditorDocument;
        }

        _currentOpenTextEditorDocument = null;
        return null;
    }

    private Document? GetCurrentDocumentFrameDocument()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_monitorSelection == null)
        {
            return null;
        }

        try
        {
            var hr = _monitorSelection.GetCurrentElementValue(
                (uint)VSConstants.VSSELELEMID.SEID_DocumentFrame,
                out var frameObject);

            if (Microsoft.VisualStudio.ErrorHandler.Failed(hr) ||
                !(frameObject is IVsWindowFrame frame))
            {
                return null;
            }

            return GetDocumentFromFrame(frame);
        }
        catch
        {
            return null;
        }
    }

    private void RememberCurrentOpenTextEditorDocument(Document? document)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (DocumentIsCSharpTextEditor(document))
        {
            _currentOpenTextEditorDocument = document;
        }
    }

    private static bool DocumentIsCSharpTextEditor(Document? document)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (document == null || !document.FullName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            return document.Object("TextDocument") is TextDocument;
        }
        catch
        {
            return false;
        }
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
        var document = GetDocumentFromFrame(pFrame);
        RememberCurrentOpenTextEditorDocument(document);
        RefreshDocumentSoon(document, force: true);
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
                if (TryRefreshFromSolutionExplorerSelection())
                {
                    return;
                }

                if (SolutionExplorerSelectionOwnsMembersToolWindow())
                {
                    return;
                }

                var currentOpenTextEditorDocument = GetCurrentOpenCSharpTextEditorDocument();
                if (currentOpenTextEditorDocument != null)
                {
                    RefreshFromDocument(currentOpenTextEditorDocument, force: true);
                    return;
                }

                if (!force && TryRefreshFromActiveDesigner())
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

    private void RefreshDocumentSoon(Document? document, bool force = false)
    {
        _package.JoinableTaskFactory
            .RunAsync(async () =>
            {
                await System.Threading.Tasks.Task.Delay(100);
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (TryRefreshFromSolutionExplorerSelection())
                {
                    return;
                }

                if (SolutionExplorerSelectionOwnsMembersToolWindow())
                {
                    return;
                }

                var currentOpenTextEditorDocument = GetCurrentOpenCSharpTextEditorDocument();
                if (currentOpenTextEditorDocument != null)
                {
                    RefreshFromDocument(currentOpenTextEditorDocument, force: true);
                    return;
                }

                if (!force && TryRefreshFromActiveDesigner())
                {
                    return;
                }

                try
                {
                    RefreshFromDocument(document, force);
                }
                catch
                {
                    _control.SetMembers(Array.Empty<MemberItem>());
                }
            })
            .FileAndForget("VSIXProject1/RefreshDocumentSoon");
    }

    private void RefreshDocumentWindowSoon(IVsWindowFrame frame)
    {
        _package.JoinableTaskFactory
            .RunAsync(async () =>
            {
                await System.Threading.Tasks.Task.Delay(100);
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (SolutionExplorerSelectionOwnsMembersToolWindow())
                {
                    return;
                }

                if (TryRefreshFromActiveDesigner())
                {
                    return;
                }

                var document = GetDocumentFromFrame(frame) ?? _dte?.ActiveDocument;
                RefreshFromDocument(document, force: true);
            })
            .FileAndForget("VSIXProject1/RefreshDocumentWindowSoon");
    }

    private static Document? GetDocumentFromFrame(IVsWindowFrame frame)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(frame.GetProperty((int)__VSFPROPID.VSFPROPID_ExtWindowObject, out var windowObject));
            return (windowObject as Window)?.Document;
        }
        catch
        {
            return null;
        }
    }
}
