using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoWeaver.CFG;

namespace MonoWeaver.Patterns.Generation;

/// <summary>可由交互式工具生成的 pattern 语义类型。</summary>
public enum HookPatternKind
{
    Value,
    Effect,
    Condition,
}

/// <summary>Hook 相对匹配表达式的位置。</summary>
public enum HookPosition
{
    Before,
    After,
}

/// <summary>从一组 IL offset 反向生成 HookPattern 时的选项。</summary>
public sealed class HookPatternSuggestionOptions
{
    /// <summary>最多返回多少个候选。</summary>
    public int MaxSuggestions { get; set; } = 30;

    /// <summary>是否返回只与选择范围部分相交的子表达式。</summary>
    public bool IncludePartialExpressions { get; set; } = true;
}

/// <summary>选区是否能由单个 HookPattern 完整表达。</summary>
public enum HookPatternSelectionStatus
{
    Success,
    NoSuggestions,
    RequiresMultiplePatterns,
}

/// <summary>对完整 IL 选区的生成与覆盖分析结果。</summary>
public sealed class HookPatternSelectionAnalysis
{
    internal HookPatternSelectionAnalysis(HookPatternSelectionStatus status,
        IReadOnlyList<HookPatternSuggestion> suggestions,
        IReadOnlyList<int> semanticInstructionOffsets)
    {
        Status = status;
        Suggestions = suggestions;
        SemanticInstructionOffsets = semanticInstructionOffsets;
    }

    public HookPatternSelectionStatus Status { get; }
    public IReadOnlyList<HookPatternSuggestion> Suggestions { get; }
    public IReadOnlyList<int> SemanticInstructionOffsets { get; }
}

/// <summary>一个经过回匹配验证、可展示给用户的 HookPattern 候选。</summary>
public sealed class HookPatternSuggestion
{
    internal HookPatternSuggestion(HookPatternKind kind, string expressionCode,
        string patternCode, IReadOnlyList<int> instructionOffsets, int anchorOffset,
        int score, int matchCount, bool matchesSelection, bool supportsAfter,
        IReadOnlyList<string> diagnostics)
    {
        Kind = kind;
        ExpressionCode = expressionCode;
        PatternCode = patternCode;
        InstructionOffsets = instructionOffsets;
        AnchorOffset = anchorOffset;
        Score = score;
        MatchCount = matchCount;
        MatchesSelection = matchesSelection;
        SupportsAfter = supportsAfter;
        Diagnostics = diagnostics;
    }

    public HookPatternKind Kind { get; }
    public string ExpressionCode { get; }
    public string PatternCode { get; }
    public IReadOnlyList<int> InstructionOffsets { get; }
    public int AnchorOffset { get; }
    public int Score { get; }
    public int MatchCount { get; }
    public bool MatchesSelection { get; }
    public bool SupportsBefore => true;
    public bool SupportsAfter { get; }
    public IReadOnlyList<string> Diagnostics { get; }

    public string Location => $"IL_{AnchorOffset:X4}";

    /// <summary>生成可直接放进 MonoMod ILContext handler 的代码片段。</summary>
    public string BuildHookCode(HookPosition position, string callback = "Hooks.OnHook")
    {
        if (string.IsNullOrWhiteSpace(callback))
            throw new ArgumentException("A callback expression is required.", nameof(callback));
        if (position == HookPosition.After && !SupportsAfter)
            throw new InvalidOperationException($"{Kind} patterns do not support an After hook at this site.");

        var operation = position == HookPosition.Before ? "Before" : "After";
        var warning = MatchCount == 1 && MatchesSelection
            ? string.Empty
            : $"// WARNING: generated pattern matched {MatchCount} location(s); add surrounding context before shipping.{Environment.NewLine}";
        return warning +
               $"var pattern = {PatternCode};{Environment.NewLine}{Environment.NewLine}" +
               "var match = il.Method.Match(pattern).Single();" + Environment.NewLine +
               $"match.{operation}((Action){callback})" + Environment.NewLine +
               "     .Apply(VerifyOptions.Full);";
    }
}

/// <summary>
/// 把 dnSpy/ILSpy 等宿主提供的 IL offset 选择反向映射为 MonoWeaver lambda pattern。
/// 反编译文本只负责定位；pattern 始终由 MethodModel 的 IL 语义生成。
/// </summary>
public static class HookPatternSuggester
{
    public static IReadOnlyList<HookPatternSuggestion> Suggest(MethodDefinition method,
        IEnumerable<int> selectedOffsets, HookPatternSuggestionOptions? options = null)
        => BuildSuggestions(method, selectedOffsets, options).Suggestions;

    /// <summary>
    /// 生成候选并验证选区内的全部语义 IL 是否能被同一个 Pattern 指令闭包覆盖。
    /// </summary>
    public static HookPatternSelectionAnalysis AnalyzeSelection(MethodDefinition method,
        IEnumerable<int> selectedOffsets, HookPatternSuggestionOptions? options = null)
    {
        var generated = BuildSuggestions(method, selectedOffsets, options);
        if (generated.Suggestions.Count == 0)
        {
            return new HookPatternSelectionAnalysis(HookPatternSelectionStatus.NoSuggestions,
                generated.Suggestions, generated.SemanticInstructionOffsets);
        }

        var semanticSelection = new HashSet<int>(generated.SemanticInstructionOffsets);
        var complete = generated.Suggestions.Where(suggestion =>
            semanticSelection.IsSubsetOf(suggestion.InstructionOffsets)).ToArray();
        if (complete.Length == 0)
        {
            return new HookPatternSelectionAnalysis(
                HookPatternSelectionStatus.RequiresMultiplePatterns,
                generated.Suggestions, generated.SemanticInstructionOffsets);
        }

        var completeSet = new HashSet<HookPatternSuggestion>(complete);
        var ordered = complete.Concat(generated.Suggestions.Where(suggestion =>
            !completeSet.Contains(suggestion))).ToArray();
        return new HookPatternSelectionAnalysis(HookPatternSelectionStatus.Success,
            ordered, generated.SemanticInstructionOffsets);
    }

    private static GeneratedSuggestions BuildSuggestions(MethodDefinition method,
        IEnumerable<int> selectedOffsets, HookPatternSuggestionOptions? options)
    {
        if (method is null)
            throw new ArgumentNullException(nameof(method));
        if (selectedOffsets is null)
            throw new ArgumentNullException(nameof(selectedOffsets));

        options ??= new HookPatternSuggestionOptions();
        if (options.MaxSuggestions <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaxSuggestions));

        var selection = new HashSet<int>(selectedOffsets);
        if (selection.Count == 0)
            return new GeneratedSuggestions(Array.Empty<HookPatternSuggestion>(), Array.Empty<int>());

        var model = MethodModel.Create(method);
        var semanticSelection = CollectSemanticSelection(model, selection);
        var candidates = new List<SuggestionCandidate>();

        foreach (var node in model.ValueCandidates.Distinct(ReferenceEqualityComparer<TargetExpressionNode>.Instance))
            AddExpressionCandidate(candidates, HookPatternKind.Value, node, node.ProducerInstruction,
                terminal: null, selection, options.IncludePartialExpressions);

        foreach (var effect in model.EffectCandidates)
            AddExpressionCandidate(candidates, HookPatternKind.Effect, effect.Expression,
                effect.TerminalInstruction, effect.TerminalInstruction, selection,
                options.IncludePartialExpressions);

        AddConditionCandidates(candidates, model, selection);

        var result = candidates
            .Select(candidate => CreateSuggestion(method, model, selection, candidate))
            .Where(static suggestion => suggestion is not null)
            .Cast<HookPatternSuggestion>()
            .GroupBy(static suggestion => suggestion.Kind + "\n" + suggestion.PatternCode,
                StringComparer.Ordinal)
            .Select(static group => group.OrderByDescending(item => item.Score).First())
            .OrderByDescending(static suggestion => suggestion.MatchesSelection)
            .ThenBy(static suggestion => suggestion.MatchCount == 1 ? 0 : 1)
            .ThenByDescending(static suggestion => suggestion.Score)
            .ThenBy(static suggestion => suggestion.InstructionOffsets.Count)
            .Take(options.MaxSuggestions)
            .ToArray();

        return new GeneratedSuggestions(result, semanticSelection.OrderBy(static offset => offset).ToArray());
    }

    private static HashSet<int> CollectSemanticSelection(MethodModel model, HashSet<int> selection)
    {
        var result = new HashSet<int>();

        foreach (var node in model.ValueCandidates)
            AddSelected(CollectInstructions(node));

        foreach (var effect in model.EffectCandidates)
        {
            var offsets = CollectInstructions(effect.Expression);
            offsets.Add(effect.TerminalInstruction.Offset);
            AddSelected(offsets);
        }

        foreach (var block in model.Blocks)
        {
            if (!model.TryGetConditionExpression(block, out var expression))
                continue;
            var offsets = CollectInstructions(expression);
            offsets.Add(block.Terminator.Offset);
            AddSelected(offsets);
        }

        return result;

        void AddSelected(IEnumerable<int> offsets)
        {
            foreach (var offset in offsets)
            {
                if (selection.Contains(offset))
                    result.Add(offset);
            }
        }
    }

    private static void AddExpressionCandidate(List<SuggestionCandidate> candidates,
        HookPatternKind kind, TargetExpressionNode node, Instruction anchor,
        Instruction? terminal, HashSet<int> selection, bool includePartial)
    {
        if (ContainsUnsupportedNode(node))
            return;

        var instructions = CollectInstructions(node);
        if (terminal is not null)
            instructions.Add(terminal.Offset);
        var overlap = instructions.Count(selection.Contains);
        if (overlap == 0)
            return;
        if (!includePartial && instructions.Any(offset => !selection.Contains(offset)))
            return;

        candidates.Add(new SuggestionCandidate(kind, node, null, anchor.Offset,
            instructions, Score(selection, instructions, anchor.Offset)));
    }

    private static void AddConditionCandidates(List<SuggestionCandidate> candidates,
        MethodModel model, HashSet<int> selection)
    {
        var selectedBlocks = model.Blocks
            .Where(block => model.TryGetConditionExpression(block, out var expression)
                            && (selection.Contains(block.Terminator.Offset)
                                || CollectInstructions(expression).Any(selection.Contains)))
            .ToArray();
        if (selectedBlocks.Length == 0)
            return;

        var selectedSet = new HashSet<BasicBlock>(selectedBlocks);
        foreach (var entry in selectedBlocks.OrderBy(block => block.StartIndex))
        {
            if (TryBuildCondition(model, entry, selectedSet, new HashSet<BasicBlock>(), out var built))
            {
                var pattern = Lower(built.Expression, new LambdaWriteContext(built.Expression));
                if (pattern is not null)
                {
                    var conditionMatcher = new ConditionPatternMatcher(model, new PatternOptions());
                    if (conditionMatcher.TryMatch(pattern, entry, new MatchContext(), out var fragment))
                    {
                        fragment.AnalyzeRewriteSafety();
                        var offsets = new HashSet<int>(built.Offsets);
                        foreach (var block in fragment.Blocks)
                            offsets.Add(block.Terminator.Offset);
                        candidates.Add(new SuggestionCandidate(HookPatternKind.Condition,
                            built.Expression, entry, entry.Terminator.Offset, offsets,
                            Score(selection, offsets, entry.Terminator.Offset) + fragment.Blocks.Count * 20));
                    }
                }
            }

            if (!model.TryGetConditionExpression(entry, out var leaf) || ContainsUnsupportedNode(leaf))
                continue;
            leaf = NormalizeConditionExpression(leaf, model.Method.Module.TypeSystem.Boolean);
            var leafOffsets = CollectInstructions(leaf);
            leafOffsets.Add(entry.Terminator.Offset);
            candidates.Add(new SuggestionCandidate(HookPatternKind.Condition, leaf, entry,
                entry.Terminator.Offset, leafOffsets,
                Score(selection, leafOffsets, entry.Terminator.Offset)));
        }
    }

    private static bool TryBuildCondition(MethodModel model, BasicBlock block,
        HashSet<BasicBlock> selected, HashSet<BasicBlock> visiting, out BuiltCondition result)
    {
        result = null!;
        if (!selected.Contains(block) || !visiting.Add(block)
            || !model.TryGetConditionExpression(block, out var expression)
            || ContainsUnsupportedNode(expression))
        {
            return false;
        }

        expression = NormalizeConditionExpression(expression, model.Method.Module.TypeSystem.Boolean);

        var trueEdge = block.Successors.SingleOrDefault(static edge => edge.Kind == ControlFlowEdgeKind.True);
        var falseEdge = block.Successors.SingleOrDefault(static edge => edge.Kind == ControlFlowEdgeKind.False);
        if (trueEdge is null || falseEdge is null)
            return false;

        var trueTarget = model.ResolveTransparentTarget(trueEdge.To, enabled: true);
        var falseTarget = model.ResolveTransparentTarget(falseEdge.To, enabled: true);
        var trueInside = selected.Contains(trueTarget);
        var falseInside = selected.Contains(falseTarget);
        var offsets = CollectInstructions(expression);
        offsets.Add(block.Terminator.Offset);

        if (!trueInside && !falseInside)
        {
            result = new BuiltCondition(expression, trueTarget, falseTarget, offsets);
            return true;
        }

        if (trueInside && !falseInside
            && TryBuildCondition(model, trueTarget, selected, visiting, out var rightAnd))
        {
            if (!ReferenceEquals(rightAnd.FalseContinuation, falseTarget)
                && ReferenceEquals(rightAnd.TrueContinuation, falseTarget))
            {
                rightAnd = Negate(rightAnd, model.Method.Module.TypeSystem.Boolean);
            }
            if (!ReferenceEquals(rightAnd.FalseContinuation, falseTarget))
                return false;

            offsets.UnionWith(rightAnd.Offsets);
            result = new BuiltCondition(new TargetBinaryNode(ExpressionType.AndAlso,
                    expression, rightAnd.Expression, model.Method.Module.TypeSystem.Boolean,
                    rightAnd.Expression.ProducerInstruction),
                rightAnd.TrueContinuation, falseTarget, offsets);
            return true;
        }

        if (falseInside && !trueInside
            && TryBuildCondition(model, falseTarget, selected, visiting, out var rightOr))
        {
            if (!ReferenceEquals(rightOr.TrueContinuation, trueTarget)
                && ReferenceEquals(rightOr.FalseContinuation, trueTarget))
            {
                rightOr = Negate(rightOr, model.Method.Module.TypeSystem.Boolean);
            }
            if (!ReferenceEquals(rightOr.TrueContinuation, trueTarget))
                return false;

            offsets.UnionWith(rightOr.Offsets);
            result = new BuiltCondition(new TargetBinaryNode(ExpressionType.OrElse,
                    expression, rightOr.Expression, model.Method.Module.TypeSystem.Boolean,
                    rightOr.Expression.ProducerInstruction),
                trueTarget, rightOr.FalseContinuation, offsets);
            return true;
        }

        return false;
    }

    private static BuiltCondition Negate(BuiltCondition condition, TypeReference booleanType)
        => new(InvertCondition(condition.Expression, booleanType),
            condition.FalseContinuation, condition.TrueContinuation, condition.Offsets);

    private static TargetExpressionNode InvertCondition(TargetExpressionNode expression,
        TypeReference booleanType)
    {
        if (expression is TargetUnaryNode { Operation: ExpressionType.Not } unary)
            return unary.Operand;

        if (expression is TargetBinaryNode binary)
        {
            var operation = binary.Operation switch
            {
                ExpressionType.Equal => ExpressionType.NotEqual,
                ExpressionType.NotEqual => ExpressionType.Equal,
                ExpressionType.GreaterThan => ExpressionType.LessThanOrEqual,
                ExpressionType.GreaterThanOrEqual => ExpressionType.LessThan,
                ExpressionType.LessThan => ExpressionType.GreaterThanOrEqual,
                ExpressionType.LessThanOrEqual => ExpressionType.GreaterThan,
                _ => (ExpressionType?)null,
            };
            if (operation is not null)
            {
                return new TargetBinaryNode(operation.Value, binary.Left, binary.Right,
                    booleanType, binary.ProducerInstruction);
            }
        }

        return new TargetUnaryNode(ExpressionType.Not, expression, booleanType,
            expression.ProducerInstruction);
    }

    private static TargetExpressionNode NormalizeConditionExpression(TargetExpressionNode expression,
        TypeReference booleanType)
    {
        if (expression.ResultType?.MetadataType == MetadataType.Boolean)
            return expression;

        var nominalType = expression.ResultType;
        var zero = nominalType is not null && nominalType.IsValueType ? (object)0 : null;
        var constant = new TargetConstantNode(zero, nominalType, expression.ProducerInstruction);
        return new TargetBinaryNode(ExpressionType.NotEqual, expression, constant,
            booleanType, expression.ProducerInstruction);
    }

    private static HookPatternSuggestion? CreateSuggestion(MethodDefinition method, MethodModel model,
        HashSet<int> selection, SuggestionCandidate candidate)
    {
        var context = new LambdaWriteContext(candidate.Node);
        if (!TargetCodeWriter.TryWrite(candidate.Node, context, out var expression))
            return null;
        var root = Lower(candidate.Node, context);
        if (root is null)
            return null;

        var lambda = context.FormatLambda(expression);
        var patternCode = $"Cil.{candidate.Kind}({lambda})";
        var matchCount = 0;
        var matchesSelection = false;
        var supportsAfter = candidate.Kind != HookPatternKind.Condition;

        try
        {
            switch (candidate.Kind)
            {
                case HookPatternKind.Value:
                {
                    var matches = PatternMatcher.For(method).Find(new ValuePattern(root, null));
                    matchCount = matches.Count;
                    matchesSelection = matches.Any(match =>
                        match.DefinitionInstruction.Offset == candidate.AnchorOffset
                        || match.ResultInstruction.Offset == candidate.AnchorOffset);
                    var selected = matches.FirstOrDefault(match =>
                        match.DefinitionInstruction.Offset == candidate.AnchorOffset
                        || match.ResultInstruction.Offset == candidate.AnchorOffset);
                    supportsAfter = selected is null || !selected.IsAddressBacked;
                    break;
                }
                case HookPatternKind.Effect:
                {
                    var matches = PatternMatcher.For(method).Find(new EffectPattern(root, null));
                    matchCount = matches.Count;
                    matchesSelection = matches.Any(match => match.LastInstruction.Offset == candidate.AnchorOffset);
                    break;
                }
                case HookPatternKind.Condition:
                {
                    var matches = PatternMatcher.For(method).Find(new ConditionPattern(root, null));
                    matchCount = matches.Count;
                    matchesSelection = matches.Any(match => match.Fragment.Blocks.Any(block =>
                        block.Terminator.Offset == candidate.AnchorOffset));
                    supportsAfter = false;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            context.Diagnostics.Add("Round-trip validation failed: " + ex.Message);
        }

        var score = candidate.Score + (matchesSelection ? 500 : 0) + (matchCount == 1 ? 250 : 0);
        return new HookPatternSuggestion(candidate.Kind, expression, patternCode,
            candidate.Offsets.OrderBy(static offset => offset).ToArray(), candidate.AnchorOffset,
            score, matchCount, matchesSelection, supportsAfter, context.Diagnostics.ToArray());
    }

    private static PatternNode? Lower(TargetExpressionNode node, LambdaWriteContext context)
    {
        if (node is TargetAddressNode address)
            return Lower(address.Target, context);

        var resultType = node.ResultType is null ? CilTypeSpec.Void : CilTypeSpec.From(node.ResultType);
        switch (node)
        {
            case TargetArgumentNode argument:
                if (context.TryGetParameter(argument, out var parameter))
                    return new LambdaParameterPatternNode(parameter.Name, CilTypeSpec.From(argument.ResultType!));
                return new ArgumentPatternNode(argument.IsThis, argument.IsThis ? null : argument.ParameterIndex,
                    null, CilTypeSpec.From(argument.ResultType!));
            case TargetLocalReadNode local:
                return new LocalPatternNode(local.Variable.Index, null, CilTypeSpec.From(local.Variable.VariableType));
            case TargetConstantNode constant:
                return new ConstantPatternNode(constant.Value, resultType);
            case TargetFieldNode field:
                return new FieldPatternNode(CilFieldSpec.From(field.Field),
                    field.Instance is null ? null : Lower(field.Instance, context));
            case TargetFieldStoreNode store:
            {
                var value = Lower(store.Value, context);
                var instance = store.Instance is null ? null : Lower(store.Instance, context);
                return value is null || store.Instance is not null && instance is null
                    ? null
                    : new FieldStorePatternNode(CilFieldSpec.From(store.Field), instance, value);
            }
            case TargetCallNode call:
            {
                var instance = call.Instance is null ? null : Lower(call.Instance, context);
                var arguments = call.Arguments.Select(argument => Lower(argument, context)).ToArray();
                if (call.Instance is not null && instance is null || arguments.Any(static argument => argument is null))
                    return null;
                return new CallPatternNode(CilMethodSpec.From(call.Method), instance,
                    arguments.Cast<PatternNode>().ToArray(), resultType);
            }
            case TargetNewArrayNode array:
            {
                var lengths = array.Lengths.Select(length => Lower(length, context)).ToArray();
                return lengths.Any(static length => length is null)
                    ? null
                    : new NewArrayPatternNode(CilTypeSpec.From(array.ElementType),
                        lengths.Cast<PatternNode>().ToArray(), resultType);
            }
            case TargetArrayElementNode element:
            {
                var array = Lower(element.Array, context);
                var index = Lower(element.Index, context);
                return array is null || index is null ? null : new ArrayElementPatternNode(array, index, resultType);
            }
            case TargetArrayLengthNode length:
            {
                var array = Lower(length.Array, context);
                return array is null ? null : new ArrayLengthPatternNode(array, resultType);
            }
            case TargetArrayStoreNode store:
            {
                var array = Lower(store.Array, context);
                var index = Lower(store.Index, context);
                var value = Lower(store.Value, context);
                return array is null || index is null || value is null
                    ? null
                    : new ArrayStorePatternNode(array, index, value);
            }
            case TargetUnaryNode unary:
            {
                var operand = Lower(unary.Operand, context);
                return operand is null ? null : new UnaryPatternNode(unary.Operation, operand, null, resultType);
            }
            case TargetBinaryNode binary:
            {
                var left = Lower(binary.Left, context);
                var right = Lower(binary.Right, context);
                return left is null || right is null
                    ? null
                    : new BinaryPatternNode(binary.Operation, left, right, null, resultType);
            }
            default:
                return null;
        }
    }

    private static bool ContainsUnsupportedNode(TargetExpressionNode node)
    {
        if (node is TargetUnknownNode or TargetOperationNode)
            return true;
        foreach (var child in Children(node))
        {
            if (ContainsUnsupportedNode(child))
                return true;
        }
        return false;
    }

    private static HashSet<int> CollectInstructions(TargetExpressionNode node)
    {
        var offsets = new HashSet<int>();
        Collect(node, offsets, new HashSet<TargetExpressionNode>(ReferenceEqualityComparer<TargetExpressionNode>.Instance));
        return offsets;

        static void Collect(TargetExpressionNode current, HashSet<int> result,
            HashSet<TargetExpressionNode> visited)
        {
            if (!visited.Add(current))
                return;
            result.Add(current.ProducerInstruction.Offset);
            foreach (var child in Children(current))
                Collect(child, result, visited);
        }
    }

    internal static IEnumerable<TargetExpressionNode> Children(TargetExpressionNode node)
    {
        switch (node)
        {
            case TargetAddressNode address:
                yield return address.Target;
                break;
            case TargetFieldNode field when field.Instance is not null:
                yield return field.Instance;
                break;
            case TargetFieldStoreNode store:
                if (store.Instance is not null)
                    yield return store.Instance;
                yield return store.Value;
                break;
            case TargetCallNode call:
                if (call.Instance is not null)
                    yield return call.Instance;
                foreach (var argument in call.Arguments)
                    yield return argument;
                break;
            case TargetNewArrayNode array:
                foreach (var length in array.Lengths)
                    yield return length;
                break;
            case TargetArrayElementNode element:
                yield return element.Array;
                yield return element.Index;
                break;
            case TargetArrayLengthNode length:
                yield return length.Array;
                break;
            case TargetArrayStoreNode store:
                yield return store.Array;
                yield return store.Index;
                yield return store.Value;
                break;
            case TargetUnaryNode unary:
                yield return unary.Operand;
                break;
            case TargetBinaryNode binary:
                yield return binary.Left;
                yield return binary.Right;
                break;
            case TargetOperationNode operation:
                foreach (var input in operation.Inputs)
                    yield return input;
                break;
        }
    }

    private static int Score(HashSet<int> selection, HashSet<int> candidate, int anchor)
    {
        var overlap = candidate.Count(selection.Contains);
        var score = overlap * 30;
        if (selection.Contains(anchor))
            score += 300;
        if (candidate.All(selection.Contains))
            score += 200;
        if (selection.All(candidate.Contains))
            score += 100;
        return score - Math.Abs(candidate.Count - selection.Count);
    }

    private sealed class GeneratedSuggestions
    {
        public GeneratedSuggestions(IReadOnlyList<HookPatternSuggestion> suggestions,
            IReadOnlyList<int> semanticInstructionOffsets)
        {
            Suggestions = suggestions;
            SemanticInstructionOffsets = semanticInstructionOffsets;
        }

        public IReadOnlyList<HookPatternSuggestion> Suggestions { get; }
        public IReadOnlyList<int> SemanticInstructionOffsets { get; }
    }

    private sealed class SuggestionCandidate
    {
        public SuggestionCandidate(HookPatternKind kind, TargetExpressionNode node,
            BasicBlock? conditionEntry, int anchorOffset, HashSet<int> offsets, int score)
        {
            Kind = kind;
            Node = node;
            ConditionEntry = conditionEntry;
            AnchorOffset = anchorOffset;
            Offsets = offsets;
            Score = score;
        }

        public HookPatternKind Kind { get; }
        public TargetExpressionNode Node { get; }
        public BasicBlock? ConditionEntry { get; }
        public int AnchorOffset { get; }
        public HashSet<int> Offsets { get; }
        public int Score { get; }
    }

    private sealed class BuiltCondition
    {
        public BuiltCondition(TargetExpressionNode expression, BasicBlock trueContinuation,
            BasicBlock falseContinuation, HashSet<int> offsets)
        {
            Expression = expression;
            TrueContinuation = trueContinuation;
            FalseContinuation = falseContinuation;
            Offsets = offsets;
        }

        public TargetExpressionNode Expression { get; }
        public BasicBlock TrueContinuation { get; }
        public BasicBlock FalseContinuation { get; }
        public HashSet<int> Offsets { get; }
    }
}

internal sealed class LambdaParameterInfo
{
    public LambdaParameterInfo(bool isThis, int index, string name, TypeReference type)
    {
        IsThis = isThis;
        Index = index;
        Name = name;
        Type = type;
    }

    public bool IsThis { get; }
    public int Index { get; }
    public string Name { get; }
    public TypeReference Type { get; }
}

internal sealed class LambdaWriteContext
{
    private readonly Dictionary<string, LambdaParameterInfo> _parameters = new(StringComparer.Ordinal);

    public LambdaWriteContext(TargetExpressionNode root)
    {
        var arguments = Enumerate(root)
            .OfType<TargetArgumentNode>()
            .GroupBy(static argument => argument.IsThis ? "this" : "arg:" + argument.ParameterIndex,
                StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToArray();

        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var argument in arguments)
        {
            var rawName = argument.IsThis ? "__this" : argument.Parameter?.Name;
            if (string.IsNullOrWhiteSpace(rawName) || !CSharpNames.IsIdentifier(rawName!)
                || argument.ResultType is null || argument.ResultType is ByReferenceType or PointerType)
            {
                continue;
            }

            var name = CSharpNames.EscapeIdentifier(rawName!);
            if (!usedNames.Add(name))
                continue;
            var key = Key(argument);
            _parameters[key] = new LambdaParameterInfo(argument.IsThis,
                argument.ParameterIndex, name, argument.ResultType);
        }
    }

    public List<string> Diagnostics { get; } = new();

    public bool TryGetParameter(TargetArgumentNode argument, out LambdaParameterInfo parameter)
        => _parameters.TryGetValue(Key(argument), out parameter!);

    public string FormatLambda(string expression)
    {
        var parameters = _parameters.Values
            .OrderBy(static parameter => parameter.IsThis ? -1 : parameter.Index)
            .Select(parameter => CSharpNames.TypeName(parameter.Type) + " " + parameter.Name);
        return "(" + string.Join(", ", parameters) + ") => " + expression;
    }

    private static string Key(TargetArgumentNode argument)
        => argument.IsThis ? "this" : "arg:" + argument.ParameterIndex;

    private static IEnumerable<TargetExpressionNode> Enumerate(TargetExpressionNode root)
    {
        var stack = new Stack<TargetExpressionNode>();
        var seen = new HashSet<TargetExpressionNode>(ReferenceEqualityComparer<TargetExpressionNode>.Instance);
        stack.Push(root);
        while (stack.Count != 0)
        {
            var node = stack.Pop();
            if (!seen.Add(node))
                continue;
            yield return node;
            foreach (var child in HookPatternSuggester.Children(node))
                stack.Push(child);
        }
    }
}

internal static class TargetCodeWriter
{
    public static bool TryWrite(TargetExpressionNode node, LambdaWriteContext context, out string code)
    {
        try
        {
            code = Write(node, context, 0);
            return true;
        }
        catch (NotSupportedException ex)
        {
            context.Diagnostics.Add(ex.Message);
            code = string.Empty;
            return false;
        }
    }

    private static string Write(TargetExpressionNode node, LambdaWriteContext context, int parentPrecedence)
    {
        if (node is TargetAddressNode address)
            return Write(address.Target, context, parentPrecedence);

        switch (node)
        {
            case TargetArgumentNode argument:
                if (context.TryGetParameter(argument, out var parameter))
                    return parameter.Name;
                var argumentType = RequireType(argument.ResultType, "argument");
                return argument.IsThis
                    ? $"P.This<{CSharpNames.TypeName(argumentType)}>()"
                    : $"P.Arg<{CSharpNames.TypeName(argumentType)}>({argument.ParameterIndex})";
            case TargetLocalReadNode local:
                return $"P.Local<{CSharpNames.TypeName(local.Variable.VariableType)}>({local.Variable.Index})";
            case TargetConstantNode constant:
                return Constant(constant.Value, constant.ResultType);
            case TargetFieldNode field:
                CheckAccessible(field.Field, context);
                return field.Instance is null
                    ? CSharpNames.TypeName(field.Field.DeclaringType) + "." + CSharpNames.MemberName(field.Field.Name)
                    : Parenthesize(Write(field.Instance, context, 100), 100, 100) + "." + CSharpNames.MemberName(field.Field.Name);
            case TargetFieldStoreNode store:
            {
                CheckAccessible(store.Field, context);
                var field = store.Instance is null
                    ? CSharpNames.TypeName(store.Field.DeclaringType) + "." + CSharpNames.MemberName(store.Field.Name)
                    : Write(store.Instance, context, 100) + "." + CSharpNames.MemberName(store.Field.Name);
                return $"P.StoreField({field}, {Write(store.Value, context, 0)})";
            }
            case TargetCallNode call:
                return WriteCall(call, context);
            case TargetNewArrayNode array:
                if (array.Lengths.Count != 1)
                    throw new NotSupportedException("Only one-dimensional array creation can be generated as a lambda pattern.");
                return $"new {CSharpNames.TypeName(array.ElementType)}[{Write(array.Lengths[0], context, 0)}]";
            case TargetArrayElementNode element:
                return Write(element.Array, context, 100) + "[" + Write(element.Index, context, 0) + "]";
            case TargetArrayLengthNode length:
                return Write(length.Array, context, 100) + ".Length";
            case TargetArrayStoreNode store:
                return $"P.StoreElement({Write(store.Array, context, 0)}, {Write(store.Index, context, 0)}, {Write(store.Value, context, 0)})";
            case TargetUnaryNode unary:
                return WriteUnary(unary, context, parentPrecedence);
            case TargetBinaryNode binary:
                return WriteBinary(binary, context, parentPrecedence);
            case TargetUnknownNode unknown:
                throw new NotSupportedException("The selected IL contains an unknown expression: " + unknown.Reason);
            case TargetOperationNode operation:
                throw new NotSupportedException($"The selected IL operation '{operation.Code}' is not supported by the lambda generator.");
            default:
                throw new NotSupportedException($"The selected expression node '{node.GetType().Name}' is not supported by the lambda generator.");
        }
    }

    private static string WriteCall(TargetCallNode call, LambdaWriteContext context)
    {
        CheckAccessible(call.Method, context);
        var arguments = string.Join(", ", call.Arguments.Select(argument => Write(argument, context, 0)));
        if (call.Method.Name == ".ctor")
            return $"new {CSharpNames.TypeName(call.Method.DeclaringType)}({arguments})";

        if (call.Method.Name.StartsWith("get_", StringComparison.Ordinal)
            && call.Arguments.Count == 0)
        {
            var property = CSharpNames.MemberName(call.Method.Name.Substring(4));
            return call.Instance is null
                ? CSharpNames.TypeName(call.Method.DeclaringType) + "." + property
                : Write(call.Instance, context, 100) + "." + property;
        }

        var methodName = CSharpNames.MemberName(RemoveGenericArity(call.Method.Name));
        if (call.Method is GenericInstanceMethod generic && generic.GenericArguments.Count != 0)
        {
            methodName += "<" + string.Join(", ",
                generic.GenericArguments.Select(CSharpNames.TypeName)) + ">";
        }

        return call.Instance is null
            ? CSharpNames.TypeName(call.Method.DeclaringType) + "." + methodName + "(" + arguments + ")"
            : Write(call.Instance, context, 100) + "." + methodName + "(" + arguments + ")";
    }

    private static string WriteUnary(TargetUnaryNode unary, LambdaWriteContext context, int parentPrecedence)
    {
        string text;
        const int precedence = 80;
        switch (unary.Operation)
        {
            case ExpressionType.Not:
                text = "!" + Write(unary.Operand, context, precedence);
                break;
            case ExpressionType.Negate:
                text = "-" + Write(unary.Operand, context, precedence);
                break;
            case ExpressionType.NegateChecked:
                text = "checked(-" + Write(unary.Operand, context, 0) + ")";
                break;
            case ExpressionType.UnaryPlus:
                text = "+" + Write(unary.Operand, context, precedence);
                break;
            case ExpressionType.OnesComplement:
                text = "~" + Write(unary.Operand, context, precedence);
                break;
            case ExpressionType.Convert:
            case ExpressionType.ConvertChecked:
                text = (unary.Operation == ExpressionType.ConvertChecked ? "checked(" : string.Empty) +
                       "(" + CSharpNames.TypeName(RequireType(unary.ResultType, "conversion")) + ")" +
                       Write(unary.Operand, context, precedence) +
                       (unary.Operation == ExpressionType.ConvertChecked ? ")" : string.Empty);
                break;
            case ExpressionType.TypeAs:
                text = Write(unary.Operand, context, 45) + " as " +
                       CSharpNames.TypeName(RequireType(unary.ResultType, "as conversion"));
                break;
            default:
                throw new NotSupportedException($"Unary operation '{unary.Operation}' is not supported by the lambda generator.");
        }
        return Parenthesize(text, precedence, parentPrecedence);
    }

    private static string WriteBinary(TargetBinaryNode binary, LambdaWriteContext context, int parentPrecedence)
    {
        var (token, precedence, isChecked) = BinaryOperator(binary.Operation);
        var left = Write(binary.Left, context, precedence);
        var right = Write(binary.Right, context, precedence + 1);
        var text = left + " " + token + " " + right;
        if (isChecked)
            text = "checked(" + text + ")";
        return Parenthesize(text, precedence, parentPrecedence);
    }

    private static (string Token, int Precedence, bool Checked) BinaryOperator(ExpressionType operation)
        => operation switch
        {
            ExpressionType.Multiply => ("*", 70, false),
            ExpressionType.MultiplyChecked => ("*", 70, true),
            ExpressionType.Divide => ("/", 70, false),
            ExpressionType.Modulo => ("%", 70, false),
            ExpressionType.Add => ("+", 60, false),
            ExpressionType.AddChecked => ("+", 60, true),
            ExpressionType.Subtract => ("-", 60, false),
            ExpressionType.SubtractChecked => ("-", 60, true),
            ExpressionType.LeftShift => ("<<", 55, false),
            ExpressionType.RightShift => (">>", 55, false),
            ExpressionType.LessThan => ("<", 50, false),
            ExpressionType.LessThanOrEqual => ("<=", 50, false),
            ExpressionType.GreaterThan => (">", 50, false),
            ExpressionType.GreaterThanOrEqual => (">=", 50, false),
            ExpressionType.Equal => ("==", 45, false),
            ExpressionType.NotEqual => ("!=", 45, false),
            ExpressionType.And => ("&", 40, false),
            ExpressionType.ExclusiveOr => ("^", 39, false),
            ExpressionType.Or => ("|", 38, false),
            ExpressionType.AndAlso => ("&&", 30, false),
            ExpressionType.OrElse => ("||", 20, false),
            _ => throw new NotSupportedException($"Binary operation '{operation}' is not supported by the lambda generator."),
        };

    private static string Parenthesize(string text, int precedence, int parentPrecedence)
        => precedence < parentPrecedence ? "(" + text + ")" : text;

    private static string Constant(object? value, TypeReference? nominalType)
    {
        if (value is null)
            return "null";
        if (nominalType?.MetadataType == MetadataType.Boolean && value is IConvertible convertibleBoolean)
            return convertibleBoolean.ToInt32(CultureInfo.InvariantCulture) == 0 ? "false" : "true";
        return value switch
        {
            string text => "\"" + EscapeString(text) + "\"",
            char character => "'" + EscapeChar(character) + "'",
            bool boolean => boolean ? "true" : "false",
            byte number => number.ToString(CultureInfo.InvariantCulture),
            sbyte number => number.ToString(CultureInfo.InvariantCulture),
            short number => number.ToString(CultureInfo.InvariantCulture),
            ushort number => number.ToString(CultureInfo.InvariantCulture),
            int number => number.ToString(CultureInfo.InvariantCulture),
            uint number => number.ToString(CultureInfo.InvariantCulture) + "u",
            long number => number.ToString(CultureInfo.InvariantCulture) + "L",
            ulong number => number.ToString(CultureInfo.InvariantCulture) + "UL",
            float number when float.IsNaN(number) => "float.NaN",
            float number when float.IsPositiveInfinity(number) => "float.PositiveInfinity",
            float number when float.IsNegativeInfinity(number) => "float.NegativeInfinity",
            float number => number.ToString("R", CultureInfo.InvariantCulture) + "f",
            double number when double.IsNaN(number) => "double.NaN",
            double number when double.IsPositiveInfinity(number) => "double.PositiveInfinity",
            double number when double.IsNegativeInfinity(number) => "double.NegativeInfinity",
            double number => number.ToString("R", CultureInfo.InvariantCulture) + "d",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)
                 ?? throw new NotSupportedException("The selected constant cannot be represented in C#."),
        };
    }

    private static string EscapeString(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        foreach (var character in value)
            builder.Append(EscapeChar(character));
        return builder.ToString();
    }

    private static string EscapeChar(char value)
        => value switch
        {
            '\\' => "\\\\",
            '\"' => "\\\"",
            '\'' => "\\'",
            '\0' => "\\0",
            '\a' => "\\a",
            '\b' => "\\b",
            '\f' => "\\f",
            '\n' => "\\n",
            '\r' => "\\r",
            '\t' => "\\t",
            '\v' => "\\v",
            _ when char.IsControl(value) => "\\u" + ((int)value).ToString("X4", CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };

    private static void CheckAccessible(MemberReference member, LambdaWriteContext context)
    {
        try
        {
            if (member is FieldReference field && field.Resolve() is { IsPublic: false })
                context.Diagnostics.Add($"Field '{field.FullName}' is not public; the generated lambda requires a publicized reference assembly.");
            else if (member is MethodReference method && method.Resolve() is { IsPublic: false })
                context.Diagnostics.Add($"Method '{method.FullName}' is not public; the generated lambda requires a publicized reference assembly.");
        }
        catch
        {
            // Accessibility is only a source-compilation warning; inability to resolve must not block IL identity generation.
        }
    }

    private static TypeReference RequireType(TypeReference? type, string role)
        => type ?? throw new NotSupportedException($"The {role} type could not be resolved.");

    private static string RemoveGenericArity(string name)
    {
        var tick = name.IndexOf('`');
        return tick < 0 ? name : name.Substring(0, tick);
    }
}

internal static class CSharpNames
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
        "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
        "void", "volatile", "while",
    };

    public static bool IsIdentifier(string value)
    {
        if (string.IsNullOrEmpty(value))
            return false;
        var start = value[0] == '@' ? 1 : 0;
        if (start == value.Length || !IsIdentifierStart(value[start]))
            return false;
        for (var i = start + 1; i < value.Length; i++)
        {
            if (!IsIdentifierPart(value[i]))
                return false;
        }
        return true;
    }

    public static string EscapeIdentifier(string value)
        => Keywords.Contains(value) ? "@" + value : value;

    public static string MemberName(string value)
    {
        if (!IsIdentifier(value))
            throw new NotSupportedException($"Metadata name '{value}' is not a valid C# identifier.");
        return EscapeIdentifier(value);
    }

    public static string TypeName(TypeReference type)
    {
        switch (type)
        {
            case ByReferenceType byReference:
                return TypeName(byReference.ElementType);
            case PointerType pointer:
                return TypeName(pointer.ElementType) + "*";
            case ArrayType array:
                return TypeName(array.ElementType) + "[" + new string(',', Math.Max(0, array.Rank - 1)) + "]";
            case GenericParameter parameter:
                return MemberName(parameter.Name);
            case GenericInstanceType generic:
                return NamedType(generic.ElementType, generic.GenericArguments.Select(TypeName).ToArray());
        }

        return type.MetadataType switch
        {
            MetadataType.Void => "void",
            MetadataType.Boolean => "bool",
            MetadataType.Char => "char",
            MetadataType.SByte => "sbyte",
            MetadataType.Byte => "byte",
            MetadataType.Int16 => "short",
            MetadataType.UInt16 => "ushort",
            MetadataType.Int32 => "int",
            MetadataType.UInt32 => "uint",
            MetadataType.Int64 => "long",
            MetadataType.UInt64 => "ulong",
            MetadataType.Single => "float",
            MetadataType.Double => "double",
            MetadataType.String => "string",
            MetadataType.Object => "object",
            MetadataType.IntPtr => "IntPtr",
            MetadataType.UIntPtr => "UIntPtr",
            _ => NamedType(type, Array.Empty<string>()),
        };
    }

    private static string NamedType(TypeReference type, IReadOnlyList<string> genericArguments)
    {
        var names = new Stack<string>();
        TypeReference? current = type;
        while (current is not null)
        {
            var name = current.Name;
            var tick = name.IndexOf('`');
            if (tick >= 0)
                name = name.Substring(0, tick);
            names.Push(MemberName(name));
            current = current.DeclaringType;
        }

        var prefix = string.IsNullOrWhiteSpace(type.Namespace)
            ? "global::"
            : "global::" + string.Join(".", type.Namespace.Split('.').Select(MemberName)) + ".";
        var text = prefix + string.Join(".", names);
        if (genericArguments.Count != 0)
            text += "<" + string.Join(", ", genericArguments) + ">";
        return text;
    }

    private static bool IsIdentifierStart(char value) => value == '_' || char.IsLetter(value);
    private static bool IsIdentifierPart(char value) => value == '_' || char.IsLetterOrDigit(value);
}

internal sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T> where T : class
{
    public static readonly ReferenceEqualityComparer<T> Instance = new();
    public bool Equals(T? x, T? y) => ReferenceEquals(x, y);
    public int GetHashCode(T obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
}
