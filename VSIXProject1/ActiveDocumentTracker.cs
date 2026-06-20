using EnvDTE;
using EnvDTE80;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text.Classification;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text;
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
    private DateTime _suppressCaretSelectionUntilUtc = DateTime.MinValue;
    private bool _solutionExplorerSelectionOwnsMembersToolWindow;
    private bool _allowCaretSelection;
    private readonly Dictionary<string, string?> _decompiledTypeFilePathCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Type?> _externalTypeCache = new(StringComparer.Ordinal);
    private readonly object _typeResolutionCacheLock = new();

    public ActiveDocumentTracker(
        AsyncPackage package,
        MembersToolWindowControl control)
    {
        _package = package;
        _control = control;
        _control.MemberDoubleClicked += NavigateToMember;
        _control.TypeClicked += NavigateToType;
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

        _suppressCaretSelectionUntilUtc = DateTime.UtcNow.AddSeconds(3);

        _selectionEvents = _dte.Events.SelectionEvents;
        _selectionEvents.OnChange += SelectionChanged;
        _documentEvents = _dte.Events.DocumentEvents;
        _documentEvents.DocumentSaved += DocumentSaved;
        _windowEvents = _dte.Events.WindowEvents;
        _windowEvents.WindowActivated += WindowActivated;

        _monitorSelection = await _package.GetServiceAsync(typeof(SVsShellMonitorSelection)) as IVsMonitorSelection;
        _runningDocumentTable = await _package.GetServiceAsync(typeof(SVsRunningDocumentTable)) as IVsRunningDocumentTable;
        await ConfigureMemberNameBrushAsync();
        _runningDocumentTable?.AdviseRunningDocTableEvents(this, out _runningDocumentTableEventsCookie);
        _currentOpenTextEditorTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _currentOpenTextEditorTimer.Tick += CurrentOpenTextEditorTimerTick;
        _currentOpenTextEditorTimer.Start();

        Refresh(selectFromCaret: false);
    }

    private async Task ConfigureMemberNameBrushAsync()
    {
        await _package.JoinableTaskFactory.SwitchToMainThreadAsync();

        try
        {
            var componentModel = await _package.GetServiceAsync(typeof(SComponentModel)) as IComponentModel;
            var classificationRegistry = componentModel?.GetService<IClassificationTypeRegistryService>();
            var formatMapService = componentModel?.GetService<IClassificationFormatMapService>();
            var classNameClassification = classificationRegistry?.GetClassificationType("class name");

            if (formatMapService == null || classNameClassification == null)
            {
                return;
            }

            var formatMap = formatMapService.GetClassificationFormatMap("text");
            _control.SetMemberNameBrush(formatMap.GetTextProperties(classNameClassification).ForegroundBrush);
        }
        catch
        {
            // Keep the fallback color if Visual Studio classification colors are unavailable.
        }
    }

    private void CurrentOpenTextEditorTimerTick(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            if (MembersToolWindowHasFocus())
            {
                return;
            }

            if (ActiveWindowIsSolutionExplorer())
            {
                return;
            }

            if (TryRefreshFromActiveDesigner())
            {
                return;
            }

            if (ResetIfActiveDocumentIsUnsupported())
            {
                return;
            }

            var currentOpenTextEditorDocument = GetCurrentOpenCSharpTextEditorDocument();
            if (currentOpenTextEditorDocument != null)
            {
                EnableCaretSelectionForActiveTextEditor();
                RefreshFromDocument(currentOpenTextEditorDocument, selectFromCaret: _allowCaretSelection);
            }
        }
        catch
        {
            ClearMembersUnlessDesignerIsLoading();
        }
    }

    public void RefreshActiveDocument()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            if (ActiveWindowIsSolutionExplorer())
            {
                return;
            }

            if (TryRefreshFromActiveDesigner())
            {
                return;
            }

            if (ResetIfActiveDocumentIsUnsupported())
            {
                return;
            }

            var currentOpenTextEditorDocument = GetCurrentOpenCSharpTextEditorDocument();
            if (currentOpenTextEditorDocument != null)
            {
                EnableCaretSelectionForActiveTextEditor();
                RefreshFromDocument(currentOpenTextEditorDocument, force: true, selectFromCaret: _allowCaretSelection);
                return;
            }

            RefreshFromDocument(_dte?.ActiveDocument, selectFromCaret: false);
        }
        catch
        {
            ClearMembersUnlessDesignerIsLoading();
        }
    }

    public void Reset()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        _currentOpenTextEditorDocument = null;
        _suppressEditorRefreshUntilUtc = DateTime.MinValue;
        _suppressCaretSelectionUntilUtc = DateTime.MinValue;
        _solutionExplorerSelectionOwnsMembersToolWindow = false;
        _allowCaretSelection = false;
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
                RefreshFromDocument(document, force: true, selectFromCaret: _allowCaretSelection);
            }
        }
        catch
        {
            ClearMembersUnlessDesignerIsLoading();
        }
    }

    private void WindowActivated(Window gotFocus, Window lostFocus)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (MembersToolWindowHasFocus())
        {
            return;
        }

        var focusedDocument = GetCurrentDocumentFrameDocument() ?? gotFocus.Document;
        if (ResetIfActiveDocumentIsUnsupported())
        {
            return;
        }

        if (gotFocus.Type == vsWindowType.vsWindowTypeDocument &&
            DocumentIsCSharpTextEditor(focusedDocument))
        {
            _solutionExplorerSelectionOwnsMembersToolWindow = false;
            if (!CaretSelectionIsSuppressed())
            {
                _allowCaretSelection = true;
            }
        }

        RememberCurrentOpenTextEditorDocument(focusedDocument);
        RefreshDocumentSoon(focusedDocument, force: true, selectFromCaret: _allowCaretSelection);
    }

    private void SelectionChanged()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (MembersToolWindowHasFocus())
        {
            return;
        }

        if (ActiveWindowIsCSharpTextEditor())
        {
            if (CaretSelectionIsSuppressed())
            {
                Refresh(selectFromCaret: false);
                return;
            }

            _allowCaretSelection = true;
            Refresh(selectFromCaret: true);
            return;
        }

        if (ResetIfActiveDocumentIsUnsupported())
        {
            return;
        }

        Refresh(selectFromCaret: false);
    }

    private void Refresh()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        Refresh(selectFromCaret: _allowCaretSelection);
    }

    private void Refresh(bool selectFromCaret)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            if (MembersToolWindowHasFocus())
            {
                return;
            }

            if (ActiveWindowIsSolutionExplorer())
            {
                return;
            }

            if (TryRefreshFromActiveDesigner())
            {
                return;
            }

            if (ResetIfActiveDocumentIsUnsupported())
            {
                return;
            }

            var currentOpenTextEditorDocument = GetCurrentOpenCSharpTextEditorDocument();
            if (currentOpenTextEditorDocument != null)
            {
                RefreshFromDocument(currentOpenTextEditorDocument, selectFromCaret: selectFromCaret);
                return;
            }

            RefreshFromDocument(_dte?.ActiveDocument, selectFromCaret: selectFromCaret);
        }
        catch
        {
            ClearMembersUnlessDesignerIsLoading();
        }
    }

    private void RefreshFromDocument(Document? doc, bool force = false, bool selectFromCaret = false)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (doc == null || !doc.FullName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            if (ActiveWindowIsDesigner())
            {
                return;
            }

            ClearMembersUnlessDesignerIsLoading();
            return;
        }

        var textDoc = doc.Object("TextDocument") as TextDocument;
        if (textDoc == null)
        {
            return;
        }

        RememberCurrentOpenTextEditorDocument(doc);

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
        var selectMemberFromCaret = selectFromCaret && ActiveWindowIsCSharpTextEditor();

        var classNameAtCaret = MemberScanner.GetClassDisplayNameAtCaret(text, caretOffset);
        if (!force && IsCurrentlyLoaded(doc.FullName, classNameAtCaret))
        {
            if (selectMemberFromCaret)
            {
                _control.SelectMemberAtOffset(doc.FullName, caretOffset, expandGroup: true);
            }
            else
            {
                _control.ClearMemberSelection();
            }

            return;
        }

        var members = MemberScanner.GetMembersForClassAtCaret(text, caretOffset, doc.FullName);

        _solutionExplorerSelectionOwnsMembersToolWindow = false;
        _control.SetMembers(members);
        SetLoadedSource(members, doc.FullName, classNameAtCaret);
        if (selectMemberFromCaret)
        {
            _control.SelectMemberAtOffset(doc.FullName, caretOffset, expandGroup: true);
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

        var filePath = _dte?.ActiveWindow?.Document?.FullName ?? _dte?.ActiveDocument?.FullName;
        if (string.IsNullOrEmpty(filePath))
        {
            return true;
        }

        var designerSourceFilePath = filePath!;
        if (!designerSourceFilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(designerSourceFilePath))
        {
            return true;
        }

        var sourceText = File.ReadAllText(designerSourceFilePath);
        var preferredClassName = Path.GetFileNameWithoutExtension(designerSourceFilePath);
        var classDisplayName = MemberScanner.GetClassDisplayNameForClassNameOrFirst(sourceText, preferredClassName);
        _allowCaretSelection = false;
        if (IsCurrentlyLoaded(designerSourceFilePath, classDisplayName))
        {
            _control.ClearMemberSelection();
            return true;
        }

        var members = MemberScanner.GetMembersForClassNameOrFirst(sourceText, preferredClassName, designerSourceFilePath);
        _control.SetMembers(members);
        SetLoadedSource(members, designerSourceFilePath, classDisplayName);
        _control.ClearMemberSelection();

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

    private bool CaretSelectionIsSuppressed()
    {
        return DateTime.UtcNow < _suppressCaretSelectionUntilUtc;
    }

    private void EnableCaretSelectionForActiveTextEditor()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!CaretSelectionIsSuppressed() && ActiveWindowIsCSharpTextEditor())
        {
            _allowCaretSelection = true;
        }
    }

    private bool MembersToolWindowHasFocus()
    {
        return _control.HasMemberListFocus();
    }

    private void ClearMembersUnlessDesignerIsLoading()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (ShouldPreserveMembersForDesignerLoad())
        {
            _control.ClearMemberSelection();
            return;
        }

        _control.SetMembers(Array.Empty<MemberItem>());
        ClearLoadedSource();
    }

    private bool ShouldPreserveMembersForDesignerLoad()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        return !string.IsNullOrEmpty(_loadedSourceFilePath) && ActiveWindowLooksLikeDesignerOrLoading();
    }

    private bool ActiveWindowLooksLikeDesignerOrLoading()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var activeWindow = _dte?.ActiveWindow;
        return activeWindow?.Type == vsWindowType.vsWindowTypeDocument &&
            WindowLooksLikeDesigner(activeWindow.Kind, activeWindow.Caption);
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

    private bool IsSamePartialClassDifferentSourceFile(string sourceFilePath, string? className)
    {
        return !string.IsNullOrEmpty(_loadedSourceFilePath)
            && !string.IsNullOrEmpty(_loadedClassName)
            && !string.Equals(_loadedSourceFilePath, sourceFilePath, StringComparison.OrdinalIgnoreCase)
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

        _allowCaretSelection = true;

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

        if (item.Kind == MemberKind.Region)
        {
            MoveCaretToMember(textDoc, item);
        }
        else
        {
            SelectMemberName(textDoc, item);
        }
    }

    private void NavigateToType(string typeName)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var shortTypeName = GetClickableShortTypeName(typeName);
        if (string.IsNullOrEmpty(shortTypeName))
        {
            return;
        }

        var definition = FindTypeDefinition(shortTypeName);
        if (definition == null)
        {
            OpenDecompiledFrameworkType(shortTypeName);
            return;
        }

        try
        {
            var textWindow = _dte?.ItemOperations.OpenFile(
                definition.Value.FilePath,
                EnvDTE.Constants.vsViewKindTextView);
            textWindow?.Activate();

            var textDoc = textWindow?.Document?.Object("TextDocument") as TextDocument;
            textDoc?.Selection.MoveToLineAndOffset(
                definition.Value.Line + 1,
                definition.Value.Column + 1);
        }
        catch
        {
        }
    }

    private (string FilePath, int Line, int Column)? FindTypeDefinition(string typeName)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        foreach (var filePath in GetSolutionCSharpFiles().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var sourceText = File.ReadAllText(filePath);
                var tree = CSharpSyntaxTree.ParseText(sourceText);
                var parsedText = tree.GetText();
                var typeDeclaration = tree.GetCompilationUnitRoot()
                    .DescendantNodes()
                    .OfType<BaseTypeDeclarationSyntax>()
                    .FirstOrDefault(type => string.Equals(type.Identifier.ValueText, typeName, StringComparison.Ordinal));

                if (typeDeclaration == null)
                {
                    continue;
                }

                var span = parsedText.Lines.GetLinePositionSpan(typeDeclaration.Identifier.Span);
                return (filePath, span.Start.Line, span.Start.Character);
            }
            catch
            {
            }
        }

        return null;
    }

    private void OpenDecompiledFrameworkType(string typeName)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        _package.JoinableTaskFactory
            .RunAsync(async () =>
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                _control.SetTypeResolutionInProgress(true);

                try
                {
                    var referenceAssemblyPaths = GetSolutionReferenceAssemblyPaths()
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    var filePath = await System.Threading.Tasks.Task.Run(
                        () => GetOrCreateDecompiledTypeFilePath(typeName, referenceAssemblyPaths));

                    await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (filePath == null)
                    {
                        return;
                    }

                    var textWindow = _dte?.ItemOperations.OpenFile(
                        filePath,
                        EnvDTE.Constants.vsViewKindTextView);
                    textWindow?.Activate();
                }
                catch
                {
                }
                finally
                {
                    await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                    _control.SetTypeResolutionInProgress(false);
                }
            })
            .FileAndForget("VSIXProject1/OpenDecompiledFrameworkType");
    }

    private string? GetOrCreateDecompiledTypeFilePath(
        string typeName,
        IReadOnlyList<string> referenceAssemblyPaths)
    {
        var mappedTypeName = GetMappedExternalTypeName(typeName);
        lock (_typeResolutionCacheLock)
        {
            if (_decompiledTypeFilePathCache.TryGetValue(mappedTypeName, out var cachedFilePath))
            {
                return cachedFilePath;
            }
        }

        var frameworkType = ResolveExternalType(typeName, referenceAssemblyPaths);
        var cacheKey = frameworkType?.FullName ?? mappedTypeName;
        lock (_typeResolutionCacheLock)
        {
            if (!string.Equals(cacheKey, mappedTypeName, StringComparison.Ordinal) &&
                _decompiledTypeFilePathCache.TryGetValue(cacheKey, out var cachedFilePath))
            {
                _decompiledTypeFilePathCache[mappedTypeName] = cachedFilePath;
                return cachedFilePath;
            }
        }

        var filePath = frameworkType == null
            ? WriteKnownExternalTypeStub(typeName)
            : WriteFrameworkTypeStub(frameworkType);

        lock (_typeResolutionCacheLock)
        {
            _decompiledTypeFilePathCache[cacheKey] = filePath;
            _decompiledTypeFilePathCache[mappedTypeName] = filePath;
        }

        return filePath;
    }

    private Type? ResolveExternalType(
        string typeName,
        IReadOnlyList<string> referenceAssemblyPaths)
    {
        var mappedTypeName = GetMappedExternalTypeName(typeName);
        lock (_typeResolutionCacheLock)
        {
            if (_externalTypeCache.TryGetValue(mappedTypeName, out var cachedType))
            {
                return cachedType;
            }
        }

        var resolvedType = ResolveTypeFromLoadedAssemblies(mappedTypeName, typeName) ??
            ResolveTypeFromReferencedAssemblies(mappedTypeName, typeName, referenceAssemblyPaths);
        lock (_typeResolutionCacheLock)
        {
            _externalTypeCache[resolvedType?.FullName ?? mappedTypeName] = resolvedType;
            _externalTypeCache[mappedTypeName] = resolvedType;
        }

        return resolvedType;
    }

    private static string GetMappedExternalTypeName(string typeName)
    {
        return typeName switch
        {
            "bool" => "System.Boolean",
            "byte" => "System.Byte",
            "char" => "System.Char",
            "decimal" => "System.Decimal",
            "double" => "System.Double",
            "float" => "System.Single",
            "int" => "System.Int32",
            "long" => "System.Int64",
            "object" => "System.Object",
            "sbyte" => "System.SByte",
            "short" => "System.Int16",
            "string" => "System.String",
            "uint" => "System.UInt32",
            "ulong" => "System.UInt64",
            "ushort" => "System.UInt16",
            "Task" => "System.Threading.Tasks.Task",
            "IActionResult" => "Microsoft.AspNetCore.Mvc.IActionResult",
            "List" => "System.Collections.Generic.List`1",
            "Dictionary" => "System.Collections.Generic.Dictionary`2",
            "IEnumerable" => "System.Collections.Generic.IEnumerable`1",
            "IList" => "System.Collections.Generic.IList`1",
            "ICollection" => "System.Collections.Generic.ICollection`1",
            _ => typeName
        };
    }

    private static Type? ResolveTypeFromLoadedAssemblies(string mappedTypeName, string shortTypeName)
    {
        return Type.GetType(mappedTypeName) ??
            AppDomain.CurrentDomain
                .GetAssemblies()
                .Select(assembly => FindTypeInAssembly(assembly, mappedTypeName, shortTypeName))
                .FirstOrDefault(type => type != null);
    }

    private static Type? ResolveTypeFromReferencedAssemblies(
        string mappedTypeName,
        string shortTypeName,
        IEnumerable<string> referenceAssemblyPaths)
    {
        foreach (var assemblyPath in referenceAssemblyPaths)
        {
            try
            {
                var assembly = Assembly.LoadFrom(assemblyPath);
                var type = FindTypeInAssembly(assembly, mappedTypeName, shortTypeName);
                if (type != null)
                {
                    return type;
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static Type? FindTypeInAssembly(Assembly assembly, string mappedTypeName, string shortTypeName)
    {
        try
        {
            return assembly.GetType(mappedTypeName) ??
                assembly.GetExportedTypes().FirstOrDefault(type => TypeNameMatches(type, shortTypeName));
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types
                .Where(type => type != null)
                .FirstOrDefault(type => TypeNameMatches(type!, shortTypeName));
        }
        catch
        {
            return null;
        }
    }

    private static bool TypeNameMatches(Type type, string shortTypeName)
    {
        var typeName = type.Name;
        var tickIndex = typeName.IndexOf('`');
        if (tickIndex >= 0)
        {
            typeName = typeName.Substring(0, tickIndex);
        }

        return string.Equals(typeName, shortTypeName, StringComparison.Ordinal) ||
            string.Equals(type.FullName, shortTypeName, StringComparison.Ordinal);
    }

    private static string? WriteKnownExternalTypeStub(string typeName)
    {
        var source = typeName switch
        {
            "IActionResult" => """
                namespace Microsoft.AspNetCore.Mvc
                {
                    public interface IActionResult
                    {
                        System.Threading.Tasks.Task ExecuteResultAsync(ActionContext context);
                    }
                }
                """,
            _ => null
        };

        if (source == null)
        {
            return null;
        }

        var directory = Path.Combine(Path.GetTempPath(), "VSIXClassView", "DecompiledTypes");
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(directory, $"{typeName}.cs");
        File.WriteAllText(filePath, source, Encoding.UTF8);

        return filePath;
    }

    private static string WriteFrameworkTypeStub(Type type)
    {
        var directory = Path.Combine(Path.GetTempPath(), "VSIXClassView", "DecompiledTypes");
        Directory.CreateDirectory(directory);

        var fileName = string.Concat(type.FullName!
            .Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        var filePath = Path.Combine(directory, $"{fileName}.cs");
        File.WriteAllText(filePath, BuildFrameworkTypeStub(type), Encoding.UTF8);

        return filePath;
    }

    private static string BuildFrameworkTypeStub(Type type)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrEmpty(type.Namespace))
        {
            builder.AppendLine($"namespace {type.Namespace}");
            builder.AppendLine("{");
        }

        var indent = string.IsNullOrEmpty(type.Namespace) ? string.Empty : "    ";
        builder.Append(indent);
        builder.Append(GetTypeDeclaration(type));
        builder.AppendLine();
        builder.Append(indent);
        builder.AppendLine("{");

        if (type.IsEnum)
        {
            foreach (var name in Enum.GetNames(type))
            {
                builder.Append(indent);
                builder.Append("    ");
                builder.AppendLine($"{name},");
            }
        }
        else
        {
            AppendFrameworkFields(builder, type, indent);
            AppendFrameworkConstructors(builder, type, indent);
            AppendFrameworkProperties(builder, type, indent);
            AppendFrameworkMethods(builder, type, indent);
        }

        builder.Append(indent);
        builder.AppendLine("}");
        if (!string.IsNullOrEmpty(type.Namespace))
        {
            builder.AppendLine("}");
        }

        return builder.ToString();
    }

    private static string GetTypeDeclaration(Type type)
    {
        var keyword = type.IsInterface
            ? "interface"
            : type.IsEnum ? "enum" : type.IsValueType ? "struct" : "class";
        return $"public {keyword} {GetTypeDisplayName(type)}";
    }

    private static void AppendFrameworkFields(StringBuilder builder, Type type, string indent)
    {
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .OrderBy(field => field.Name, StringComparer.Ordinal))
        {
            builder.Append(indent);
            builder.Append("    ");
            builder.Append(field.IsStatic ? "static " : string.Empty);
            builder.AppendLine($"{GetTypeDisplayName(field.FieldType)} {field.Name};");
        }
    }

    private static void AppendFrameworkConstructors(StringBuilder builder, Type type, string indent)
    {
        foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            builder.Append(indent);
            builder.Append("    ");
            builder.AppendLine($"public {GetTypeDisplayName(type)}({GetParameterList(constructor.GetParameters())}) {{ }}");
        }
    }

    private static void AppendFrameworkProperties(StringBuilder builder, Type type, string indent)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .OrderBy(property => property.Name, StringComparer.Ordinal))
        {
            builder.Append(indent);
            builder.Append("    ");
            builder.AppendLine($"public {GetTypeDisplayName(property.PropertyType)} {property.Name} {{ get; set; }}");
        }
    }

    private static void AppendFrameworkMethods(StringBuilder builder, Type type, string indent)
    {
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .OrderBy(method => method.Name, StringComparer.Ordinal))
        {
            builder.Append(indent);
            builder.Append("    ");
            builder.Append(method.IsStatic ? "static " : string.Empty);
            builder.AppendLine($"{GetTypeDisplayName(method.ReturnType)} {method.Name}({GetParameterList(method.GetParameters())}) {{ }}");
        }
    }

    private static string GetParameterList(ParameterInfo[] parameters)
    {
        return string.Join(", ", parameters.Select(parameter => $"{GetTypeDisplayName(parameter.ParameterType)} {parameter.Name}"));
    }

    private static string GetTypeDisplayName(Type type)
    {
        if (type == typeof(void))
        {
            return "void";
        }

        if (type.IsGenericParameter)
        {
            return type.Name;
        }

        if (type.IsArray)
        {
            return $"{GetTypeDisplayName(type.GetElementType()!)}[]";
        }

        var alias = type.FullName switch
        {
            "System.Boolean" => "bool",
            "System.Byte" => "byte",
            "System.Char" => "char",
            "System.Decimal" => "decimal",
            "System.Double" => "double",
            "System.Int16" => "short",
            "System.Int32" => "int",
            "System.Int64" => "long",
            "System.Object" => "object",
            "System.Single" => "float",
            "System.String" => "string",
            _ => null
        };

        if (alias != null)
        {
            return alias;
        }

        if (!type.IsGenericType)
        {
            return type.Name;
        }

        var genericName = type.Name;
        var tickIndex = genericName.IndexOf('`');
        if (tickIndex >= 0)
        {
            genericName = genericName.Substring(0, tickIndex);
        }

        return $"{genericName}<{string.Join(", ", type.GetGenericArguments().Select(GetTypeDisplayName))}>";
    }

    private IEnumerable<string> GetSolutionReferenceAssemblyPaths()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var projects = _dte?.Solution?.Projects;
        if (projects == null)
        {
            yield break;
        }

        foreach (Project project in projects)
        {
            foreach (var assemblyPath in GetProjectReferenceAssemblyPaths(project))
            {
                yield return assemblyPath;
            }
        }
    }

    private static IEnumerable<string> GetProjectReferenceAssemblyPaths(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        foreach (var assemblyPath in GetProjectReferencePaths(project))
        {
            yield return assemblyPath;
        }

        ProjectItems? projectItems = null;
        try
        {
            projectItems = project.ProjectItems;
        }
        catch
        {
        }

        if (projectItems == null)
        {
            yield break;
        }

        foreach (ProjectItem projectItem in projectItems)
        {
            Project? subProject = null;
            try
            {
                subProject = projectItem.SubProject;
            }
            catch
            {
            }

            if (subProject == null)
            {
                continue;
            }

            foreach (var assemblyPath in GetProjectReferenceAssemblyPaths(subProject))
            {
                yield return assemblyPath;
            }
        }
    }

    private static IEnumerable<string> GetProjectReferencePaths(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        object? references = null;
        try
        {
            references = project.Object
                ?.GetType()
                .InvokeMember("References", BindingFlags.GetProperty, null, project.Object, null);
        }
        catch
        {
        }

        if (references == null)
        {
            yield break;
        }

        System.Collections.IEnumerable? enumerableReferences = references as System.Collections.IEnumerable;
        if (enumerableReferences == null)
        {
            yield break;
        }

        foreach (var reference in enumerableReferences)
        {
            string? path = null;
            try
            {
                path = reference
                    .GetType()
                    .InvokeMember("Path", BindingFlags.GetProperty, null, reference, null) as string;
            }
            catch
            {
            }

            if (!string.IsNullOrEmpty(path) &&
                path!.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(path))
            {
                yield return path;
            }
        }
    }

    private IEnumerable<string> GetSolutionCSharpFiles()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var projects = _dte?.Solution?.Projects;
        if (projects == null)
        {
            yield break;
        }

        foreach (Project project in projects)
        {
            foreach (var filePath in GetProjectCSharpFiles(project))
            {
                yield return filePath;
            }
        }
    }

    private static IEnumerable<string> GetProjectCSharpFiles(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        ProjectItems? projectItems;
        try
        {
            projectItems = project.ProjectItems;
        }
        catch
        {
            yield break;
        }

        if (projectItems == null)
        {
            yield break;
        }

        foreach (var filePath in GetProjectItemCSharpFiles(projectItems))
        {
            yield return filePath;
        }
    }

    private static IEnumerable<string> GetProjectItemCSharpFiles(ProjectItems projectItems)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        foreach (ProjectItem projectItem in projectItems)
        {
            short fileCount;
            try
            {
                fileCount = projectItem.FileCount;
            }
            catch
            {
                fileCount = 0;
            }

            for (short index = 1; index <= fileCount; index++)
            {
                string? filePath;
                try
                {
                    filePath = projectItem.FileNames[index];
                }
                catch
                {
                    filePath = null;
                }

                if (!string.IsNullOrEmpty(filePath) &&
                    filePath!.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(filePath))
                {
                    yield return filePath;
                }
            }

            ProjectItems? childItems = null;
            try
            {
                childItems = projectItem.ProjectItems;
            }
            catch
            {
            }

            if (childItems != null)
            {
                foreach (var filePath in GetProjectItemCSharpFiles(childItems))
                {
                    yield return filePath;
                }
            }

            Project? subProject = null;
            try
            {
                subProject = projectItem.SubProject;
            }
            catch
            {
            }

            if (subProject != null)
            {
                foreach (var filePath in GetProjectCSharpFiles(subProject))
                {
                    yield return filePath;
                }
            }
        }
    }

    private static string GetClickableShortTypeName(string typeName)
    {
        var trimmedTypeName = typeName.Trim();
        var stopIndex = trimmedTypeName.IndexOfAny(new[] { '<', '?', '[', '(', ',', ' ' });
        return stopIndex < 0
            ? trimmedTypeName
            : trimmedTypeName.Substring(0, stopIndex);
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

    private static void MoveCaretToMember(TextDocument textDoc, MemberItem item)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            textDoc.Selection.MoveToLineAndOffset(
                item.NameStartLine + 1,
                item.NameStartColumn + 1);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            var point = textDoc.StartPoint.CreateEditPoint();
            point.MoveToLineAndOffset(item.NameStartLine + 1, item.NameStartColumn + 1);
            textDoc.Selection.MoveToPoint(point);
        }
    }

    private static void RevealMemberName(TextDocument textDoc, MemberItem item)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            var point = textDoc.StartPoint.CreateEditPoint();
            point.MoveToLineAndOffset(item.NameStartLine + 1, item.NameStartColumn + 1);
            point.TryToShow(vsPaneShowHow.vsPaneShowCentered);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
        }
    }

    private bool ActiveWindowIsDesigner()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        return ActiveWindowLooksLikeDesignerOrLoading();
    }

    private static bool WindowLooksLikeDesigner(string? kind, string? caption)
    {
        var windowKind = kind ?? string.Empty;
        var windowCaption = caption ?? string.Empty;

        return windowKind.IndexOf("Designer", StringComparison.OrdinalIgnoreCase) >= 0 ||
            windowCaption.IndexOf("Designer", StringComparison.OrdinalIgnoreCase) >= 0 ||
            windowCaption.IndexOf("Loading Designer", StringComparison.OrdinalIgnoreCase) >= 0 ||
            (windowCaption.IndexOf("Loading", StringComparison.OrdinalIgnoreCase) >= 0 &&
                windowCaption.IndexOf("Design", StringComparison.OrdinalIgnoreCase) >= 0) ||
            windowCaption.EndsWith(" [Design]", StringComparison.OrdinalIgnoreCase) ||
            windowCaption.EndsWith(" (Design)", StringComparison.OrdinalIgnoreCase);
    }

    private bool ActiveWindowIsSolutionExplorer()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        return _dte?.ActiveWindow?.Type == vsWindowType.vsWindowTypeSolutionExplorer;
    }

    private bool ActiveWindowIsCSharpTextEditor()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var activeWindow = _dte?.ActiveWindow;
        return activeWindow?.Type == vsWindowType.vsWindowTypeDocument &&
            DocumentIsCSharpTextEditor(activeWindow.Document);
    }

    private bool ResetIfActiveDocumentIsUnsupported()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!ActiveWindowIsUnsupportedDocument())
        {
            return false;
        }

        _currentOpenTextEditorDocument = null;
        _allowCaretSelection = false;
        ClearLoadedSource();
        _control.SetMembers(Array.Empty<MemberItem>());
        return true;
    }

    private bool ActiveWindowIsUnsupportedDocument()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var activeWindow = _dte?.ActiveWindow;
        if (activeWindow?.Type != vsWindowType.vsWindowTypeDocument ||
            ActiveWindowIsDesigner())
        {
            return false;
        }

        var document = GetCurrentDocumentFrameDocument() ?? activeWindow.Document ?? _dte?.ActiveDocument;
        return document != null && !DocumentIsCSharpTextEditor(document);
    }

    private Document? GetCurrentOpenCSharpTextEditorDocument()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (ActiveWindowIsUnsupportedDocument())
        {
            _currentOpenTextEditorDocument = null;
            return null;
        }

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
        RefreshActiveDocumentSoon(force: true, selectFromCaret: _allowCaretSelection);
        return VSConstants.S_OK;
    }

    public int OnBeforeDocumentWindowShow(uint docCookie, int fFirstShow, IVsWindowFrame pFrame)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var document = GetDocumentFromFrame(pFrame);
        RememberCurrentOpenTextEditorDocument(document);
        if (IsDesignerFrame(pFrame))
        {
            RefreshDocumentWindowSoon(pFrame);
        }
        else
        {
            RefreshDocumentSoon(document, force: true, selectFromCaret: false);
        }

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

    private void RefreshActiveDocumentSoon(bool force = false, bool selectFromCaret = false)
    {
        _package.JoinableTaskFactory
            .RunAsync(async () =>
            {
                await System.Threading.Tasks.Task.Delay(100);
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (MembersToolWindowHasFocus())
                {
                    return;
                }

                if (TryRefreshFromActiveDesigner())
                {
                    return;
                }

                if (ResetIfActiveDocumentIsUnsupported())
                {
                    return;
                }

                var currentOpenTextEditorDocument = GetCurrentOpenCSharpTextEditorDocument();
                if (currentOpenTextEditorDocument != null)
                {
                    RefreshFromDocument(currentOpenTextEditorDocument, force: true, selectFromCaret: selectFromCaret);
                    return;
                }

                if (force)
                {
                    try
                    {
                        RefreshFromDocument(_dte?.ActiveDocument, force: true, selectFromCaret: selectFromCaret);
                    }
                    catch
                    {
                        ClearMembersUnlessDesignerIsLoading();
                    }
                }
                else
                {
                    RefreshActiveDocument();
                }
            })
            .FileAndForget("VSIXProject1/RefreshActiveDocumentSoon");
    }

    private void RefreshDocumentSoon(Document? document, bool force = false, bool selectFromCaret = false)
    {
        _package.JoinableTaskFactory
            .RunAsync(async () =>
            {
                await System.Threading.Tasks.Task.Delay(100);
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (MembersToolWindowHasFocus())
                {
                    return;
                }

                if (TryRefreshFromActiveDesigner())
                {
                    return;
                }

                if (ResetIfActiveDocumentIsUnsupported())
                {
                    return;
                }

                var currentOpenTextEditorDocument = GetCurrentOpenCSharpTextEditorDocument();
                if (currentOpenTextEditorDocument != null)
                {
                    RefreshFromDocument(currentOpenTextEditorDocument, force: true, selectFromCaret: selectFromCaret);
                    return;
                }

                try
                {
                    RefreshFromDocument(document, force, selectFromCaret);
                }
                catch
                {
                    ClearMembersUnlessDesignerIsLoading();
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

                if (MembersToolWindowHasFocus())
                {
                    return;
                }

                if (TryRefreshFromActiveDesigner())
                {
                    return;
                }

                if (ResetIfActiveDocumentIsUnsupported())
                {
                    return;
                }

                var document = GetDocumentFromFrame(frame) ?? _dte?.ActiveDocument;
                RefreshFromDocument(document, force: true, selectFromCaret: false);
            })
            .FileAndForget("VSIXProject1/RefreshDocumentWindowSoon");
    }

    private static bool IsDesignerFrame(IVsWindowFrame frame)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            if (Microsoft.VisualStudio.ErrorHandler.Succeeded(
                    frame.GetProperty((int)__VSFPROPID.VSFPROPID_Caption, out var captionObject)) &&
                captionObject is string caption &&
                WindowLooksLikeDesigner(kind: null, caption))
            {
                return true;
            }
        }
        catch
        {
        }

        return false;
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
