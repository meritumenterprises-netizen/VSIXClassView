using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
    }

    private void MembersToolWindowControl_Loaded(object sender, RoutedEventArgs e)
    {
        MembersList.Tag = Math.Max(1, MembersList.FontSize - 1);
    }

    public void SetMembers(IEnumerable<MemberItem> members)
    {
        var previousSourceFilePath = _allMembers.FirstOrDefault()?.SourceFilePath;
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
    }

    public void SetSelectedClassName(string? className)
    {
        var selectedClassName = string.IsNullOrEmpty(className) ? NoClassSelectedText : className!;
        if (!string.Equals(SelectedClassName, selectedClassName, StringComparison.Ordinal))
        {
            SelectedClassName = selectedClassName;
            SetFilterText(string.Empty, rememberSearchPhrase: false);
            ApplyFilter();
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

    public void SelectMemberAtOffset(string sourceFilePath, int caretOffset, bool expandGroup)
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
            MembersList.SelectedItem = null;
            return;
        }

        if (expandGroup)
        {
            ExpandGroup(selectedMember.GroupHeading);
        }

        MembersList.SelectedItem = selectedMember;
        if (expandGroup)
        {
            MembersList.ScrollIntoView(selectedMember);
        }
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
            MemberDoubleClicked?.Invoke(item);
        }
    }

    private void MemberItem_ToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is not StackPanel itemPanel)
        {
            e.Handled = true;
            return;
        }

        var textBlock = FindVisualChildren<TextBlock>(itemPanel).FirstOrDefault();
        if (textBlock == null)
        {
            e.Handled = true;
            return;
        }

        textBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        if (textBlock.DesiredSize.Width <= textBlock.ActualWidth)
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
