using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using VSIXProject1;

public partial class MembersToolWindowControl : UserControl, INotifyPropertyChanged
{
    private const string NoClassSelectedText = "(not selected)";

    private readonly List<MemberItem> _allMembers = new();
    private readonly Dictionary<string, bool> _groupExpandedStates = new(StringComparer.Ordinal);
    private string _selectedClassName = NoClassSelectedText;
    private string _lastSearchPhrase = string.Empty;
    private bool _ignoreFilterTextChange;
    private Brush _memberNameBrush = Brushes.Blue;
    private MemberItem? _manualScrollSuppressedMember;

    public ObservableCollection<MemberItem> Members { get; } = new();
    public string SelectedClassName
    {
        get => _selectedClassName;
        private set
        {
            if (_selectedClassName == value)
            {
                return;
            }

            _selectedClassName = value;
            OnPropertyChanged();
        }
    }

    public event Action<MemberItem>? MemberDoubleClicked;
    public event PropertyChangedEventHandler? PropertyChanged;

    public MembersToolWindowControl()
    {
        InitializeComponent();
        DataContext = this;
        Loaded += MembersToolWindowControl_Loaded;
        PreviewKeyDown += MembersToolWindowControl_PreviewKeyDown;
        MembersList.PreviewMouseWheel += MembersList_PreviewManualScroll;
        MembersList.PreviewMouseDown += MembersList_PreviewMouseDown;
    }

    private void MembersToolWindowControl_Loaded(object sender, RoutedEventArgs e)
    {
        MembersList.Tag = Math.Max(1, MembersList.FontSize - 1);
    }

    public void SetMembers(IEnumerable<MemberItem> members)
    {
        var previousSourceFilePath = _allMembers.FirstOrDefault()?.SourceFilePath;
        var previouslySelectedMember = MembersList.SelectedItem as MemberItem;
        _allMembers.Clear();
        _allMembers.AddRange(members);
        var selectedClassName = _allMembers.FirstOrDefault()?.DeclaringClassName ?? NoClassSelectedText;
        var selectedSourceFilePath = _allMembers.FirstOrDefault()?.SourceFilePath;
        var selectedClassChanged = !string.Equals(SelectedClassName, selectedClassName, StringComparison.Ordinal);
        var selectedSourceChanged = !string.Equals(previousSourceFilePath, selectedSourceFilePath, StringComparison.OrdinalIgnoreCase);

        SelectedClassName = selectedClassName;
        if (selectedClassChanged || selectedSourceChanged)
        {
            SetFilterText(string.Empty, rememberSearchPhrase: false);
        }

        ApplyFilter();
        RestoreSelectedMember(previouslySelectedMember);
    }

    public void SetSelectedClassName(string? className)
    {
        var selectedClassName = string.IsNullOrEmpty(className) ? NoClassSelectedText : className!;
        if (!string.Equals(SelectedClassName, selectedClassName, StringComparison.Ordinal))
        {
            SelectedClassName = selectedClassName;
            SetFilterText(string.Empty, rememberSearchPhrase: false);
            ApplyFilter();
            ClearSelectedMember();
        }
    }

    private void MembersToolWindowControl_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F &&
            Keyboard.Modifiers.HasFlag(ModifierKeys.Control) &&
            Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            SetFilterText(_lastSearchPhrase, rememberSearchPhrase: false);
            MembersFilterTextBox.Focus();
            MembersFilterTextBox.SelectAll();
            e.Handled = true;
        }
    }

    public void SetMemberNameBrush(Brush? brush)
    {
        if (brush == null)
        {
            return;
        }

        _memberNameBrush = brush;
        RefreshVisibleMemberText();
    }

    public MemberItem? SelectMemberAtOffset(string sourceFilePath, int caretOffset, bool expandGroup)
    {
        var orderedMembers = _allMembers
            .Where(member => string.Equals(member.SourceFilePath, sourceFilePath, StringComparison.OrdinalIgnoreCase))
            .OrderBy(member => member.StartOffset)
            .ToList();
        var selectedMember = default(MemberItem);

        for (var index = 0; index < orderedMembers.Count; index++)
        {
            var member = orderedMembers[index];
            var nextMemberStartOffset = index + 1 < orderedMembers.Count
                ? orderedMembers[index + 1].StartOffset
                : int.MaxValue;

            if (member.StartOffset <= caretOffset && caretOffset < nextMemberStartOffset)
            {
                selectedMember = member;
                break;
            }
        }

        if (selectedMember == null || !Members.Contains(selectedMember))
        {
            ClearSelectedMember();
            return null;
        }

        if (expandGroup)
        {
            ExpandGroup(selectedMember.GroupHeading);
        }

        var selectedItemChanged = !ReferenceEquals(MembersList.SelectedItem, selectedMember);
        if (selectedItemChanged)
        {
            _manualScrollSuppressedMember = null;
        }

        MembersList.SelectedItem = selectedMember;
        if (expandGroup &&
            (selectedItemChanged ||
                (!ReferenceEquals(_manualScrollSuppressedMember, selectedMember) && !IsItemVisible(selectedMember))))
        {
            MembersList.ScrollIntoView(selectedMember);
        }

        return selectedMember;
    }

    public void ClearMemberSelection()
    {
        ClearSelectedMember();
    }

    public bool HasMemberListFocus()
    {
        return IsKeyboardFocusWithin || MembersList.IsKeyboardFocusWithin;
    }

    private void ExpandGroup(string groupHeading)
    {
        _groupExpandedStates[groupHeading] = true;
        MembersList.UpdateLayout();

        foreach (var groupItem in FindVisualChildren<GroupItem>(MembersList))
        {
            if (groupItem.DataContext is CollectionViewGroup group &&
                string.Equals(group.Name?.ToString(), groupHeading, StringComparison.Ordinal))
            {
                var expander = FindVisualChild<Expander>(groupItem);
                if (expander != null)
                {
                    expander.IsExpanded = true;
                    MembersList.UpdateLayout();
                }

                return;
            }
        }
    }

    private void GroupExpander_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Expander expander || expander.DataContext is not CollectionViewGroup group)
        {
            return;
        }

        var groupHeading = group.Name?.ToString();
        if (string.IsNullOrEmpty(groupHeading))
        {
            return;
        }

        expander.IsExpanded = !_groupExpandedStates.TryGetValue(groupHeading!, out var isExpanded) || isExpanded;
    }

    private void GroupExpander_Expanded(object sender, RoutedEventArgs e)
    {
        SetGroupExpandedState(sender, isExpanded: true);
    }

    private void GroupExpander_Collapsed(object sender, RoutedEventArgs e)
    {
        SetGroupExpandedState(sender, isExpanded: false);
    }

    private void SetGroupExpandedState(object sender, bool isExpanded)
    {
        if (sender is Expander { DataContext: CollectionViewGroup group })
        {
            var groupHeading = group.Name?.ToString();
            if (!string.IsNullOrEmpty(groupHeading))
            {
                _groupExpandedStates[groupHeading!] = isExpanded;
            }
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
        where T : DependencyObject
    {
        if (!IsVisual(parent))
        {
            yield break;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T typedChild)
            {
                yield return typedChild;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        return FindVisualChildren<T>(parent).FirstOrDefault();
    }

    private static T? FindVisualParent<T>(DependencyObject child)
        where T : DependencyObject
    {
        if (!IsVisual(child))
        {
            return null;
        }

        var parent = VisualTreeHelper.GetParent(child);
        while (parent != null)
        {
            if (parent is T typedParent)
            {
                return typedParent;
            }

            parent = VisualTreeHelper.GetParent(parent);
        }

        return null;
    }

    private static bool IsVisual(DependencyObject dependencyObject)
    {
        return dependencyObject is Visual || dependencyObject is System.Windows.Media.Media3D.Visual3D;
    }

    private void MembersFilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ignoreFilterTextChange)
        {
            _lastSearchPhrase = MembersFilterTextBox.Text;
        }

        ApplyFilter();
    }

    private void SetFilterText(string text, bool rememberSearchPhrase)
    {
        if (rememberSearchPhrase)
        {
            MembersFilterTextBox.Text = text;
            return;
        }

        _ignoreFilterTextChange = true;
        try
        {
            MembersFilterTextBox.Text = text;
        }
        finally
        {
            _ignoreFilterTextChange = false;
        }
    }

    private void ApplyFilter()
    {
        var filter = MembersFilterTextBox.Text;
        var filteredMembers = string.IsNullOrWhiteSpace(filter)
            ? _allMembers
            : _allMembers.Where(member =>
                member.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                member.DisplayText.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);

        Members.Clear();

        foreach (var member in filteredMembers.OrderBy(member => member.Kind))
        {
            Members.Add(member);
        }
    }

    private void MembersList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (MembersList.SelectedItem is MemberItem item)
        {
            SelectAndRevealMember(item);
            MemberDoubleClicked?.Invoke(item);
            SelectAndRevealMember(item);
        }
    }

    private void SelectAndRevealMember(MemberItem item)
    {
        MembersList.SelectedItem = item;
        MembersList.ScrollIntoView(item);
    }

    private void RestoreSelectedMember(MemberItem? previousMember)
    {
        if (previousMember == null)
        {
            ClearSelectedMember();
            return;
        }

        var matchingMember = Members.FirstOrDefault(member => MemberMatches(member, previousMember));
        if (matchingMember == null)
        {
            ClearSelectedMember();
            return;
        }

        MembersList.SelectedItem = matchingMember;
    }

    private static bool MemberMatches(MemberItem member, MemberItem other)
    {
        return member.Kind == other.Kind &&
            member.StartOffset == other.StartOffset &&
            member.NameStartLine == other.NameStartLine &&
            member.NameStartColumn == other.NameStartColumn &&
            string.Equals(member.Name, other.Name, StringComparison.Ordinal) &&
            string.Equals(member.SourceFilePath, other.SourceFilePath, StringComparison.OrdinalIgnoreCase);
    }

    private void ClearSelectedMember()
    {
        MembersList.SelectedItem = null;
        _manualScrollSuppressedMember = null;
    }

    private void MembersList_PreviewManualScroll(object sender, MouseWheelEventArgs e)
    {
        SuppressAutoScrollForCurrentSelection();
    }

    private void MembersList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            FindVisualParent<ScrollBar>(source) != null)
        {
            SuppressAutoScrollForCurrentSelection();
        }
    }

    private void SuppressAutoScrollForCurrentSelection()
    {
        if (MembersList.SelectedItem is MemberItem item)
        {
            _manualScrollSuppressedMember = item;
        }
    }

    private bool IsItemVisible(MemberItem item)
    {
        if (MembersList.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement itemContainer)
        {
            return false;
        }

        var itemBounds = itemContainer
            .TransformToAncestor(MembersList)
            .TransformBounds(new Rect(new Point(0, 0), itemContainer.RenderSize));
        var listBounds = new Rect(new Point(0, 0), MembersList.RenderSize);

        return listBounds.Contains(itemBounds.TopLeft) && listBounds.Contains(itemBounds.BottomRight);
    }

    private void MemberItem_ToolTipOpening(object sender, ToolTipEventArgs e)
    {
        var itemContainer = sender as ListBoxItem;
        var itemPanel = itemContainer == null
            ? sender as StackPanel
            : FindVisualChild<StackPanel>(itemContainer);

        if (itemPanel == null)
        {
            e.Handled = true;
            return;
        }

        var scrollViewer = FindVisualChild<ScrollViewer>(MembersList);
        var availableWidth = scrollViewer?.ViewportWidth > 0
            ? scrollViewer.ViewportWidth
            : MembersList.ActualWidth;

        if (itemContainer != null)
        {
            availableWidth -= itemContainer.Padding.Left + itemContainer.Padding.Right;
        }

        if (availableWidth <= 0)
        {
            e.Handled = true;
            return;
        }

        itemPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        if (itemPanel.DesiredSize.Width <= availableWidth)
        {
            e.Handled = true;
        }
    }

    private void MemberTextBlock_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBlock textBlock || textBlock.DataContext is not MemberItem item)
        {
            return;
        }

        textBlock.Inlines.Clear();
        var parts = item.DisplayParts.Count == 0
            ? new[] { new MemberDisplayPart(item.DisplayText) }
            : item.DisplayParts;

        foreach (var part in parts)
        {
            textBlock.Inlines.Add(new Run(part.Text)
            {
                FontWeight = part.IsBold ? FontWeights.Bold : FontWeights.Normal,
                Foreground = part.IsParameterName
                    ? Brushes.Blue
                    : part.IsMemberName ? _memberNameBrush : textBlock.Foreground
            });
        }
    }

    private void RefreshVisibleMemberText()
    {
        foreach (var textBlock in FindVisualChildren<TextBlock>(MembersList))
        {
            if (textBlock.DataContext is MemberItem)
            {
                MemberTextBlock_Loaded(textBlock, new RoutedEventArgs());
            }
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
