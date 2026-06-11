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
            var currentType = token.Parent?
                .AncestorsAndSelf()
                .OfType<BaseTypeDeclarationSyntax>()
                .Where(IsSupportedType)
                .FirstOrDefault();

            if (currentType == null)
                return Array.Empty<MemberItem>();

            return GetMembersForType(currentType, parsedText, sourceFilePath);
        }

        public static string? GetClassNameAtCaret(string sourceText, int caretOffset)
        {
            var tree = CSharpSyntaxTree.ParseText(sourceText);
            var root = tree.GetCompilationUnitRoot();
            var token = root.FindToken(Math.Max(0, Math.Min(caretOffset, sourceText.Length)));

            return token.Parent?
                .AncestorsAndSelf()
                .OfType<BaseTypeDeclarationSyntax>()
                .Where(IsSupportedType)
                .FirstOrDefault()
                ?.Identifier.ValueText;
        }

        public static string? GetClassDisplayNameAtCaret(string sourceText, int caretOffset)
        {
            var tree = CSharpSyntaxTree.ParseText(sourceText);
            var root = tree.GetCompilationUnitRoot();
            var token = root.FindToken(Math.Max(0, Math.Min(caretOffset, sourceText.Length)));
            var currentType = token.Parent?
                .AncestorsAndSelf()
                .OfType<BaseTypeDeclarationSyntax>()
                .Where(IsSupportedType)
                .FirstOrDefault();

            return currentType == null ? null : GetTypeDisplayName(currentType);
        }

        public static IReadOnlyList<MemberItem> GetMembersForClassNameOrFirst(
            string sourceText,
            string? preferredClassName,
            string? sourceFilePath = null)
        {
            var tree = CSharpSyntaxTree.ParseText(sourceText);
            var parsedText = tree.GetText();
            var root = tree.GetCompilationUnitRoot();

            var types = GetSupportedTypeDeclarations(root);
            var currentType = types.FirstOrDefault(t =>
                    string.Equals(t.Identifier.ValueText, preferredClassName, StringComparison.Ordinal))
                ?? types.FirstOrDefault();

            return currentType == null
                ? Array.Empty<MemberItem>()
                : GetMembersForType(currentType, parsedText, sourceFilePath);
        }

        public static IReadOnlyList<string> GetClassNames(string sourceText)
        {
            var tree = CSharpSyntaxTree.ParseText(sourceText);
            var root = tree.GetCompilationUnitRoot();

            return root.DescendantNodes()
                .OfType<BaseTypeDeclarationSyntax>()
                .Where(IsSupportedType)
                .Select(typeDeclaration => typeDeclaration.Identifier.ValueText)
                .ToList();
        }

        public static string? GetClassDisplayNameForClassNameOrFirst(
            string sourceText,
            string? preferredClassName)
        {
            var tree = CSharpSyntaxTree.ParseText(sourceText);
            var root = tree.GetCompilationUnitRoot();

            var types = GetSupportedTypeDeclarations(root);
            var currentType = types.FirstOrDefault(t =>
                    string.Equals(t.Identifier.ValueText, preferredClassName, StringComparison.Ordinal))
                ?? types.FirstOrDefault();

            return currentType == null ? null : GetTypeDisplayName(currentType);
        }

        public static bool ClassInheritsWindowsFormOrUserControl(
            string sourceText,
            string? preferredClassName)
        {
            var tree = CSharpSyntaxTree.ParseText(sourceText);
            var root = tree.GetCompilationUnitRoot();

            var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>();
            var currentClass = classes.FirstOrDefault(c =>
                    string.Equals(c.Identifier.ValueText, preferredClassName, StringComparison.Ordinal))
                ?? classes.FirstOrDefault();

            return currentClass?.BaseList?.Types.Any(baseType =>
            {
                var shortTypeName = GetShortTypeName(baseType.Type);
                return string.Equals(shortTypeName, "Form", StringComparison.Ordinal) ||
                    string.Equals(shortTypeName, "UserControl", StringComparison.Ordinal);
            }) == true;
        }

        private static IReadOnlyList<MemberItem> GetMembersForType(
            BaseTypeDeclarationSyntax currentType,
            SourceText parsedText,
            string? sourceFilePath)
        {
            if (currentType is EnumDeclarationSyntax currentEnum)
            {
                return GetMembersForEnum(currentEnum, parsedText, sourceFilePath);
            }

            if (!(currentType is TypeDeclarationSyntax currentTypeDeclaration))
            {
                return Array.Empty<MemberItem>();
            }

            var classDisplayName = GetTypeDisplayName(currentType);

            var fields = currentTypeDeclaration.Members
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

            var properties = currentTypeDeclaration.Members
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

            var methods = currentTypeDeclaration.Members
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

            var constructors = currentTypeDeclaration.Members
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

            var primaryConstructor = currentTypeDeclaration is TypeDeclarationSyntax primaryConstructorType &&
                primaryConstructorType.ParameterList != null
                ? new[]
                {
                    CreatePrimaryConstructorMemberItem(primaryConstructorType, parsedText, sourceFilePath)
                }
                : Enumerable.Empty<MemberItem>();

            return fields
                .Concat(properties)
                .Concat(primaryConstructor.Concat(constructors).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                .Concat(methods)
                .ToList();
        }

        private static IReadOnlyList<MemberItem> GetMembersForEnum(
            EnumDeclarationSyntax currentEnum,
            SourceText parsedText,
            string? sourceFilePath)
        {
            var classDisplayName = GetTypeDisplayName(currentEnum);

            return currentEnum.Members
                .Select(member =>
                {
                    var span = parsedText.Lines.GetLinePositionSpan(member.Identifier.Span);
                    return new MemberItem
                    {
                        Name = member.Identifier.ValueText,
                        DeclaringClassName = classDisplayName,
                        DisplayText = member.EqualsValue == null
                            ? member.Identifier.ValueText
                            : $"{member.Identifier.ValueText} = {member.EqualsValue.Value}",
                        Kind = MemberKind.EnumMember,
                        StartOffset = member.SpanStart,
                        NameStartOffset = member.Identifier.SpanStart,
                        NameLength = member.Identifier.Span.Length,
                        NameStartLine = span.Start.Line,
                        NameStartColumn = span.Start.Character,
                        NameEndLine = span.End.Line,
                        NameEndColumn = span.End.Character,
                        SourceFilePath = sourceFilePath
                    };
                })
                .ToList();
        }

        private static MemberItem CreatePrimaryConstructorMemberItem(
            TypeDeclarationSyntax currentClass,
            SourceText parsedText,
            string? sourceFilePath)
        {
            var span = parsedText.Lines.GetLinePositionSpan(currentClass.Identifier.Span);
            var classDisplayName = GetTypeDisplayName(currentClass);
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

        private static IEnumerable<BaseTypeDeclarationSyntax> GetSupportedTypeDeclarations(CompilationUnitSyntax root)
        {
            return root.DescendantNodes()
                .OfType<BaseTypeDeclarationSyntax>()
                .Where(IsSupportedType);
        }

        private static bool IsSupportedType(BaseTypeDeclarationSyntax typeDeclaration)
        {
            return typeDeclaration is ClassDeclarationSyntax ||
                typeDeclaration is StructDeclarationSyntax ||
                typeDeclaration is RecordDeclarationSyntax ||
                typeDeclaration is InterfaceDeclarationSyntax ||
                typeDeclaration is EnumDeclarationSyntax;
        }

        private static string GetTypeDisplayName(BaseTypeDeclarationSyntax typeDeclaration)
        {
            if (typeDeclaration is EnumDeclarationSyntax)
            {
                return $"enum {typeDeclaration.Identifier.ValueText}";
            }

            var typedDeclaration = (TypeDeclarationSyntax)typeDeclaration;
            var typeName = typedDeclaration.TypeParameterList == null
                ? typeDeclaration.Identifier.ValueText
                : $"{typeDeclaration.Identifier.ValueText}{typedDeclaration.TypeParameterList}";

            if (typeDeclaration is RecordDeclarationSyntax recordDeclaration)
            {
                return recordDeclaration.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword)
                    ? $"record struct {typeName}"
                    : $"record {typeName}";
            }

            if (typeDeclaration is StructDeclarationSyntax)
            {
                return $"struct {typeName}";
            }

            if (typeDeclaration is InterfaceDeclarationSyntax)
            {
                return $"interface {typeName}";
            }

            if (typeDeclaration.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.StaticKeyword)))
            {
                return $"static {typeName}";
            }

            if (typeDeclaration.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.AbstractKeyword)))
            {
                return $"abstract {typeName}";
            }

            return typeName;
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
