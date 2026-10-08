using Microsoft.JSInterop;
namespace StateMachine.Components.Training;

public partial class ColorTrainer
{

    private static string RegionValue(Rectangle region) => $"{region.X},{region.Y},{region.Width},{region.Height}";

    private async Task CopyRegionAsync(bool capture)
    {
        if (_busy) return;
        try
        {
            var region = capture ? Workspace.CaptureRegion : Workspace.SearchRegion;
            await JS.InvokeVoidAsync("navigator.clipboard.writeText", RegionValue(region));
            SetStatus("坐标范围已复制。", false);
        }
        catch (Exception ex) { SetStatus($"复制失败：{ex.Message}", true); }
    }

    private async Task PasteRegionAsync(bool capture)
    {
        if (_busy) return;
        try { await ApplyRegionAsync(await JS.InvokeAsync<string>("navigator.clipboard.readText"), capture); }
        catch (Exception ex) { SetStatus($"粘贴失败，可直接在范围输入框按 Ctrl+V：{ex.Message}", true); }
    }

    private async Task ApplyRegionAsync(string? value, bool capture)
    {
        if (_busy) return;
        var parts = (value ?? "").Split(new[] { ',', '，', ' ', ';', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var coordinates = new int[4];
        if (parts.Length != 4 || !parts.Select((part, index) => int.TryParse(part, out coordinates[index])).All(valid => valid)
            || coordinates[2] <= 0 || coordinates[3] <= 0)
        { SetStatus("请输入 X,Y,宽,高，宽和高必须大于 0。", true); return; }
        var region = new Rectangle(coordinates[0], coordinates[1], coordinates[2], coordinates[3]);
        if (region == (capture ? Workspace.CaptureRegion : Workspace.SearchRegion)) return;
        await StopContinuousAsync();
        SetBusy(true);
        try
        {
            if (capture)
            {
                SetCaptureForEditing(region);
                await PushCanvasStateAsync();
            }
            else Workspace.SetSearchRegion(region);
            SetStatus($"{(capture ? "捕获" : "搜索")}范围：{RegionValue(region)}", false);
        }
        catch (Exception ex) { SetStatus(ex.Message, true); }
        finally { SetBusy(false); }
    }

    private string BuildCallCode()
    {
        Rectangle range = Workspace.SearchRegion.Width > 0
            ? Workspace.SearchRegion
            : new Rectangle(0, 0, 800, 600);

        return $$"""
using System.Drawing;
using FastColorFinderRuntime;

string weightString = "{{_weightText}}";

FastColorSearch.SearchOutcome result = await FastColorSearch.FindAsync(
    new Rectangle(
        {{range.X}},
        {{range.Y}},
        {{range.Width}},
        {{range.Height}}
    ),

    weightString,

    colorDeviation: {{_colorDeviation}},

    similarityThreshold: {{_threshold}},

    onSuccess: success =>
    {
        Console.WriteLine(
            "找到 X=" + success.X +
            " Y=" + success.Y +
            " 相似度=" + success.Similarity.ToString("F1") + "%" +
            " 查找次数=" + success.Attempts +
            " 总耗时=" + success.Elapsed.TotalMilliseconds.ToString("F0") + "ms" +
            " 本次匹配=" + success.MatchMilliseconds.ToString("F2") + "ms");
    },

    onFailure: failure =>
    {
        Console.WriteLine(
            "未找到 最佳X=" + failure.BestX +
            " 最佳Y=" + failure.BestY +
            " 最佳相似度=" + failure.BestSimilarity.ToString("F1") + "%" +
            " 查找次数=" + failure.Attempts +
            " 总耗时=" + failure.Elapsed.TotalMilliseconds.ToString("F0") + "ms" +
            " 原因=" + failure.Reason);
    },

    loopSeconds: {{_loopSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}},

    intervalMilliseconds: {{_intervalMilliseconds}}
);
""";
    }
}
