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
            var regions = GetRegions(currentType, parsedText, sourceFilePath);

            if (currentType is EnumDeclarationSyntax currentEnum)
            {
                return regions
                    .Concat(GetMembersForEnum(currentEnum, parsedText, sourceFilePath))
                    .ToList();
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
                    var fieldType = GetShortTypeName(f.Declaration.Type);
                    var initializerText = v.Initializer == null
                        ? string.Empty
                        : $" = {v.Initializer.Value}";
                    var displayText = FieldIsConst(f)
                        ? $"{v.Identifier.ValueText} : {fieldType} = {v.Initializer?.Value}"
                        : $"{v.Identifier.ValueText} : {fieldType}{initializerText}";
                    return new MemberItem
                    {
                        Name = v.Identifier.ValueText,
                        DeclaringClassName = classDisplayName,
                        DisplayText = displayText,
                        DisplayParts = FieldIsConst(f)
                            ? Parts(
                                (v.Identifier.ValueText, true, true),
                                (" : ", false, false),
                                (fieldType, true, false),
                                ($" = {v.Initializer?.Value}", false, false))
                            : Parts(
                                (v.Identifier.ValueText, true, true),
                                (" : ", false, false),
                                (fieldType, true, false),
                                (initializerText, false, false)),
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
                    var propertyType = GetShortTypeName(p.Type);
                    var accessorText = GetPropertyAccessorDisplayText(p);
                    var initializerText = p.Initializer == null
                        ? string.Empty
                        : $" = {p.Initializer.Value}";
                    var displayText = $"{p.Identifier.ValueText} : {propertyType} {accessorText}{initializerText}".TrimEnd();
                    return new MemberItem
                    {
                        Name = p.Identifier.ValueText,
                        DeclaringClassName = classDisplayName,
                        DisplayText = displayText,
                        DisplayParts = string.IsNullOrEmpty(accessorText)
                            ? Parts(
                                (p.Identifier.ValueText, true, true),
                                (" : ", false, false),
                                (propertyType, true, false),
                                (initializerText, false, false))
                            : Parts(
                                (p.Identifier.ValueText, true, true),
                                (" : ", false, false),
                                (propertyType, true, false),
                                ($" {accessorText}", false, false),
                                (initializerText, false, false)),
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
                    var returnType = GetShortTypeName(m.ReturnType);
                    var methodDisplayName = GetMethodDisplayName(m);
                    var parameterDisplayParts = GetParameterDisplayParts(m.ParameterList);
                    return new MemberItem
                    {
                        Name = m.Identifier.ValueText,
                        DeclaringClassName = classDisplayName,
                        DisplayText = $"{returnType} : {methodDisplayName} ({GetParameterDisplayText(m.ParameterList)})",
                        DisplayParts = Parts((returnType, true, false), (" : ", false, false), (methodDisplayName, true, true), (" (", false, false))
                            .Concat(parameterDisplayParts)
                            .Concat(Parts((")", false, false)))
                            .ToList(),
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
                    var parameterDisplayParts = GetParameterDisplayParts(c.ParameterList);
                    return new MemberItem
                    {
                        Name = c.Identifier.ValueText,
                        DeclaringClassName = classDisplayName,
                        DisplayText = $"ctor {c.Identifier.ValueText} ({GetParameterDisplayText(c.ParameterList)})",
                        DisplayParts = Parts(("ctor ", false, false), (c.Identifier.ValueText, true, true), (" (", false, false))
                            .Concat(parameterDisplayParts)
                            .Concat(Parts((")", false, false)))
                            .ToList(),
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

            return regions
                .Concat(fields)
                .Concat(properties)
                .Concat(primaryConstructor.Concat(constructors).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                .Concat(methods)
                .ToList();
        }

        private static IReadOnlyList<MemberItem> GetRegions(
            BaseTypeDeclarationSyntax currentType,
            SourceText parsedText,
            string? sourceFilePath)
        {
            var classDisplayName = GetTypeDisplayName(currentType);
            var regionStack = new Stack<RegionDirectiveTriviaSyntax>();
            var regions = new List<MemberItem>();

            foreach (var directive in currentType.SyntaxTree
                .GetCompilationUnitRoot()
                .DescendantTrivia(descendIntoTrivia: true)
                .Select(trivia => trivia.GetStructure())
                .OfType<DirectiveTriviaSyntax>()
                .OrderBy(directive => directive.SpanStart))
            {
                if (directive is RegionDirectiveTriviaSyntax regionDirective)
                {
                    regionStack.Push(regionDirective);
                    continue;
                }

                if (directive is EndRegionDirectiveTriviaSyntax endRegionDirective &&
                    regionStack.Count > 0)
                {
                    var matchingRegionDirective = regionStack.Pop();
                    regions.Add(CreateRegionItem(
                        matchingRegionDirective,
                        endRegionDirective,
                        classDisplayName,
                        parsedText,
                        sourceFilePath));
                }
            }

            return regions
                .OrderBy(region => region.StartOffset)
                .ToList();
        }

        private static MemberItem CreateRegionItem(
            RegionDirectiveTriviaSyntax regionDirective,
            EndRegionDirectiveTriviaSyntax endRegionDirective,
            string classDisplayName,
            SourceText parsedText,
            string? sourceFilePath)
        {
            var regionLine = parsedText.Lines.GetLineFromPosition(regionDirective.HashToken.SpanStart);
            var endRegionLine = parsedText.Lines.GetLineFromPosition(endRegionDirective.HashToken.SpanStart);
            var regionName = GetRegionName(regionLine.ToString());
            var regionContentStart = Math.Min(regionLine.EndIncludingLineBreak, parsedText.Length);
            var regionContentEnd = Math.Max(regionContentStart, endRegionLine.Start);
            var regionContent = parsedText.ToString(TextSpan.FromBounds(regionContentStart, regionContentEnd)).Trim();
            var tooltipText = LimitLines(regionContent, maxLines: 15);
            var displayText = string.IsNullOrWhiteSpace(regionName)
                ? "Region"
                : $"Region {regionName}";
            var nameStartOffset = regionLine.Start;
            var nameLength = Math.Max(1, regionLine.End - regionLine.Start);
            var span = parsedText.Lines.GetLinePositionSpan(TextSpan.FromBounds(nameStartOffset, nameStartOffset + nameLength));

            return new MemberItem
            {
                Name = regionName,
                DeclaringClassName = classDisplayName,
                DisplayText = displayText,
                TooltipText = string.IsNullOrEmpty(tooltipText) ? displayText : tooltipText,
                DisplayParts = string.IsNullOrWhiteSpace(regionName)
                    ? Parts(("Region", true, true))
                    : Parts(("Region ", false, false), (regionName, true, true)),
                Kind = MemberKind.Region,
                StartOffset = regionDirective.SpanStart,
                NameStartOffset = nameStartOffset,
                NameLength = nameLength,
                NameStartLine = span.Start.Line,
                NameStartColumn = span.Start.Character,
                NameEndLine = span.End.Line,
                NameEndColumn = span.End.Character,
                SourceFilePath = sourceFilePath
            };
        }

        private static string GetRegionName(string regionLine)
        {
            var regionIndex = regionLine.IndexOf("#region", StringComparison.Ordinal);
            return regionIndex < 0
                ? string.Empty
                : regionLine.Substring(regionIndex + "#region".Length).Trim();
        }

        private static string LimitLines(string text, int maxLines)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            return string.Join(Environment.NewLine, lines.Take(maxLines));
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
                        DisplayParts = member.EqualsValue == null
                            ? Parts((member.Identifier.ValueText, true, true))
                            : Parts(
                                (member.Identifier.ValueText, true, true),
                                ($" = {member.EqualsValue.Value}", false, false)),
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
                DisplayParts = Parts(("ctor ", false, false), (currentClass.Identifier.ValueText, true, true), (" (", false, false))
                    .Concat(GetParameterDisplayParts(currentClass.ParameterList!))
                    .Concat(Parts((")", false, false)))
                    .ToList(),
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
                    parameter.Default == null
                        ? $"{parameter.Identifier.ValueText}: {GetShortTypeName(parameter.Type)}"
                        : $"{parameter.Identifier.ValueText}: {GetShortTypeName(parameter.Type)} = {parameter.Default.Value}"));
        }

        private static IReadOnlyList<MemberDisplayPart> GetParameterDisplayParts(ParameterListSyntax parameterList)
        {
            var parts = new List<MemberDisplayPart>();

            for (var index = 0; index < parameterList.Parameters.Count; index++)
            {
                var parameter = parameterList.Parameters[index];
                if (index > 0)
                {
                    parts.Add(new MemberDisplayPart(", "));
                }

                parts.Add(new MemberDisplayPart(
                    parameter.Identifier.ValueText,
                    isParameterName: true));
                parts.Add(new MemberDisplayPart(": "));
                parts.Add(new MemberDisplayPart(GetShortTypeName(parameter.Type), isBold: true));
                if (parameter.Default != null)
                {
                    parts.Add(new MemberDisplayPart($" = {parameter.Default.Value}"));
                }
            }

            return parts;
        }

        private static IReadOnlyList<MemberDisplayPart> Parts(params (string Text, bool IsBold, bool IsMemberName)[] parts)
        {
            return parts
                .Select(part => new MemberDisplayPart(part.Text, part.IsBold, part.IsMemberName))
                .ToList();
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
