public sealed class DesktopActions : StateScript
{
    [StateAction("鼠标点击", "桌面自动化")]
    public void Click([StateParameter("屏幕 X")] int x, [StateParameter("屏幕 Y")] int y,
        [StateParameter("按钮 left/right/middle")] string button = "left") => Api.Input.Click(x, y, button);

    [StateAction("鼠标移动", "桌面自动化")]
    public void Move([StateParameter("屏幕 X")] int x, [StateParameter("屏幕 Y")] int y) => Api.Input.MoveTo(x, y);

    [StateAction("按键", "桌面自动化")]
    public void Press([StateParameter("按键 ENTER/ESC/F1/A 等")] string key) => Api.Input.Press(key);

    [StateAction("组合键", "桌面自动化")]
    public void Hotkey([StateParameter("组合键，例如 CTRL+A")] string keys) =>
        Api.Input.Hotkey(keys.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

    [StateAction("输入文字", "桌面自动化")]
    public void Type([StateParameter("文字")] string text) => Api.Input.TypeText(text);

    [StateAction("等待", "桌面自动化")]
    public Task Wait([StateParameter("毫秒")] int milliseconds = 300) => Api.Delay(milliseconds);

    [StateAction("点击本轮找到的特征", "桌面自动化")]
    public void ClickFeature([StateParameter("识别项目名")] string name,
        [StateParameter("X 偏移")] int offsetX = 0, [StateParameter("Y 偏移")] int offsetY = 0)
    {
        var hit = Api.Recognition.Last(name);
        if (hit is null || !hit.Found || hit.Error is not null)
            throw new InvalidOperationException($"本轮没有找到特征：{name}");
        Api.Input.Click(hit.X + offsetX, hit.Y + offsetY);
    }

    [StateAction("识别后点击", "桌面自动化")]
    public async Task FindAndClick([StateParameter("识别项目名")] string name,
        [StateParameter("最长查找秒数")] double seconds = 3,
        [StateParameter("X 偏移")] int offsetX = 0, [StateParameter("Y 偏移")] int offsetY = 0)
    {
        var hit = await Api.Recognition.FindAsync(name, seconds);
        if (hit.Found) Api.Input.Click(hit.X + offsetX, hit.Y + offsetY);
        else Log($"没有找到：{name}", level: "warn");
    }

    // The initial demo deliberately only records an observation. Select ClickFeature to perform a click.
    [StateAction("记录找到特征", "桌面自动化")]
    public void RecordFeature()
    {
        var hit = Api.Recognition.Last("TargetVisible");
        if (hit is null || !hit.Found) throw new InvalidOperationException("目标特征不存在。");
        Vars.Set("ExecutionCount", Vars.Get<int>("ExecutionCount", 0) + 1);
        Log($"找到目标 ({hit.X}, {hit.Y})，执行次数 {Vars.Get<int>("ExecutionCount", 0)}");
    }
}
