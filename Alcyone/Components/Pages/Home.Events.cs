using Microsoft.AspNetCore.Components;
namespace StateMachine.Components.Pages;

public partial class Home : IHandleEvent
{
    // Persist UI edits after their event completes. Runtime notifications only render snapshots.
    async Task IHandleEvent.HandleEventAsync(EventCallbackWorkItem callback, object? argument)
    {
        var task = callback.InvokeAsync(argument);
        StateHasChanged();
        await task;
        await SaveConfigAsync();
        if (!_disposed) StateHasChanged();
    }
}
