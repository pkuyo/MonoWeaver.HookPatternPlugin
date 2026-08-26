using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using dnlib.DotNet;
using dnSpy.Contracts.Controls;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Documents.Tabs.DocViewer;
using dnSpy.Contracts.Extension;
using dnSpy.Contracts.Menus;
using dnSpy.Contracts.Text;
using dnSpy.Contracts.Text.Classification;
using dnSpy.Contracts.Text.Editor;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;
using Mono.Cecil;
using MonoWeaver.DnSpyEx.Properties;
using MonoWeaver.Patterns.Generation;
using CecilMethodDefinition = Mono.Cecil.MethodDefinition;

namespace MonoWeaver.DnSpyEx;

internal static class ExtensionConstants
{
    public const string ContextMenuGroup = "25000,0F15EB57-7F30-4E86-A679-EF72D3C17568";
    public const string ShortcutText = "Ctrl+Alt+H";
}

[ExportMenuItem(Header = "res:MenuGenerate",
    InputGestureText = ExtensionConstants.ShortcutText,
    Group = ExtensionConstants.ContextMenuGroup, Order = 0)]
internal sealed class GenerateHookPatternMenuCommand : MenuItemBase
{
    private readonly HookPatternCommandService commandService;

    [ImportingConstructor]
    public GenerateHookPatternMenuCommand(HookPatternCommandService commandService)
        => this.commandService = commandService;

    public override bool IsVisible(IMenuItemContext context)
        => context.CreatorObject.Guid == new Guid(MenuConstants.GUIDOBJ_DOCUMENTVIEWERCONTROL_GUID);

    public override bool IsEnabled(IMenuItemContext context)
        => commandService.CanExecute(context);

    public override void Execute(IMenuItemContext context)
        => commandService.Execute(context);
}

[Export]
internal sealed class HookPatternCommandService
{
    private readonly IDsTextEditorFactoryService textEditorFactory;
    private readonly ITextBufferFactoryService textBufferFactory;
    private readonly IContentTypeRegistryService contentTypeRegistry;
    private readonly IViewClassifierAggregatorService classifierAggregatorService;
    private readonly IThemeClassificationTypeService themeClassificationTypeService;

    [ImportingConstructor]
    public HookPatternCommandService(
        IDsTextEditorFactoryService textEditorFactory,
        ITextBufferFactoryService textBufferFactory,
        IContentTypeRegistryService contentTypeRegistry,
        IViewClassifierAggregatorService classifierAggregatorService,
        IThemeClassificationTypeService themeClassificationTypeService)
    {
        this.textEditorFactory = textEditorFactory;
        this.textBufferFactory = textBufferFactory;
        this.contentTypeRegistry = contentTypeRegistry;
        this.classifierAggregatorService = classifierAggregatorService;
        this.themeClassificationTypeService = themeClassificationTypeService;
    }

    public bool CanExecute(IMenuItemContext context)
        => TryGetContext(context, out var viewer, out var position)
           && TryGetSelection(viewer!, position, captureClassifications: false, out _);

    public void Execute(IMenuItemContext context)
    {
        if (!TryGetContext(context, out var viewer, out var position))
            return;
        Execute(viewer!, position);
    }

    public bool CanExecute(IDocumentViewer? viewer)
        => viewer is not null
           && TryGetSelection(viewer, viewer.Caret.Position.BufferPosition.Position,
               captureClassifications: false, out _);

    public void Execute(IDocumentViewer? viewer)
    {
        if (viewer is null)
            return;
        Execute(viewer, viewer.Caret.Position.BufferPosition.Position);
    }

    private void Execute(IDocumentViewer viewer, int position)
    {
        if (!TryGetSelection(viewer, position, captureClassifications: true, out var selection))
            return;

        try
        {
            var suggestions = Generate(selection!);
            if (suggestions.Count == 0)
            {
                MessageBox.Show(Application.Current.MainWindow,
                    UiStrings.Get("NoSuggestions"), UiStrings.Get("WindowTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var window = new HookPatternWindow(selection!.Method.FullName,
                selection.SourceText, selection.SourceClassifications, suggestions,
                selection.ReferencePaths, textEditorFactory, textBufferFactory,
                contentTypeRegistry, themeClassificationTypeService)
            {
                Owner = Application.Current.MainWindow,
            };
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show(Application.Current.MainWindow, ex.Message,
                UiStrings.Get("WindowTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static bool TryGetContext(IMenuItemContext context,
        out IDocumentViewer? viewer, out int position)
    {
        viewer = null;
        position = 0;
        if (context.CreatorObject.Guid != new Guid(MenuConstants.GUIDOBJ_DOCUMENTVIEWERCONTROL_GUID))
            return false;
        viewer = context.Find<IDocumentViewer?>();
        var editorPosition = context.Find<TextEditorPosition?>();
        if (viewer is null || editorPosition is null)
            return false;
        position = editorPosition.Position;
        return true;
    }

    private static IReadOnlyList<HookPatternSuggestion> Generate(DnSpySelection selection)
    {
        var location = selection.Method.Module.Location;
        if (string.IsNullOrWhiteSpace(location) || !File.Exists(location))
        {
            throw new InvalidOperationException(UiStrings.Get("ModuleUnsaved"));
        }

        var resolver = new DefaultAssemblyResolver();
        var directory = Path.GetDirectoryName(location);
        if (!string.IsNullOrWhiteSpace(directory))
            resolver.AddSearchDirectory(directory);

        using var module = ModuleDefinition.ReadModule(location, new ReaderParameters
        {
            AssemblyResolver = resolver,
            InMemory = true,
            ReadSymbols = false,
        });

        if (selection.Method.Module.Mvid != Guid.Empty && module.Mvid != selection.Method.Module.Mvid)
        {
            throw new InvalidOperationException(UiStrings.Get("ModuleMismatch"));
        }

        var token = new MetadataToken(selection.Method.MDToken.Raw);
        if (module.LookupToken(token) is not CecilMethodDefinition method || !method.HasBody)
            throw new InvalidOperationException(UiStrings.Format("MethodResolveFailed",
                $"0x{selection.Method.MDToken.Raw:X8}"));

        var offsets = method.Body.Instructions
            .Where(instruction => selection.Spans.Any(span =>
                span.Start <= (uint)instruction.Offset && (uint)instruction.Offset < span.End))
            .Select(static instruction => instruction.Offset)
            .ToArray();

        if (offsets.Length == 0)
            throw new InvalidOperationException(UiStrings.Get("NoIlMapping"));

        var analysis = HookPatternSuggester.AnalyzeSelection(method, offsets);
        if (analysis.Status == HookPatternSelectionStatus.RequiresMultiplePatterns)
            throw new InvalidOperationException(UiStrings.Get("SelectionExceedsSinglePattern"));
        return analysis.Suggestions;
    }

    private bool TryGetSelection(IDocumentViewer viewer, int position,
        bool captureClassifications, out DnSpySelection? selection)
    {
        selection = null;
        var debugService = viewer.GetMethodDebugService();
        var statements = debugService
            .FindByTextPosition(position, FindByTextPositionOptions.SameMethod);
        if (statements.Count == 0)
            return false;

        var method = statements[0].Method;
        if (method.Body is null || method.Body.Instructions.Count == 0)
            return false;

        var snapshot = viewer.TextView.TextSnapshot;
        int start;
        int length;
        MethodSourceStatement[] selectedStatements;
        if (!viewer.TextView.Selection.IsEmpty)
        {
            var selectedSpans = viewer.TextView.Selection.SelectedSpans;
            start = selectedSpans.Min(static span => span.Start.Position);
            var end = selectedSpans.Max(static span => span.End.Position);
            length = Math.Max(0, end - start);
            selectedStatements = debugService
                .GetStatementsByTextSpan(new Microsoft.VisualStudio.Text.Span(start, length))
                .Where(item => ReferenceEquals(item.Method, method))
                .ToArray();
        }
        else
        {
            selectedStatements = statements
                .Where(item => ReferenceEquals(item.Method, method))
                .ToArray();
            var sourceSpan = selectedStatements
                .Select(static item => item.Statement.TextSpan)
                .OrderByDescending(static span => span.Length)
                .First();
            start = sourceSpan.Start;
            length = sourceSpan.Length;
        }

        if (selectedStatements.Length == 0)
            return false;

        start = Math.Max(0, Math.Min(start, snapshot.Length));
        length = Math.Max(0, Math.Min(length, snapshot.Length - start));
        var sourceText = snapshot.GetText(start, length).Trim();

        var trimStart = snapshot.GetText(start, length).IndexOf(sourceText, StringComparison.Ordinal);
        if (trimStart < 0)
            trimStart = 0;
        var classifiedStart = start + trimStart;
        var classifiedLength = sourceText.Length;
        var sourceClassifications = captureClassifications
            ? CaptureClassifications(viewer, classifiedStart, classifiedLength)
            : Array.Empty<SnippetClassification>();

        selection = new DnSpySelection(method,
            selectedStatements.Select(static item => item.Statement.ILSpan).Distinct().ToArray(),
            sourceText, sourceClassifications, GetReferencePaths(method.Module));
        return true;
    }

    private static IReadOnlyList<string> GetReferencePaths(ModuleDef module)
    {
        var result = new List<string>();
        var modulePath = module.Location;
        if (string.IsNullOrWhiteSpace(modulePath))
            return result;

        result.Add(modulePath);
        var directory = Path.GetDirectoryName(modulePath);
        if (string.IsNullOrWhiteSpace(directory))
            return result;

        foreach (var assemblyRef in module.GetAssemblyRefs())
        {
            var name = assemblyRef.Name.String;
            if (string.IsNullOrWhiteSpace(name))
                continue;
            var dllPath = Path.Combine(directory, name + ".dll");
            if (File.Exists(dllPath))
                result.Add(dllPath);
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private IReadOnlyList<SnippetClassification> CaptureClassifications(IDocumentViewer viewer,
        int start, int length)
    {
        if (length == 0)
            return Array.Empty<SnippetClassification>();

        var snapshot = viewer.TextView.TextSnapshot;
        var requested = new SnapshotSpan(snapshot, start, length);
        var classifier = classifierAggregatorService.GetClassifier(viewer.TextView);
        return classifier.GetClassificationSpans(requested)
            .Select(item =>
            {
                var itemStart = Math.Max(start, item.Span.Start.Position);
                var itemEnd = Math.Min(start + length, item.Span.End.Position);
                return new SnippetClassification(itemStart - start,
                    Math.Max(0, itemEnd - itemStart), item.ClassificationType);
            })
            .Where(static item => item.Length != 0)
            .ToArray();
    }
}

[ExportAutoLoaded]
internal sealed class HookPatternShortcutLoader : IAutoLoaded
{
    private static readonly RoutedCommand GenerateCommand =
        new("GenerateMonoWeaverHookPattern", typeof(HookPatternShortcutLoader));

    [ImportingConstructor]
    public HookPatternShortcutLoader(IWpfCommandService wpfCommandService,
        IDocumentTabService documentTabService,
        HookPatternCommandService commandService)
    {
        var commands = wpfCommandService.GetCommands(ControlConstants.GUID_DOCUMENTVIEWER_UICONTEXT);
        commands.Add(GenerateCommand,
            (sender, args) =>
            {
                commandService.Execute(documentTabService.ActiveTab.TryGetDocumentViewer());
                args.Handled = true;
            },
            (sender, args) =>
            {
                args.CanExecute = commandService.CanExecute(
                    documentTabService.ActiveTab.TryGetDocumentViewer());
                args.Handled = true;
            },
            ModifierKeys.Control | ModifierKeys.Alt, Key.H);
    }
}

internal sealed class DnSpySelection
{
    public DnSpySelection(MethodDef method, IReadOnlyList<ILSpan> spans, string sourceText,
        IReadOnlyList<SnippetClassification> sourceClassifications,
        IReadOnlyList<string> referencePaths)
    {
        Method = method;
        Spans = spans;
        SourceText = sourceText;
        SourceClassifications = sourceClassifications;
        ReferencePaths = referencePaths;
    }

    public MethodDef Method { get; }
    public IReadOnlyList<ILSpan> Spans { get; }
    public string SourceText { get; }
    public IReadOnlyList<SnippetClassification> SourceClassifications { get; }
    public IReadOnlyList<string> ReferencePaths { get; }
}
