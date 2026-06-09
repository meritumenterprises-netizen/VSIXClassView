using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using VSIXProject1;

public partial class MembersToolWindowControl : UserControl, INotifyPropertyChanged
{
    private const string NoClassSelectedText = "(not selected)";

    private readonly List<MemberItem> _allMembers = new();
    private string _selectedClassName = NoClassSelectedText;

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
    }

    public void SetMembers(IEnumerable<MemberItem> members)
    {
        _allMembers.Clear();
        _allMembers.AddRange(members);
        SelectedClassName = _allMembers.FirstOrDefault()?.DeclaringClassName ?? NoClassSelectedText;
        ApplyFilter();
    }

    private void MembersFilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
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

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
