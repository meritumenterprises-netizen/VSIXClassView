using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;

[Guid("9C48F37A-53A2-4E3A-8B69-1A6D67B63F11")]
public sealed class MembersToolWindow : ToolWindowPane
{
    public MembersToolWindowControl Control { get; }
    public event EventHandler? VisibilityChanged;

    public MembersToolWindow() : base(null)
    {
        Caption = "Class Members";

        Control = new MembersToolWindowControl();
        Control.IsVisibleChanged += (sender, args) => VisibilityChanged?.Invoke(this, EventArgs.Empty);
        Content = Control;
    }
}
