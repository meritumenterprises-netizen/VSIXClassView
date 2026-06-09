using Microsoft.VisualStudio.Shell;
using System.Runtime.InteropServices;

[Guid("9C48F37A-53A2-4E3A-8B69-1A6D67B63F11")]
public sealed class MembersToolWindow : ToolWindowPane
{
    public MembersToolWindowControl Control { get; }

    public MembersToolWindow() : base(null)
    {
        Caption = "Class Members";

        Control = new MembersToolWindowControl();
        Content = Control;
    }
}