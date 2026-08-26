using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Windows.Controls;
using dnSpy.Contracts.Settings.AppearanceCategory;
using dnSpy.Contracts.Text;
using dnSpy.Contracts.Text.Classification;
using dnSpy.Contracts.Text.Editor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using MonoWeaver.Patterns;
using RoslynTextSpan = Microsoft.CodeAnalysis.Text.TextSpan;

namespace MonoWeaver.DnSpyEx;

internal static class SnippetContentType
{
    public const string Name = "MonoWeaver-CSharpSnippet";

#pragma warning disable CS0649, CS0169
    [Export, Name(Name), BaseDefinition(ContentTypes.Code)]
    private static ContentTypeDefinition? definition;
#pragma warning restore CS0649, CS0169
}

internal readonly struct SnippetClassification
{
    public SnippetClassification(int start, int length, IClassificationType type)
    {
        Start = start;
        Length = length;
        Type = type;
    }

    public int Start { get; }
    public int Length { get; }
    public IClassificationType Type { get; }
}

internal abstract class SnippetClassificationState
{
    public static readonly object PropertyKey = new();
    public abstract IReadOnlyList<SnippetClassification> Classify(string text);
}

internal sealed class FixedSnippetClassificationState : SnippetClassificationState
{
    private readonly IReadOnlyList<SnippetClassification> classifications;

    public FixedSnippetClassificationState(IReadOnlyList<SnippetClassification> classifications)
        => this.classifications = classifications;

    public override IReadOnlyList<SnippetClassification> Classify(string text)
        => classifications;
}

internal sealed class RoslynSnippetClassificationState : SnippetClassificationState
{
    private readonly IThemeClassificationTypeService classificationTypes;
    private readonly bool expression;
    private readonly MetadataReference[] references;
    private string? cachedText;
    private IReadOnlyList<SnippetClassification>? cachedClassifications;

    public RoslynSnippetClassificationState(IThemeClassificationTypeService classificationTypes,
        bool expression, MetadataReference[] references)
    {
        this.classificationTypes = classificationTypes;
        this.expression = expression;
        this.references = references;
    }

    public static MetadataReference[] CreateReferences(IEnumerable<string> referencePaths)
    {
        var result = new List<MetadataReference>();
        foreach (var path in referencePaths
                     .Where(static path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                result.Add(MetadataReference.CreateFromFile(path));
            }
            catch (BadImageFormatException)
            {
                // Unity/game folders commonly contain native DLLs next to managed assemblies.
            }
            catch (IOException)
            {
                // Syntax coloring should remain available when an optional reference is locked.
            }
            catch (UnauthorizedAccessException)
            {
                // Treat inaccessible neighboring assemblies as optional references.
            }
        }
        return result.ToArray();
    }

    public override IReadOnlyList<SnippetClassification> Classify(string text)
    {
        if (string.Equals(text, cachedText, StringComparison.Ordinal)
            && cachedClassifications is not null)
        {
            return cachedClassifications;
        }

        const string statementPrefix =
            "using System;\n" +
            "using System.Linq;\n" +
            "using MonoWeaver.Cecil;\n" +
            "using MonoWeaver.CFG;\n" +
            "using MonoWeaver.Patterns;\n" +
            "internal sealed class __Preview { private void __Method(dynamic il) {\n";
        const string expressionPrefix = statementPrefix + "_ = ";
        const string suffix = "\n} }";
        var prefix = expression ? expressionPrefix : statementPrefix;
        var wrapped = prefix + text + (expression ? ";" : string.Empty) + suffix;
        var tree = CSharpSyntaxTree.ParseText(wrapped);
        var root = tree.GetRoot();
        var compilation = CSharpCompilation.Create("MonoWeaver.PatternPreview",
            new[] { tree }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
        var snippetSpan = new RoslynTextSpan(prefix.Length, text.Length);
        var result = new List<SnippetClassification>();

        foreach (var token in root.DescendantTokens(descendIntoTrivia: true))
        {
            if (!snippetSpan.Contains(token.Span) || token.Span.Length == 0)
                continue;
            var color = GetTokenColor(token, model);
            if (color != TextColor.Text)
            {
                result.Add(new SnippetClassification(token.Span.Start - prefix.Length,
                    token.Span.Length, classificationTypes.GetClassificationType(color)));
            }
        }

        foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: true))
        {
            if (!snippetSpan.Contains(trivia.Span) || trivia.Span.Length == 0)
                continue;
            var name = trivia.Kind().ToString();
            var color = name.IndexOf("Comment", StringComparison.Ordinal) >= 0
                ? TextColor.Comment
                : name.IndexOf("Directive", StringComparison.Ordinal) >= 0
                    ? TextColor.PreprocessorText
                    : name == "DisabledTextTrivia" ? TextColor.ExcludedCode : TextColor.Text;
            if (color != TextColor.Text)
            {
                result.Add(new SnippetClassification(trivia.Span.Start - prefix.Length,
                    trivia.Span.Length, classificationTypes.GetClassificationType(color)));
            }
        }

        cachedText = text;
        cachedClassifications = result.OrderBy(static item => item.Start).ToArray();
        return cachedClassifications;
    }

    private static TextColor GetTokenColor(SyntaxToken token, SemanticModel model)
    {
        var kind = token.Kind();
        if (SyntaxFacts.IsKeywordKind(kind)
            || SyntaxFacts.GetContextualKeywordKind(token.ValueText) != SyntaxKind.None)
            return TextColor.Keyword;

        var name = kind.ToString();
        if (name.IndexOf("NumericLiteral", StringComparison.Ordinal) >= 0)
            return TextColor.Number;
        if (name.IndexOf("CharacterLiteral", StringComparison.Ordinal) >= 0)
            return TextColor.Char;
        if (name.IndexOf("String", StringComparison.Ordinal) >= 0)
            return TextColor.String;
        if (kind == SyntaxKind.IdentifierToken)
            return GetIdentifierColor(token, model);
        if (kind == SyntaxKind.EndOfFileToken)
            return TextColor.Text;
        if (name.EndsWith("Token", StringComparison.Ordinal))
        {
            return name.IndexOf("Paren", StringComparison.Ordinal) >= 0
                   || name.IndexOf("Brace", StringComparison.Ordinal) >= 0
                   || name.IndexOf("Bracket", StringComparison.Ordinal) >= 0
                   || name is "CommaToken" or "DotToken" or "ColonToken" or "SemicolonToken"
                ? TextColor.Punctuation
                : TextColor.Operator;
        }
        return TextColor.Text;
    }

    private static TextColor GetIdentifierColor(SyntaxToken token, SemanticModel model)
    {
        var node = token.Parent;
        if (node is null)
            return TextColor.Text;

        var symbol = model.GetSymbolInfo(node).Symbol
                     ?? model.GetSymbolInfo(node).CandidateSymbols.FirstOrDefault()
                     ?? model.GetDeclaredSymbol(node);
        if (symbol is not null)
            return SymbolColor(symbol);

        if (node is VariableDeclaratorSyntax)
            return TextColor.Local;
        if (node is ParameterSyntax)
            return TextColor.Parameter;
        if (node.Parent is InvocationExpressionSyntax)
            return TextColor.InstanceMethod;
        if (node is SimpleNameSyntax { Parent: MemberAccessExpressionSyntax member }
            && ReferenceEquals(member.Name, node))
        {
            return member.Parent is InvocationExpressionSyntax
                ? TextColor.InstanceMethod
                : TextColor.InstanceProperty;
        }
        if (node is IdentifierNameSyntax identifier
            && identifier.Parent is QualifiedNameSyntax or AliasQualifiedNameSyntax)
            return TextColor.Type;
        return TextColor.Text;
    }

    private static TextColor SymbolColor(ISymbol symbol)
    {
        return symbol switch
        {
            INamespaceSymbol => TextColor.Namespace,
            INamedTypeSymbol type => type.TypeKind switch
            {
                TypeKind.Interface => TextColor.Interface,
                TypeKind.Enum => TextColor.Enum,
                TypeKind.Delegate => TextColor.Delegate,
                TypeKind.Struct => TextColor.ValueType,
                _ when type.IsStatic => TextColor.StaticType,
                _ when type.IsSealed => TextColor.SealedType,
                _ => TextColor.Type,
            },
            ITypeParameterSymbol typeParameter => typeParameter.TypeParameterKind == TypeParameterKind.Method
                ? TextColor.MethodGenericParameter
                : TextColor.TypeGenericParameter,
            IMethodSymbol method => method.IsExtensionMethod
                ? TextColor.ExtensionMethod
                : method.IsStatic ? TextColor.StaticMethod : TextColor.InstanceMethod,
            IFieldSymbol field => field.ContainingType?.TypeKind == TypeKind.Enum
                ? TextColor.EnumField
                : field.IsConst ? TextColor.LiteralField
                : field.IsStatic ? TextColor.StaticField : TextColor.InstanceField,
            IPropertySymbol property => property.IsStatic
                ? TextColor.StaticProperty : TextColor.InstanceProperty,
            IEventSymbol @event => @event.IsStatic
                ? TextColor.StaticEvent : TextColor.InstanceEvent,
            IParameterSymbol => TextColor.Parameter,
            ILocalSymbol => TextColor.Local,
            ILabelSymbol => TextColor.Label,
            _ => TextColor.Text,
        };
    }
}

[Export(typeof(IClassifierProvider))]
[ContentType(SnippetContentType.Name)]
internal sealed class SnippetClassifierProvider : IClassifierProvider
{
    public IClassifier GetClassifier(ITextBuffer buffer)
        => new SnippetClassifier(buffer);
}

internal sealed class SnippetClassifier : IClassifier
{
    private readonly ITextBuffer buffer;

    public SnippetClassifier(ITextBuffer buffer)
    {
        this.buffer = buffer;
        buffer.Changed += Buffer_Changed;
    }

    public event EventHandler<ClassificationChangedEventArgs>? ClassificationChanged;

    public IList<ClassificationSpan> GetClassificationSpans(SnapshotSpan requested)
    {
        var result = new List<ClassificationSpan>();
        if (!buffer.Properties.TryGetProperty(SnippetClassificationState.PropertyKey,
                out SnippetClassificationState state))
            return result;

        var snapshot = requested.Snapshot;
        foreach (var item in state.Classify(snapshot.GetText()))
        {
            if (item.Start < 0 || item.Length <= 0 || item.Start + item.Length > snapshot.Length)
                continue;
            var span = new SnapshotSpan(snapshot, item.Start, item.Length);
            if (requested.IntersectsWith(span))
                result.Add(new ClassificationSpan(span, item.Type));
        }
        return result;
    }

    private void Buffer_Changed(object sender, TextContentChangedEventArgs e)
    {
        if (e.After.Length != 0)
        {
            ClassificationChanged?.Invoke(this,
                new ClassificationChangedEventArgs(new SnapshotSpan(e.After, 0, e.After.Length)));
        }
    }
}

internal sealed class CSharpSnippetView : IDisposable
{
    private readonly ITextBuffer buffer;
    private readonly IDsWpfTextViewHost host;

    public CSharpSnippetView(IDsTextEditorFactoryService editorFactory,
        ITextBufferFactoryService bufferFactory,
        IContentTypeRegistryService contentTypeRegistry,
        SnippetClassificationState classificationState)
    {
        var contentType = contentTypeRegistry.GetContentType(SnippetContentType.Name)
            ?? throw new InvalidOperationException("MonoWeaver C# snippet content type is unavailable.");
        buffer = bufferFactory.CreateTextBuffer(string.Empty, contentType);
        buffer.Properties.AddProperty(SnippetClassificationState.PropertyKey, classificationState);
        var roles = editorFactory.CreateTextViewRoleSet(new[]
        {
            PredefinedTextViewRoles.Document,
            PredefinedTextViewRoles.Analyzable,
            PredefinedTextViewRoles.Interactive,
            PredefinedTextViewRoles.Editable,
        });
        var view = editorFactory.CreateTextView(buffer, roles, new TextViewCreatorOptions
        {
            EnableUndoHistory = false,
        });
        view.Options.SetOptionValue(DefaultWpfViewOptions.AppearanceCategory,
            AppearanceCategoryConstants.TextEditor);
        view.Options.SetOptionValue(DefaultTextViewOptions.ViewProhibitUserInputId, false);
        view.Options.SetOptionValue(DefaultDsTextViewOptions.EnableColorizationId, true);
        view.Options.SetOptionValue(DefaultTextViewOptions.WordWrapStyleId, WordWrapStyles.None);
        view.Options.SetOptionValue(DefaultTextViewHostOptions.HorizontalScrollBarId, true);
        view.Options.SetOptionValue(DefaultTextViewHostOptions.VerticalScrollBarId, true);
        view.Options.SetOptionValue(DefaultTextViewHostOptions.LineNumberMarginId, false);
        view.Options.SetOptionValue(DefaultTextViewHostOptions.GlyphMarginId, false);
        view.Options.SetOptionValue(DefaultTextViewHostOptions.SelectionMarginId, false);
        view.Options.SetOptionValue(DefaultTextViewHostOptions.OutliningMarginId, false);
        view.Options.SetOptionValue(DefaultTextViewHostOptions.ZoomControlId, false);
        host = editorFactory.CreateTextViewHost(view, setFocus: false);
    }

    public Control Control => host.HostControl;

    public string Text
    {
        get => buffer.CurrentSnapshot.GetText();
        set => buffer.Replace(new Span(0, buffer.CurrentSnapshot.Length), value ?? string.Empty);
    }

    public void Dispose()
    {
        if (!host.IsClosed)
            host.Close();
    }

    public static IEnumerable<string> GetDefaultReferencePaths(
        IEnumerable<string> targetReferencePaths)
    {
        yield return typeof(object).Assembly.Location;
        yield return typeof(Enumerable).Assembly.Location;
        yield return typeof(Cil).Assembly.Location;
        foreach (var path in targetReferencePaths)
            yield return path;
    }
}
