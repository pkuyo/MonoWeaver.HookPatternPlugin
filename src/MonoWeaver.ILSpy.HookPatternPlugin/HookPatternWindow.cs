using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit.Highlighting;
using ICSharpCode.ILSpy.TextView;
using MonoWeaver.ILSpy.Properties;
using MonoWeaver.Patterns.Generation;

namespace MonoWeaver.ILSpy;

internal sealed class HookPatternWindow : Window
{
    private readonly ComboBox patternType = new();
    private readonly RadioButton before = new();
    private readonly RadioButton after = new();
    private readonly TextBox callback = new();
    private readonly ListBox candidatesList = new();
    private readonly TextBlock status = new();
    private readonly DecompilerTextEditor sourceEditor = CreateEditor();
    private readonly DecompilerTextEditor patternEditor = CreateEditor();
    private readonly DecompilerTextEditor hookEditor = CreateEditor();
    private IReadOnlyList<CandidateRow> candidates = Array.Empty<CandidateRow>();
    private IReadOnlyList<CandidateRow> primaryCandidates = Array.Empty<CandidateRow>();
    private CandidateRow? selected;
    private bool ready;
    private bool synchronizing;

    public HookPatternWindow(string method, string source,
        IReadOnlyList<SourceColorSpan> sourceColors,
        IReadOnlyList<HookPatternSuggestion> suggestions)
    {
        Title = UiStrings.Get("WindowTitle");
        Width = 940;
        Height = 700;
        MinWidth = 760;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = BuildContent(method, source, sourceColors);

        candidates = suggestions.Select(suggestion => new CandidateRow(suggestion)).ToArray();
        primaryCandidates = candidates.GroupBy(candidate => candidate.Kind)
            .Select(group => group.First()).ToArray();
        patternType.ItemsSource = primaryCandidates;
        candidatesList.ItemsSource = candidates;
        if (candidates.Count > 0)
        {
            ready = true;
            SelectCandidate(candidates[0], updateType: true, updateAdvanced: true);
        }
    }

    private Control BuildContent(string method, string source,
        IReadOnlyList<SourceColorSpan> sourceColors)
    {
        var root = new Grid {
            Margin = new Thickness(12),
            RowDefinitions = Rows(GridLength.Auto, GridLength.Auto, GridLength.Auto,
                GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto),
            RowSpacing = 8,
        };

        var methodLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        methodLine.Children.Add(new TextBlock { Text = UiStrings.Get("MethodLabel"), FontWeight = FontWeight.SemiBold });
        methodLine.Children.Add(new TextBlock { Text = method, TextTrimming = TextTrimming.CharacterEllipsis });
        root.Children.Add(methodLine);

        sourceEditor.Height = 76;
        sourceEditor.Text = source;
        ApplySemanticColors(sourceEditor, sourceColors);
        var selectedGroup = Group(UiStrings.Get("SelectedCodeHeader"), sourceEditor);
        Grid.SetRow(selectedGroup, 1);
        root.Children.Add(selectedGroup);

        var hookSetup = BuildHookSetup();
        Grid.SetRow(hookSetup, 2);
        root.Children.Add(hookSetup);

        var advanced = new Expander {
            Header = UiStrings.Get("AdvancedHeader"),
            IsExpanded = false,
            Content = new StackPanel {
                Spacing = 5,
                Children = {
                    new TextBlock { Text = UiStrings.Get("AdvancedDescription"), Opacity = 0.72 },
                    candidatesList,
                }
            }
        };
        candidatesList.Height = 180;
        candidatesList.FontFamily = new FontFamily("Consolas, Menlo, Monospace");
        candidatesList.SelectionChanged += (_, _) => {
            if (ready && !synchronizing && candidatesList.SelectedItem is CandidateRow row)
                SelectCandidate(row, updateType: true, updateAdvanced: false);
        };
        Grid.SetRow(advanced, 3);
        root.Children.Add(advanced);

        var generatedGrid = new Grid {
            RowDefinitions = Rows(new GridLength(2, GridUnitType.Star), new GridLength(6),
                new GridLength(3, GridUnitType.Star)),
        };
        generatedGrid.Children.Add(patternEditor);
        var splitter = new GridSplitter {
            Height = 6,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ResizeDirection = GridResizeDirection.Rows,
        };
        Grid.SetRow(splitter, 1);
        generatedGrid.Children.Add(splitter);
        Grid.SetRow(hookEditor, 2);
        generatedGrid.Children.Add(hookEditor);
        var generatedGroup = Group(UiStrings.Get("GeneratedCodeHeader"), generatedGrid);
        Grid.SetRow(generatedGroup, 4);
        root.Children.Add(generatedGroup);

        var bottom = BuildBottomBar();
        Grid.SetRow(bottom, 5);
        root.Children.Add(bottom);
        return root;
    }

    private GroupBox BuildHookSetup()
    {
        patternType.MinWidth = 150;
        patternType.SelectionChanged += (_, _) => {
            if (ready && !synchronizing && patternType.SelectedItem is CandidateRow row)
                SelectCandidate(row, updateType: false, updateAdvanced: true);
        };
        before.Content = UiStrings.Get("BeforeSelectedCode");
        before.GroupName = "Position";
        before.IsChecked = true;
        after.Content = UiStrings.Get("AfterSelectedCode");
        after.GroupName = "Position";
        before.IsCheckedChanged += (_, _) => RefreshPreview();
        after.IsCheckedChanged += (_, _) => RefreshPreview();
        callback.Text = "Hooks.OnHook";
        callback.FontFamily = new FontFamily("Consolas, Menlo, Monospace");
        callback.TextChanged += (_, _) => RefreshPreview();

        var grid = new Grid {
            Margin = new Thickness(8),
            RowDefinitions = Rows(GridLength.Auto, GridLength.Auto),
            ColumnDefinitions = Columns(GridLength.Auto, new GridLength(210), new GridLength(22),
                GridLength.Auto, new GridLength(1, GridUnitType.Star)),
            RowSpacing = 8,
            ColumnSpacing = 7,
        };
        Add(grid, new TextBlock { Text = UiStrings.Get("PatternTypeLabel"), VerticalAlignment = VerticalAlignment.Center }, 0, 0);
        Add(grid, patternType, 0, 1);
        Add(grid, new TextBlock { Text = UiStrings.Get("PositionLabel"), VerticalAlignment = VerticalAlignment.Center }, 0, 3);
        var positions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        positions.Children.Add(before);
        positions.Children.Add(after);
        Add(grid, positions, 0, 4);
        Add(grid, new TextBlock { Text = UiStrings.Get("CallbackLabel"), VerticalAlignment = VerticalAlignment.Center }, 1, 0);
        Add(grid, callback, 1, 1, columnSpan: 4);
        return Group(UiStrings.Get("HookHeader"), grid, contentHasMargin: true);
    }

    private Control BuildBottomBar()
    {
        status.TextWrapping = TextWrapping.Wrap;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var copyPattern = new Button { Content = UiStrings.Get("CopyPattern"), Padding = new Thickness(12, 5) };
        var copyHook = new Button { Content = UiStrings.Get("CopyHook"), Padding = new Thickness(12, 5) };
        var close = new Button { Content = UiStrings.Get("Close"), Padding = new Thickness(12, 5) };
        copyPattern.Click += async (_, _) => {
            if (selected is not null && Clipboard is { } clipboard)
                await clipboard.SetTextAsync(selected.Suggestion.PatternCode);
        };
        copyHook.Click += async (_, _) => {
            if (Clipboard is { } clipboard)
                await clipboard.SetTextAsync(hookEditor.Text);
        };
        close.Click += (_, _) => Close();
        buttons.Children.Add(copyPattern);
        buttons.Children.Add(copyHook);
        buttons.Children.Add(close);
        var grid = new Grid { ColumnDefinitions = Columns(new GridLength(1, GridUnitType.Star), GridLength.Auto) };
        grid.Children.Add(status);
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);
        return grid;
    }

    private void SelectCandidate(CandidateRow row, bool updateType, bool updateAdvanced)
    {
        selected = row;
        synchronizing = true;
        try
        {
            if (updateType)
                patternType.SelectedItem = primaryCandidates.FirstOrDefault(item => item.Kind == row.Kind);
            if (updateAdvanced)
                candidatesList.SelectedItem = row;
        }
        finally
        {
            synchronizing = false;
        }
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        if (!ready || selected is null)
            return;
        var suggestion = selected.Suggestion;
        patternEditor.Text = suggestion.PatternCode;
        after.IsEnabled = suggestion.SupportsAfter;
        if (!after.IsEnabled && after.IsChecked == true)
            before.IsChecked = true;
        status.Text = suggestion.MatchCount == 1 && suggestion.MatchesSelection
            ? UiStrings.Get("StatusUnique")
            : UiStrings.Format("StatusAmbiguous", suggestion.MatchCount);
        if (suggestion.Diagnostics.Count > 0)
            status.Text += Environment.NewLine + string.Join(Environment.NewLine, suggestion.Diagnostics);
        var callbackName = string.IsNullOrWhiteSpace(callback.Text) ? "Hooks.OnHook" : callback.Text.Trim();
        var position = after.IsChecked == true ? HookPosition.After : HookPosition.Before;
        hookEditor.Text = suggestion.BuildHookCode(position, callbackName);
    }

    private static DecompilerTextEditor CreateEditor()
    {
        var editor = new DecompilerTextEditor {
            IsReadOnly = true,
            ShowLineNumbers = false,
            WordWrap = false,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        return editor;
    }

    private static void ApplySemanticColors(DecompilerTextEditor editor,
        IReadOnlyList<SourceColorSpan> colors)
    {
        if (colors.Count == 0)
            return;
        var model = new RichTextModel();
        foreach (var color in colors)
            model.SetHighlighting(color.Start, color.Length, color.Color);
        editor.TextArea.TextView.LineTransformers.Add(new RichTextColorizer(model));
    }

    private static GroupBox Group(string header, Control content, bool contentHasMargin = false)
        => new() { Header = header, Content = content, Padding = contentHasMargin ? new Thickness(0) : new Thickness(8) };

    private static void Add(Grid grid, Control control, int row, int column, int columnSpan = 1)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        Grid.SetColumnSpan(control, columnSpan);
        grid.Children.Add(control);
    }

    private static RowDefinitions Rows(params GridLength[] lengths)
    {
        var definitions = new RowDefinitions();
        foreach (var length in lengths)
            definitions.Add(new RowDefinition(length));
        return definitions;
    }
    private static ColumnDefinitions Columns(params GridLength[] lengths)
    {
        var definitions = new ColumnDefinitions();
        foreach (var length in lengths)
            definitions.Add(new ColumnDefinition(length));
        return definitions;
    }
}

internal sealed class CandidateRow
{
    public CandidateRow(HookPatternSuggestion suggestion) => Suggestion = suggestion;
    public HookPatternSuggestion Suggestion { get; }
    public HookPatternKind Kind => Suggestion.Kind;
    public override string ToString()
        => $"{Kind,-10}  {Suggestion.Location,-10}  {Suggestion.ExpressionCode}  [{Suggestion.MatchCount}]";
}

internal static class MessageDialog
{
    public static async System.Threading.Tasks.Task Show(Window? owner, string message)
    {
        var dialog = new Window {
            Title = UiStrings.Get("ErrorTitle"),
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var ok = new Button {
            Content = UiStrings.Get("Ok"),
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(18, 5),
        };
        ok.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel {
            Margin = new Thickness(16),
            Spacing = 14,
            Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, ok },
        };
        if (owner is null)
            dialog.Show();
        else
            await dialog.ShowDialog(owner);
    }
}
