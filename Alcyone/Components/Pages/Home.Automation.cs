using StateMachine.Automation;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StateMachine.Components.Pages;

public partial class Home
{
    private static readonly string[] NumericRecognitionOperators = { "==", "!=", ">", ">=", "<", "<=" };
    private bool _recognitionManagerOpen;
    private string? _recognitionSelection;
    private readonly Dictionary<(string Id, string Mode, string Query), ColorProbeResult> _recognitionResults = new();
    private void OpenRecognitionManager(string? id = null)
    { _recognitionSelection = id; _recognitionManagerOpen = true; }
    private async Task RecognitionLibraryChanged() { _colorResults.Clear(); _recognitionResults.Clear(); RefreshCurrentConditionResults(); await SaveConfigCoreAsync(reportFailure: true); }
    private string RecognitionPath(RecognitionItem item) => item.ParentId is { } parent
        ? $"{Machine.Recognitions.FirstOrDefault(i => i.Id == parent)?.Name}/{item.Name}" : item.Name;
    private string RecognitionStatus(RecognitionItem item)
    {
        if (!_colorResults.TryGetValue(item.Id, out var result)) return "未检测";
        if (result.Error is not null) return $"错误：{result.Error}";
        return result.Found ? $"成功 · {result.Similarity:F1}% · ({result.X}, {result.Y})"
            + (string.IsNullOrEmpty(result.Text) ? "" : $" · {result.Text}") : $"失败 · {result.Similarity:F1}%";
    }
    private async Task TestRecognitionAsync(RecognitionItem item)
    { if (_session is not null) { await _session.ConfigureAsync(Machine); await _session.TestAsync(item); ApplyRuntimeSnapshot(); } }
    private string RecognitionConditionResult(ConditionModel condition)
    {
        var item = Machine.Recognitions.FirstOrDefault(i => !i.IsFolder && i.Id == condition.RecognitionId);
        if (item is null) return "请选择识别项目。";
        if (!_recognitionResults.TryGetValue(RecognitionScanKey(condition, item), out var result)) return "尚未检测当前条件。";
        if (result.Error is not null) return $"检测错误：{result.Error}";
        return $"识别{(result.Found ? "成功" : "失败")} · 相似度 {result.Similarity:F1}% · 坐标 ({result.X},{result.Y}) · 条件{(EvaluateRecognitionCondition(condition) ? "满足" : "不满足")}\n实际文字：{(string.IsNullOrEmpty(result.Text) ? "（空）" : result.Text)}";
    }
    private async Task TestRecognitionConditionAsync(ConditionModel condition)
    { if (_session is not null) { await _session.ConfigureAsync(Machine); await _session.TestConditionAsync(condition); ApplyRuntimeSnapshot(); } }
    private string ConditionTarget(ConditionModel condition)
    {
        if (condition.Kind == "recognition") return "recognition:" + condition.RecognitionId;
        var variable = Machine.Variables.FirstOrDefault(v => v.Name == condition.Left);
        return variable is null ? "" : "variable:" + variable.Id;
    }
    private void ChangeConditionTarget(ConditionModel condition, string target)
    {
        if (target.StartsWith("recognition:", StringComparison.Ordinal))
        {
            var item = Machine.Recognitions.FirstOrDefault(i => !i.IsFolder && i.Id == target[12..]);
            if (item is null) return;
            if (condition.Kind == "recognition") RecognitionCriteriaFor(condition);
            else condition.Recognition ??= new();
            condition.Kind = "recognition";
            condition.RecognitionId = item.Id;
            if (item.Settings.Mode == "dictionary")
            {
                condition.Recognition!.DictionaryMode ??= item.Settings.DictionaryMode;
                condition.Recognition.SearchText ??= item.Settings.Query;
            }
        }
        else if (target.StartsWith("variable:", StringComparison.Ordinal))
        {
            var variable = Machine.Variables.FirstOrDefault(v => v.Id == target[9..]);
            if (variable is null) return;
            if (condition.Kind != "variable")
            {
                condition.Operator = "==";
                condition.RightMode = "literal";
                condition.RightType = variable.Type == "json" ? "string" : variable.Type;
                condition.RightValue = condition.RightType switch
                { "boolean" => JsonValue.Create(true), "number" => JsonValue.Create(0), _ => JsonValue.Create("") };
            }
            condition.Kind = "variable";
            condition.Left = variable.Name;
        }
        RefreshCurrentConditionResults();
    }
    private RecognitionCriteria RecognitionCriteriaFor(ConditionModel condition)
    {
        if (condition.Recognition is not null) return condition.Recognition;
        var criteria = new RecognitionCriteria { Success = null };
        if (condition.RecognitionField == "status") criteria.Success = condition.ExpectedSuccess;
        else if (condition.RecognitionField == "text")
        { criteria.Text = NodeString(condition.RightValue); criteria.TextOperator = condition.Operator; }
        else if (TryNumber(Primitive(condition.RightValue), out var number))
        {
            switch (condition.RecognitionField)
            {
                case "similarity": criteria.Similarity = number; criteria.SimilarityOperator = condition.Operator; break;
                case "x": criteria.X = number; criteria.XOperator = condition.Operator; break;
                case "y": criteria.Y = number; criteria.YOperator = condition.Operator; break;
            }
        }
        return condition.Recognition = criteria;
    }
    private string RecognitionSuccessValue(ConditionModel condition) =>
        RecognitionCriteriaFor(condition).Success switch { true => "success", false => "failure", null => "ignore" };
    private void ChangeRecognitionSuccess(ConditionModel condition, string value)
    {
        RecognitionCriteriaFor(condition).Success = value switch { "success" => true, "failure" => false, _ => (bool?)null };
        RefreshCurrentConditionResults();
    }
    private bool EvaluateRecognitionCondition(ConditionModel condition) => EvaluateCondition(condition);
    private bool IsDictionaryCondition(ConditionModel condition) =>
        Machine.Recognitions.Any(i => i.Id == condition.RecognitionId && i.Settings.Mode == "dictionary");
    private string DictionaryModeFor(ConditionModel condition)
    {
        var criteria = RecognitionCriteriaFor(condition);
        return criteria.DictionaryMode ?? Machine.Recognitions.FirstOrDefault(i => !i.IsFolder && i.Id == condition.RecognitionId)?.Settings.DictionaryMode ?? "ocr";
    }
    private void ChangeDictionaryMode(ConditionModel condition, string mode)
    {
        var criteria = RecognitionCriteriaFor(condition);
        criteria.DictionaryMode = mode == "text" ? "text" : "ocr";
        criteria.SearchText ??= Machine.Recognitions.FirstOrDefault(i => !i.IsFolder && i.Id == condition.RecognitionId)?.Settings.Query ?? "";
        RefreshCurrentConditionResults();
    }
    private (string Id, string Mode, string Query) RecognitionScanKey(ConditionModel condition, RecognitionItem item)
    {
        if (item.Settings.Mode != "dictionary") return (item.Id, "feature", "");
        var criteria = RecognitionCriteriaFor(condition);
        var mode = criteria.DictionaryMode ?? item.Settings.DictionaryMode;
        var query = mode == "text" ? (criteria.SearchText ?? item.Settings.Query).Trim() : "";
        return (item.Id, mode, query);
    }
    private string RecognitionConditionText(ConditionModel condition)
    {
        var item = Machine.Recognitions.FirstOrDefault(i => !i.IsFolder && i.Id == condition.RecognitionId);
        var name = item?.Name ?? "未选择识别项";
        var criteria = RecognitionCriteriaFor(condition);
        var parts = new List<string>();
        if (item?.Settings.Mode == "dictionary")
        {
            var key = RecognitionScanKey(condition, item);
            parts.Add(key.Mode == "text" ? $"找固定文字“{key.Query}”" : "OCR 结果");
        }
        if (criteria.Success.HasValue) parts.Add(criteria.Success.Value ? "成功" : "失败");
        if (criteria.Similarity.HasValue) parts.Add($"相似度 {criteria.SimilarityOperator} {criteria.Similarity}%");
        if (criteria.X.HasValue) parts.Add($"X {criteria.XOperator} {criteria.X}");
        if (criteria.Y.HasValue) parts.Add($"Y {criteria.YOperator} {criteria.Y}");
        if (!string.IsNullOrWhiteSpace(criteria.Text)) parts.Add($"文字 {criteria.TextOperator} {criteria.Text}");
        return $"{name} · {(parts.Count == 0 ? "无筛选" : string.Join(" 且 ", parts))}";
    }
    private void MigrateLegacyRecognitionVariables()
    {
        foreach (var variable in Machine.Variables.Where(v => v.Source == "color").ToArray())
        {
            var item = new RecognitionItem { Id = variable.Id, Name = variable.Name, Settings = variable.Color ?? new() };
            if (!Machine.Recognitions.Any(i => i.Id == item.Id)) Machine.Recognitions.Add(item);
            foreach (var condition in Machine.States.SelectMany(s => s.Outputs).SelectMany(o => o.Conditions)
                         .Where(c => c.Left == variable.Name))
            {
                condition.Kind = "recognition"; condition.RecognitionId = item.Id; condition.RecognitionField = "status";
                var expected = DisplayNode(condition.RightValue, "boolean") == "true";
                condition.ExpectedSuccess = condition.Operator == "!=" ? !expected : expected;
            }
            Machine.Variables.Remove(variable);
        }
        foreach (var variable in Machine.Variables) { variable.Source = null; variable.Color = null; }
    }
}
