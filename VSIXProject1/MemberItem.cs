using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace VSIXProject1
{
    public sealed class MemberItem
    {
        public MemberItem() { }
        public required string Name { get; init; }
        public required MemberKind Kind { get; init; }
        public required int StartOffset { get; init; }
        public required int NameStartOffset { get; init; }
        public required int NameLength { get; init; }
        public required int NameStartLine { get; init; }
        public required int NameStartColumn { get; init; }
        public required int NameEndLine { get; init; }
        public required int NameEndColumn { get; init; }

        public string DisplayText => $"{Kind}: {Name}";
    }
}
