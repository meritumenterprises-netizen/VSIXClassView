using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace VSIXProject1
{
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;

    public static class MemberScanner
    {
        public static IReadOnlyList<MemberItem> GetMembersForClassAtCaret(
            string sourceText,
            int caretOffset)
        {
            var tree = CSharpSyntaxTree.ParseText(sourceText);
            var parsedText = tree.GetText();
            var root = tree.GetCompilationUnitRoot();

            var token = root.FindToken(Math.Max(0, Math.Min(caretOffset, sourceText.Length)));
            var currentClass = token.Parent?
                .AncestorsAndSelf()
                .OfType<ClassDeclarationSyntax>()
                .FirstOrDefault();

            if (currentClass == null)
                return Array.Empty<MemberItem>();

            var fields = currentClass.Members
                .OfType<FieldDeclarationSyntax>()
                .SelectMany(f => f.Declaration.Variables.Select(v =>
                {
                    var span = parsedText.Lines.GetLinePositionSpan(v.Identifier.Span);
                    return new MemberItem
                    {
                        Name = v.Identifier.ValueText,
                        Kind = MemberKind.Field,
                        StartOffset = f.SpanStart,
                        NameStartOffset = v.Identifier.SpanStart,
                        NameLength = v.Identifier.Span.Length,
                        NameStartLine = span.Start.Line,
                        NameStartColumn = span.Start.Character,
                        NameEndLine = span.End.Line,
                        NameEndColumn = span.End.Character
                    };
                }))
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase);

            var properties = currentClass.Members
                .OfType<PropertyDeclarationSyntax>()
                .Select(p =>
                {
                    var span = parsedText.Lines.GetLinePositionSpan(p.Identifier.Span);
                    return new MemberItem
                    {
                        Name = p.Identifier.ValueText,
                        Kind = MemberKind.Property,
                        StartOffset = p.SpanStart,
                        NameStartOffset = p.Identifier.SpanStart,
                        NameLength = p.Identifier.Span.Length,
                        NameStartLine = span.Start.Line,
                        NameStartColumn = span.Start.Character,
                        NameEndLine = span.End.Line,
                        NameEndColumn = span.End.Character
                    };
                })
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase);

            var methods = currentClass.Members
                .OfType<MethodDeclarationSyntax>()
                .Where(m => !m.Identifier.ValueText.StartsWith("get_", StringComparison.Ordinal))
                .Select(m =>
                {
                    var span = parsedText.Lines.GetLinePositionSpan(m.Identifier.Span);
                    return new MemberItem
                    {
                        Name = m.Identifier.ValueText,
                        Kind = MemberKind.Method,
                        StartOffset = m.SpanStart,
                        NameStartOffset = m.Identifier.SpanStart,
                        NameLength = m.Identifier.Span.Length,
                        NameStartLine = span.Start.Line,
                        NameStartColumn = span.Start.Character,
                        NameEndLine = span.End.Line,
                        NameEndColumn = span.End.Character
                    };
                })
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase);

            return fields.Concat(properties).Concat(methods).ToList();
        }
    }
}
