using FastColorFinder.Native;

namespace StateMachine.Training;

public interface IRecognitionRegionSelector
{
    Task<Rectangle?> SelectAsync();
}

public sealed class RecognitionRegionSelector(NativeRegionSelector selector) : IRecognitionRegionSelector
{
    public Task<Rectangle?> SelectAsync() => selector.SelectAsync();
}
