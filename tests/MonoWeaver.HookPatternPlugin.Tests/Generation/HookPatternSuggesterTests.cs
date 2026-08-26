using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoWeaver.Patterns.Generation;
using Xunit;

namespace MonoWeaver.PatternTests;

public sealed class HookPatternSuggesterTests
{
    [Fact]
    public void GeneratesAndValidatesValueLambda()
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "StaticCall");

        var suggestion = Suggest(method, HookPatternKind.Value)
            .First(item => item.ExpressionCode.IndexOf("Ops.Add", StringComparison.Ordinal) >= 0);

        Assert.Contains("(int left, int right) =>", suggestion.PatternCode);
        Assert.Equal(1, suggestion.MatchCount);
        Assert.True(suggestion.MatchesSelection);
        Assert.True(suggestion.SupportsAfter);
    }

    [Fact]
    public void GeneratesAndValidatesEffectLambda()
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "VoidCall");

        var suggestion = Suggest(method, HookPatternKind.Effect)
            .First(item => item.ExpressionCode.IndexOf("ConsumeInt", StringComparison.Ordinal) >= 0);

        Assert.StartsWith("Cil.Effect(", suggestion.PatternCode);
        Assert.Equal(1, suggestion.MatchCount);
        Assert.True(suggestion.MatchesSelection);
        Assert.True(suggestion.SupportsAfter);
    }

    [Fact]
    public void ReconstructsShortCircuitConditionAndHidesAfter()
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "ShortCircuitAndCondition");

        var suggestions = Suggest(method, HookPatternKind.Condition);
        var suggestion = suggestions
            .FirstOrDefault(item => item.ExpressionCode.IndexOf("&&", StringComparison.Ordinal) >= 0);
        Assert.True(suggestion is not null,
            "Generated conditions: " + string.Join(" | ", suggestions.Select(static item => item.ExpressionCode)));

        Assert.StartsWith("Cil.Condition(", suggestion!.PatternCode);
        Assert.Equal(1, suggestion.MatchCount);
        Assert.True(suggestion.MatchesSelection);
        Assert.False(suggestion.SupportsAfter);
        Assert.Throws<InvalidOperationException>(() =>
        {
            suggestion.BuildHookCode(HookPosition.After);
        });
    }

    [Fact]
    public void RanksWholeSelectedConditionBeforeLeafExpressions()
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Condition");

        var suggestion = HookPatternSuggester.Suggest(method,
            method.Body.Instructions.Select(static instruction => instruction.Offset))[0];

        Assert.Equal(HookPatternKind.Condition, suggestion.Kind);
        Assert.Contains("Ops.CallA()", suggestion.ExpressionCode);
        Assert.Contains("value.CallB()", suggestion.ExpressionCode);
        Assert.Contains("Ops.CallD()", suggestion.ExpressionCode);
    }

    [Fact]
    public void ReconstructsWholeConditionWhenSequencePointsOmitBranchTerminators()
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "NullAndGreaterCondition");
        var selectedOffsets = method.Body.Instructions
            .Where(static instruction => instruction.OpCode.Code is Code.Ldarg_0 or Code.Ldarg_1)
            .Select(static instruction => instruction.Offset);

        var suggestion = HookPatternSuggester.Suggest(method, selectedOffsets)
            .First(item => item.Kind == HookPatternKind.Condition
                           && item.ExpressionCode.IndexOf("&&", StringComparison.Ordinal) >= 0);

        Assert.Contains("value != null", suggestion.ExpressionCode);
        Assert.Contains("count > 0", suggestion.ExpressionCode);
        Assert.Equal(1, suggestion.MatchCount);
        Assert.True(suggestion.MatchesSelection);
    }

    [Fact]
    public void FullHookSnippetUsesSafeSingleAndVerification()
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "StaticCall");
        var suggestion = Suggest(method, HookPatternKind.Value)
            .First(item => item.MatchCount == 1 && item.MatchesSelection);

        var code = suggestion.BuildHookCode(HookPosition.Before, "Hooks.BeforeCall");

        Assert.Contains("il.Method.Match(pattern).Single()", code);
        Assert.Contains("match.Before((Action)Hooks.BeforeCall)", code);
        Assert.Contains("Apply(VerifyOptions.Full)", code);
    }

    [Fact]
    public void SelectionAnalysisAcceptsOneCompleteExpressionRoot()
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "StaticCall");
        var root = Suggest(method, HookPatternKind.Value)
            .First(item => item.ExpressionCode.IndexOf("Ops.Add", StringComparison.Ordinal) >= 0);

        var analysis = HookPatternSuggester.AnalyzeSelection(method, root.InstructionOffsets);

        Assert.Equal(HookPatternSelectionStatus.Success, analysis.Status);
        Assert.Equal(root.PatternCode, analysis.Suggestions[0].PatternCode);
    }

    [Fact]
    public void SelectionAnalysisRejectsTwoIndependentExpressionRoots()
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "Ambiguous");
        var calls = method.Body.Instructions
            .Where(static instruction => instruction.OpCode.Code == Code.Callvirt)
            .Select(static instruction => instruction.Offset)
            .ToArray();
        Assert.True(calls.Length >= 2);

        var analysis = HookPatternSuggester.AnalyzeSelection(method, calls);

        Assert.Equal(HookPatternSelectionStatus.RequiresMultiplePatterns, analysis.Status);
    }

    [Fact]
    public void SelectionAnalysisRejectsUnsupportedNestedConditionalValue()
    {
        using var module = PatternTestSupport.OpenFixtureModule();
        var method = PatternTestSupport.FixtureMethod(module, "NestedConditionalValue");

        var analysis = HookPatternSuggester.AnalyzeSelection(method,
            method.Body.Instructions.Select(static instruction => instruction.Offset));

        Assert.Equal(HookPatternSelectionStatus.RequiresMultiplePatterns, analysis.Status);
    }

    private static HookPatternSuggestion[] Suggest(MethodDefinition method, HookPatternKind kind)
        => HookPatternSuggester.Suggest(method,
                method.Body.Instructions.Select(static instruction => instruction.Offset))
            .Where(item => item.Kind == kind)
            .ToArray();
}
