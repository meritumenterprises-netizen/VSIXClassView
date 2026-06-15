using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;

namespace VSIXProject1
{
    public sealed class MemberItem
    {
        private string? _tooltipText;

        public MemberItem() { }
        public required string Name { get; init; }
        public required string DeclaringClassName { get; init; }
        public required string DisplayText { get; init; }
        public string TooltipText
        {
            get => string.IsNullOrEmpty(_tooltipText) ? DisplayText : _tooltipText!;
            init => _tooltipText = value;
        }
        public IReadOnlyList<MemberDisplayPart> DisplayParts { get; init; } = Array.Empty<MemberDisplayPart>();
        public required MemberKind Kind { get; init; }
        public required int StartOffset { get; init; }
        public required int NameStartOffset { get; init; }
        public required int NameLength { get; init; }
        public required int NameStartLine { get; init; }
        public required int NameStartColumn { get; init; }
        public required int NameEndLine { get; init; }
        public required int NameEndColumn { get; init; }
        public string? SourceFilePath { get; init; }
        public ImageMoniker IconMoniker => Kind switch
        {
            MemberKind.Region => KnownMonikers.Namespace,
            MemberKind.Const => KnownMonikers.Constant,
            MemberKind.Field => KnownMonikers.Field,
            MemberKind.EnumMember => KnownMonikers.EnumerationItemPublic,
            MemberKind.Property => KnownMonikers.Property,
            MemberKind.Method => KnownMonikers.Method,
            _ => KnownMonikers.Member
        };

        public string GroupHeading => Kind switch
        {
            MemberKind.Region => "Regions",
            MemberKind.Const => "Const",
            MemberKind.Field => "Fields",
            MemberKind.EnumMember => "Members",
            MemberKind.Property => "Properties",
            MemberKind.Method => "Methods",
            _ => Kind.ToString()
        };
    }

    public sealed class MemberDisplayPart
    {
        public MemberDisplayPart(
            string text,
            bool isBold = false,
            bool isMemberName = false,
            bool isParameterName = false)
        {
            Text = text;
            IsBold = isBold;
            IsMemberName = isMemberName;
            IsParameterName = isParameterName;
        }

        public string Text { get; }
        public bool IsBold { get; }
        public bool IsMemberName { get; }
        public bool IsParameterName { get; }
    }
}
