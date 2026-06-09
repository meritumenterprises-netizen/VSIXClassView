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
    using Microsoft.CodeAnalysis.Text;

    public static class MemberScanner
    {
        public static IReadOnlyList<MemberItem> GetMembersForClassAtCaret(
            string sourceText,
            int caretOffset,
            string? sourceFilePath = null)
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

            return GetMembersForClass(currentClass, parsedText, sourceFilePath);
        }

        public static string? GetClassNameAtCaret(string sourceText, int caretOffset)
        {
            var tree = CSharpSyntaxTree.ParseText(sourceText);
            var root = tree.GetCompilationUnitRoot();
            var token = root.FindToken(Math.Max(0, Math.Min(caretOffset, sourceText.Length)));

            return token.Parent?
                .AncestorsAndSelf()
                .OfType<ClassDeclarationSyntax>()
                .FirstOrDefault()
                ?.Identifier.ValueText;
        }

        public static string? GetClassDisplayNameAtCaret(string sourceText, int caretOffset)
        {
            var tree = CSharpSyntaxTree.ParseText(sourceText);
            var root = tree.GetCompilationUnitRoot();
            var token = root.FindToken(Math.Max(0, Math.Min(caretOffset, sourceText.Length)));
            var currentClass = token.Parent?
                .AncestorsAndSelf()
                .OfType<ClassDeclarationSyntax>()
                .FirstOrDefault();

            return currentClass == null ? null : GetClassDisplayName(currentClass);
        }

        public static IReadOnlyList<MemberItem> GetMembersForClassNameOrFirst(
            string sourceText,
            string? preferredClassName,
            string? sourceFilePath = null)
        {
            var tree = CSharpSyntaxTree.ParseText(sourceText);
            var parsedText = tree.GetText();
            var root = tree.GetCompilationUnitRoot();

            var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();
            var currentClass = classes.FirstOrDefault(c =>
                    string.Equals(c.Identifier.ValueText, preferredClassName, StringComparison.Ordinal))
                ?? classes.FirstOrDefault();

            return currentClass == null
                ? Array.Empty<MemberItem>()
                : GetMembersForClass(currentClass, parsedText, sourceFilePath);
        }

        public static IReadOnlyList<string> GetClassNames(string sourceText)
        {
            var tree = CSharpSyntaxTree.ParseText(sourceText);
            var root = tree.GetCompilationUnitRoot();

            return root.DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .Select(classDeclaration => classDeclaration.Identifier.ValueText)
                .ToList();
        }

        public static string? GetClassDisplayNameForClassNameOrFirst(
            string sourceText,
            string? preferredClassName)
        {
            var tree = CSharpSyntaxTree.ParseText(sourceText);
            var root = tree.GetCompilationUnitRoot();

            var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();
            var currentClass = classes.FirstOrDefault(c =>
                    string.Equals(c.Identifier.ValueText, preferredClassName, StringComparison.Ordinal))
                ?? classes.FirstOrDefault();

            return currentClass == null ? null : GetClassDisplayName(currentClass);
        }

        private static IReadOnlyList<MemberItem> GetMembersForClass(
            ClassDeclarationSyntax currentClass,
            SourceText parsedText,
            string? sourceFilePath)
        {
            var classDisplayName = GetClassDisplayName(currentClass);

            var fields = currentClass.Members
                .OfType<FieldDeclarationSyntax>()
                .SelectMany(f => f.Declaration.Variables.Select(v =>
                {
                    var span = parsedText.Lines.GetLinePositionSpan(v.Identifier.Span);
                    return new MemberItem
                    {
                        Name = v.Identifier.ValueText,
                        DeclaringClassName = classDisplayName,
                        DisplayText = FieldIsConst(f)
                            ? $"{v.Identifier.ValueText} : {GetShortTypeName(f.Declaration.Type)} = {v.Initializer?.Value}"
                            : $"{v.Identifier.ValueText} : {GetShortTypeName(f.Declaration.Type)}",
                        Kind = FieldIsConst(f) ? MemberKind.Const : MemberKind.Field,
                        StartOffset = f.SpanStart,
                        NameStartOffset = v.Identifier.SpanStart,
                        NameLength = v.Identifier.Span.Length,
                        NameStartLine = span.Start.Line,
                        NameStartColumn = span.Start.Character,
                        NameEndLine = span.End.Line,
                        NameEndColumn = span.End.Character,
                        SourceFilePath = sourceFilePath
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
                        DeclaringClassName = classDisplayName,
                        DisplayText = $"{p.Identifier.ValueText} : {GetShortTypeName(p.Type)} {GetPropertyAccessorDisplayText(p)}".TrimEnd(),
                        Kind = MemberKind.Property,
                        StartOffset = p.SpanStart,
                        NameStartOffset = p.Identifier.SpanStart,
                        NameLength = p.Identifier.Span.Length,
                        NameStartLine = span.Start.Line,
                        NameStartColumn = span.Start.Character,
                        NameEndLine = span.End.Line,
                        NameEndColumn = span.End.Character,
                        SourceFilePath = sourceFilePath
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
                        DeclaringClassName = classDisplayName,
                        DisplayText = $"{GetShortTypeName(m.ReturnType)} : {GetMethodDisplayName(m)} ({GetParameterDisplayText(m.ParameterList)})",
                        Kind = MemberKind.Method,
                        StartOffset = m.SpanStart,
                        NameStartOffset = m.Identifier.SpanStart,
                        NameLength = m.Identifier.Span.Length,
                        NameStartLine = span.Start.Line,
                        NameStartColumn = span.Start.Character,
                        NameEndLine = span.End.Line,
                        NameEndColumn = span.End.Character,
                        SourceFilePath = sourceFilePath
                    };
                })
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase);

            var constructors = currentClass.Members
                .OfType<ConstructorDeclarationSyntax>()
                .Select(c =>
                {
                    var span = parsedText.Lines.GetLinePositionSpan(c.Identifier.Span);
                    return new MemberItem
                    {
                        Name = c.Identifier.ValueText,
                        DeclaringClassName = classDisplayName,
                        DisplayText = $"ctor {c.Identifier.ValueText} ({GetParameterDisplayText(c.ParameterList)})",
                        Kind = MemberKind.Method,
                        StartOffset = c.SpanStart,
                        NameStartOffset = c.Identifier.SpanStart,
                        NameLength = c.Identifier.Span.Length,
                        NameStartLine = span.Start.Line,
                        NameStartColumn = span.Start.Character,
                        NameEndLine = span.End.Line,
                        NameEndColumn = span.End.Character,
                        SourceFilePath = sourceFilePath
                    };
                })
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase);

            var primaryConstructor = currentClass.ParameterList == null
                ? Enumerable.Empty<MemberItem>()
                : new[]
                {
                    CreatePrimaryConstructorMemberItem(currentClass, parsedText, sourceFilePath)
                };

            return fields
                .Concat(properties)
                .Concat(primaryConstructor.Concat(constructors).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                .Concat(methods)
                .ToList();
        }

        private static MemberItem CreatePrimaryConstructorMemberItem(
            ClassDeclarationSyntax currentClass,
            SourceText parsedText,
            string? sourceFilePath)
        {
            var span = parsedText.Lines.GetLinePositionSpan(currentClass.Identifier.Span);
            var classDisplayName = GetClassDisplayName(currentClass);
            return new MemberItem
            {
                Name = currentClass.Identifier.ValueText,
                DeclaringClassName = classDisplayName,
                DisplayText = $"ctor {currentClass.Identifier.ValueText} ({GetParameterDisplayText(currentClass.ParameterList!)})",
                Kind = MemberKind.Method,
                StartOffset = currentClass.SpanStart,
                NameStartOffset = currentClass.Identifier.SpanStart,
                NameLength = currentClass.Identifier.Span.Length,
                NameStartLine = span.Start.Line,
                NameStartColumn = span.Start.Character,
                NameEndLine = span.End.Line,
                NameEndColumn = span.End.Character,
                SourceFilePath = sourceFilePath
            };
        }

        private static string GetClassDisplayName(ClassDeclarationSyntax classDeclaration)
        {
            if (classDeclaration.TypeParameterList == null)
            {
                return classDeclaration.Identifier.ValueText;
            }

            return $"{classDeclaration.Identifier.ValueText}{classDeclaration.TypeParameterList}";
        }

        private static string GetParameterDisplayText(ParameterListSyntax parameterList)
        {
            return string.Join(
                ", ",
                parameterList.Parameters.Select(parameter =>
                    $"{parameter.Identifier.ValueText}: {GetShortTypeName(parameter.Type)}"));
        }

        private static string GetMethodDisplayName(MethodDeclarationSyntax method)
        {
            if (method.TypeParameterList == null)
            {
                return method.Identifier.ValueText;
            }

            return $"{method.Identifier.ValueText}{method.TypeParameterList}";
        }

        private static bool FieldIsConst(FieldDeclarationSyntax field)
        {
            return field.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.ConstKeyword));
        }

        private static string GetPropertyAccessorDisplayText(PropertyDeclarationSyntax property)
        {
            var accessors = property.AccessorList?.Accessors;
            if (accessors == null || accessors.Value.Count == 0)
            {
                return string.Empty;
            }

            var hasGet = accessors.Value.Any(accessor => accessor.IsKind(SyntaxKind.GetAccessorDeclaration));
            var hasSet = accessors.Value.Any(accessor => accessor.IsKind(SyntaxKind.SetAccessorDeclaration));
            var hasInit = accessors.Value.Any(accessor => accessor.IsKind(SyntaxKind.InitAccessorDeclaration));

            if (hasGet && hasSet)
            {
                return "get; set;";
            }

            if (hasGet && hasInit)
            {
                return "get; init;";
            }

            if (hasGet)
            {
                return "get;";
            }

            if (hasSet)
            {
                return "set;";
            }

            return hasInit ? "init;" : string.Empty;
        }

        private static string GetShortTypeName(TypeSyntax? type)
        {
            if (type == null)
            {
                return "object";
            }

            switch (type)
            {
                case PredefinedTypeSyntax predefinedType:
                    return predefinedType.Keyword.ValueText;

                case IdentifierNameSyntax identifierName:
                    return identifierName.Identifier.ValueText;

                case GenericNameSyntax genericName:
                    return $"{genericName.Identifier.ValueText}<{string.Join(", ", genericName.TypeArgumentList.Arguments.Select(GetShortTypeName))}>";

                case QualifiedNameSyntax qualifiedName:
                    return GetShortTypeName(qualifiedName.Right);

                case AliasQualifiedNameSyntax aliasQualifiedName:
                    return GetShortTypeName(aliasQualifiedName.Name);

                case NullableTypeSyntax nullableType:
                    return $"{GetShortTypeName(nullableType.ElementType)}?";

                case ArrayTypeSyntax arrayType:
                    return $"{GetShortTypeName(arrayType.ElementType)}{string.Concat(arrayType.RankSpecifiers.Select(rank => "[" + new string(',', rank.Rank - 1) + "]"))}";

                case TupleTypeSyntax tupleType:
                    return $"({string.Join(", ", tupleType.Elements.Select(element => string.IsNullOrEmpty(element.Identifier.ValueText)
                        ? GetShortTypeName(element.Type)
                        : $"{element.Identifier.ValueText}: {GetShortTypeName(element.Type)}"))})";

                default:
                    return type.ToString();
            }
        }
    }
}
