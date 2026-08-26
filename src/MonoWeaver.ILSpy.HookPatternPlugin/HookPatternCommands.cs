using System;
using System.Collections.Generic;
using System.Composition;
using System.IO;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.VisualTree;
using AvaloniaEdit.Highlighting;
using ICSharpCode.ILSpy;
using ICSharpCode.ILSpy.Commands;
using ICSharpCode.ILSpy.Docking;
using ICSharpCode.ILSpy.TextView;
using Mono.Cecil;
using MonoWeaver.ILSpy.Properties;
using MonoWeaver.Patterns.Generation;

namespace MonoWeaver.ILSpy;

[ExportContextMenuEntry(Header = "Generate MonoWeaver HookPattern...", Category = "MonoWeaver",
    Order = 250, InputGestureText = "Ctrl+Alt+H")]
[Shared]
public sealed class HookPatternContextMenuEntry : IContextMenuEntry
{
    private readonly HookPatternCommandService command;

    [ImportingConstructor]
    public HookPatternContextMenuEntry(HookPatternCommandService command) => this.command = command;

    public bool IsVisible(TextViewContext context) => context.TextView is not null;
    public bool IsEnabled(TextViewContext context)
        => context.TextView is { } view && command.CanExecute(view, context.TextLocation);
    public void Execute(TextViewContext context)
    {
        if (context.TextView is { } view)
            command.Execute(view, context.TextLocation);
    }
}

[ExportMainMenuCommand(ParentMenuID = "_View", Header = "Generate MonoWeaver HookPattern...",
    MenuCategory = "MonoWeaver", MenuOrder = 500, InputGestureText = "Ctrl+Alt+H")]
[Shared]
public sealed class HookPatternShortcutCommand : ICommand
{
    private readonly HookPatternCommandService command;
    private readonly DockWorkspace workspace;

    [ImportingConstructor]
    public HookPatternShortcutCommand(HookPatternCommandService command, DockWorkspace workspace)
    {
        this.command = command;
        this.workspace = workspace;
    }

    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter)
    {
        var view = FindActiveView();
        if (view is not null)
            command.Execute(view, null);
    }

    public event EventHandler? CanExecuteChanged { add { } remove { } }

    private DecompilerTextView? FindActiveView()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null || workspace.ActiveDecompilerTab is null)
            return null;
        return desktop.MainWindow.GetVisualDescendants()
            .OfType<DecompilerTextView>()
            .FirstOrDefault(view => ReferenceEquals(view.DataContext, workspace.ActiveDecompilerTab));
    }
}

[Export, Shared]
public sealed class HookPatternCommandService
{
    public bool CanExecute(DecompilerTextView view, int? location)
        => TryCapture(view, location, includeColors: false, out _);

    public async void Execute(DecompilerTextView view, int? location)
    {
        var owner = TopLevel.GetTopLevel(view) as Window;
        try
        {
            if (!TryCapture(view, location, includeColors: true, out var capture))
                return;
            var suggestions = Generate(capture!);
            if (suggestions.Count == 0)
            {
                await MessageDialog.Show(owner, UiStrings.Get("NoSuggestions"));
                return;
            }
            var window = new HookPatternWindow(capture!.MethodDisplayName, capture.SourceText,
                capture.SourceColors, suggestions);
            if (owner is null)
                window.Show();
            else
                await window.ShowDialog(owner);
        }
        catch (Exception ex)
        {
            await MessageDialog.Show(owner, ex.Message);
        }
    }

    private static IReadOnlyList<HookPatternSuggestion> Generate(ILSpySelection capture)
    {
        if (string.IsNullOrWhiteSpace(capture.FileName) || !File.Exists(capture.FileName))
            throw new InvalidOperationException(UiStrings.Get("ModuleUnsaved"));

        var resolver = new DefaultAssemblyResolver();
        var directory = Path.GetDirectoryName(capture.FileName);
        if (!string.IsNullOrWhiteSpace(directory))
            resolver.AddSearchDirectory(directory);
        using var module = ModuleDefinition.ReadModule(capture.FileName, new ReaderParameters {
            AssemblyResolver = resolver,
            InMemory = true,
            ReadSymbols = false,
        });
        if (module.LookupToken(new MetadataToken(capture.Token)) is not MethodDefinition method
            || !method.HasBody)
        {
            throw new InvalidOperationException(UiStrings.Format("MethodResolveFailed", $"0x{capture.Token:X8}"));
        }

        var allPoints = capture.MethodPoints.Select(point => point.ILOffset).Distinct().OrderBy(x => x).ToArray();
        var selected = capture.SelectedPointOffsets.ToHashSet();
        var offsets = new List<int>();
        foreach (var point in allPoints.Where(selected.Contains))
        {
            var next = allPoints.FirstOrDefault(value => value > point);
            var hasNext = next > point;
            offsets.AddRange(method.Body.Instructions
                .Where(instruction => instruction.Offset >= point && (!hasNext || instruction.Offset < next))
                .Select(instruction => instruction.Offset));
        }
        if (offsets.Count == 0)
            throw new InvalidOperationException(UiStrings.Get("NoIlMapping"));

        var analysis = HookPatternSuggester.AnalyzeSelection(method, offsets.Distinct().ToArray());
        if (analysis.Status == HookPatternSelectionStatus.RequiresMultiplePatterns)
            throw new InvalidOperationException(UiStrings.Get("SelectionExceedsSinglePattern"));
        return analysis.Suggestions;
    }

    private static bool TryCapture(DecompilerTextView view, int? location, bool includeColors,
        out ILSpySelection? capture)
    {
        capture = null;
        var editor = view.FindControl<DecompilerTextEditor>("Editor");
        if (editor?.Document is null
            || view.DataContext is not DecompilerTabPageModel { SyntaxExtension: ".cs", DebugInfo: { } debug } model
            || debug.Methods.Count == 0)
            return false;

        var document = editor.Document;
        var hasSelection = editor.SelectionLength > 0;
        var rawStart = hasSelection ? editor.SelectionStart
            : Math.Clamp(location ?? editor.TextArea.Caret.Offset, 0, document.TextLength);
        var rawLength = hasSelection ? editor.SelectionLength : 0;
        if (!hasSelection)
        {
            var line = document.GetLineByOffset(rawStart);
            rawStart = line.Offset;
            rawLength = line.Length;
        }
        if (rawLength <= 0)
            return false;

        var rawText = document.GetText(rawStart, rawLength);
        var sourceText = rawText.Trim();
        if (sourceText.Length == 0)
            return false;
        var leading = rawText.IndexOf(sourceText, StringComparison.Ordinal);
        var sourceStart = rawStart + Math.Max(0, leading);
        var sourceEnd = sourceStart + sourceText.Length;
        var startLine = document.GetLineByOffset(sourceStart).LineNumber;
        var endLine = document.GetLineByOffset(Math.Max(sourceStart, sourceEnd - 1)).LineNumber;
        var anchorLine = document.GetLineByOffset(Math.Clamp(location ?? editor.TextArea.Caret.Offset,
            0, document.TextLength)).LineNumber;

        var points = new List<SourcePoint>();
        foreach (var method in debug.Methods)
        {
            for (var line = 1; line <= document.LineCount; line++)
            {
                if (method.TryGetOffsetForLine(line, out var ilOffset))
                    points.Add(new SourcePoint(method, line, ilOffset));
            }
        }
        var anchor = points.Where(point => point.Line >= startLine && point.Line <= endLine)
            .OrderBy(point => Math.Abs(point.Line - anchorLine)).FirstOrDefault()
            ?? points.Where(point => point.Line <= anchorLine).OrderByDescending(point => point.Line).FirstOrDefault()
            ?? points.OrderBy(point => point.Line).FirstOrDefault();
        if (anchor is null)
            return false;
        var methodPoints = points.Where(point => ReferenceEquals(point.Method, anchor.Method))
            .OrderBy(point => point.ILOffset).ToArray();
        var selectedPoints = methodPoints
            .Where(point => point.Line >= startLine && point.Line <= endLine)
            .Select(point => point.ILOffset).Distinct().ToArray();
        if (selectedPoints.Length == 0)
            selectedPoints = new[] { anchor.ILOffset };

        var colors = new List<SourceColorSpan>();
        if (includeColors && model.HighlightingSpans is { } spans)
        {
            foreach (var (start, length, color) in spans)
            {
                var clippedStart = Math.Max(sourceStart, start);
                var clippedEnd = Math.Min(sourceEnd, start + length);
                if (clippedEnd > clippedStart)
                    colors.Add(new SourceColorSpan(clippedStart - sourceStart,
                        clippedEnd - clippedStart, color));
            }
        }
        capture = new ILSpySelection(anchor.Method.FileName, anchor.Method.Token,
            anchor.Method.MemberName, sourceText, colors, methodPoints, selectedPoints);
        return true;
    }
}

internal sealed record SourcePoint(ICSharpCode.ILSpy.Bookmarks.MethodDebugInfo Method,
    int Line, int ILOffset);

internal sealed record SourceColorSpan(int Start, int Length, HighlightingColor Color);

internal sealed record ILSpySelection(string FileName, uint Token, string MethodDisplayName,
    string SourceText, IReadOnlyList<SourceColorSpan> SourceColors,
    IReadOnlyList<SourcePoint> MethodPoints, IReadOnlyList<int> SelectedPointOffsets);
