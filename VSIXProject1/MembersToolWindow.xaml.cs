using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using VSIXProject1;

public partial class MembersToolWindowControl : UserControl
{
    public ObservableCollection<MemberItem> Members { get; } = new();

    public event Action<MemberItem>? MemberDoubleClicked;

    public MembersToolWindowControl()
    {
        InitializeComponent();
        MembersList.ItemsSource = Members;
    }

    public void SetMembers(IEnumerable<MemberItem> members)
    {
        Members.Clear();

        foreach (var member in members)
            Members.Add(member);
    }

    private void MembersList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (MembersList.SelectedItem is MemberItem item)
            MemberDoubleClicked?.Invoke(item);
    }
}
