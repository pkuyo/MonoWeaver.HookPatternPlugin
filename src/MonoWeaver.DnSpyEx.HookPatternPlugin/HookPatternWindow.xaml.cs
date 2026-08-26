using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using dnSpy.Contracts.Controls;
using dnSpy.Contracts.Text.Classification;
using dnSpy.Contracts.Text.Editor;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Utilities;
using Microsoft.CodeAnalysis;
using MonoWeaver.DnSpyEx.Properties;
using MonoWeaver.Patterns.Generation;

namespace MonoWeaver.DnSpyEx;

public partial class HookPatternWindow : WindowBase
{
    private IReadOnlyList<CandidateRow> candidates = Array.Empty<CandidateRow>();
    private IReadOnlyList<CandidateRow> primaryCandidates = Array.Empty<CandidateRow>();
    private CandidateRow? selected;
    private bool ready;
    private bool synchronizingSelection;
    private readonly CSharpSnippetView sourceView;
    private readonly CSharpSnippetView patternView;
    private readonly CSharpSnippetView hookView;

    internal HookPatternWindow(string method, string source,
        IReadOnlyList<SnippetClassification> sourceClassifications,
        IReadOnlyList<HookPatternSuggestion> suggestions,
        IReadOnlyList<string> targetReferencePaths,
        IDsTextEditorFactoryService textEditorFactory,
        ITextBufferFactoryService textBufferFactory,
        IContentTypeRegistryService contentTypeRegistry,
        IThemeClassificationTypeService themeClassificationTypeService)
    {
        InitializeComponent();
        var referencePaths = CSharpSnippetView.GetDefaultReferencePaths(targetReferencePaths);
        var metadataReferences = RoslynSnippetClassificationState.CreateReferences(referencePaths);
        sourceView = new CSharpSnippetView(textEditorFactory, textBufferFactory,
            contentTypeRegistry, new FixedSnippetClassificationState(sourceClassifications));
        patternView = new CSharpSnippetView(textEditorFactory, textBufferFactory,
            contentTypeRegistry, new RoslynSnippetClassificationState(
                themeClassificationTypeService, expression: true, metadataReferences));
        hookView = new CSharpSnippetView(textEditorFactory, textBufferFactory,
            contentTypeRegistry, new RoslynSnippetClassificationState(
                themeClassificationTypeService, expression: false, metadataReferences));
        SourceEditorContainer.Content = sourceView.Control;
        PatternEditorContainer.Content = patternView.Control;
        CodeEditorContainer.Content = hookView.Control;
        Closed += (_, _) =>
        {
            sourceView.Dispose();
            patternView.Dispose();
            hookView.Dispose();
        };

        MethodText.Text = method;
        sourceView.Text = source;
        candidates = suggestions.Select(static suggestion => new CandidateRow(suggestion)).ToArray();
        primaryCandidates = candidates
            .GroupBy(static candidate => candidate.Kind)
            .Select(static group => group.First())
            .ToArray();
        PatternTypeCombo.ItemsSource = primaryCandidates;
        CandidatesGrid.ItemsSource = candidates;
        if (candidates.Count != 0)
        {
            ready = true;
            SelectCandidate(candidates[0], updateType: true, updateAdvanced: true);
        }
    }

    private CandidateRow? Selected => selected;

    private void PatternTypeCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || synchronizingSelection || PatternTypeCombo.SelectedItem is not CandidateRow candidate)
            return;
        SelectCandidate(candidate, updateType: false, updateAdvanced: true);
    }

    private void CandidatesGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || synchronizingSelection || CandidatesGrid.SelectedItem is not CandidateRow candidate)
            return;
        SelectCandidate(candidate, updateType: true, updateAdvanced: false);
    }

    private void Position_OnChanged(object sender, RoutedEventArgs e) => RefreshPreview();
    private void CallbackText_OnTextChanged(object sender, TextChangedEventArgs e) => RefreshPreview();

    private void RefreshPreview()
    {
        if (!IsInitialized || Selected is not { } selected)
            return;

        var suggestion = selected.Suggestion;
        patternView.Text = suggestion.PatternCode;
        AfterRadio.IsEnabled = suggestion.SupportsAfter;
        if (!AfterRadio.IsEnabled && AfterRadio.IsChecked == true)
            BeforeRadio.IsChecked = true;

        var isValidated = suggestion.MatchCount == 1 && suggestion.MatchesSelection;
        var status = isValidated
            ? UiStrings.Get("StatusUnique")
            : UiStrings.Format("StatusAmbiguous", suggestion.MatchCount);
        if (suggestion.Diagnostics.Count != 0)
            status += Environment.NewLine + string.Join(Environment.NewLine, suggestion.Diagnostics);
        StatusText.Text = status;

        var callback = string.IsNullOrWhiteSpace(CallbackText.Text)
            ? "Hooks.OnHook"
            : CallbackText.Text.Trim();
        var position = AfterRadio.IsChecked == true ? HookPosition.After : HookPosition.Before;
        hookView.Text = suggestion.BuildHookCode(position, callback);
    }

    private void SelectCandidate(CandidateRow candidate, bool updateType, bool updateAdvanced)
    {
        selected = candidate;
        synchronizingSelection = true;
        try
        {
            if (updateType)
            {
                PatternTypeCombo.SelectedItem = primaryCandidates
                    .FirstOrDefault(item => item.Kind == candidate.Kind);
            }
            if (updateAdvanced)
                CandidatesGrid.SelectedItem = candidate;
        }
        finally
        {
            synchronizingSelection = false;
        }
        RefreshPreview();
    }

    private void CopyPattern_OnClick(object sender, RoutedEventArgs e)
    {
        if (Selected is { } selected)
            TryCopy(selected.Suggestion.PatternCode);
    }

    private void CopyHook_OnClick(object sender, RoutedEventArgs e) => TryCopy(hookView.Text);

    private static void TryCopy(string text)
    {
        try
        {
            Clipboard.SetText(text ?? string.Empty);
        }
        catch (ExternalException)
        {
        }
    }
}

internal sealed class CandidateRow
{
    public CandidateRow(HookPatternSuggestion suggestion) => Suggestion = suggestion;
    public HookPatternSuggestion Suggestion { get; }
    public HookPatternKind Kind => Suggestion.Kind;
    public string Location => Suggestion.Location;
    public string ExpressionCode => Suggestion.ExpressionCode;
    public int InstructionCount => Suggestion.InstructionOffsets.Count;
    public string MatchSummary => Suggestion.MatchCount == 1 && Suggestion.MatchesSelection
        ? UiStrings.Format("MatchSelected", 1)
        : Suggestion.MatchCount.ToString();
}
