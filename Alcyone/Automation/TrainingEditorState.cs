namespace StateMachine.Automation;

// Saved with the recognition item so editing survives closing the app.
public sealed record TrainingEditorState
{
    public byte[]? ImagePng { get; init; }
    public TrainingCaptureRegion? Capture { get; init; }
    public TrainingCaptureRegion? Selection { get; init; }
    public string SeedsJson { get; init; } = "[]";
    public string PointsJson { get; init; } = "[]";
    public string Label { get; init; } = "";
    public int Zoom { get; init; } = 2;
    public int Seconds { get; init; } = 5;
    public int SelectedSeed { get; init; } = -1;
    public int DefaultDeviation { get; init; } = 20;
    public bool SelectTool { get; init; }
    public bool HasPreview { get; init; }
    public bool[]? PreviewMask { get; init; }
}
