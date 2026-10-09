using Microsoft.AspNetCore.Components;

namespace StateMachine.Localization;

public abstract class LocalizedComponent : ComponentBase, IAsyncDisposable
{
    [Inject] protected UiText L { get; set; } = default!;
    private bool _subscribed;
    private bool _disposed;

    public override Task SetParametersAsync(ParameterView parameters)
    {
        if (!_subscribed)
        {
            L.Changed += OnLanguageChanged;
            _subscribed = true;
        }
        return base.SetParametersAsync(parameters);
    }

    private void OnLanguageChanged()
    {
        if (!_disposed) _ = InvokeAsync(() => { if (!_disposed) StateHasChanged(); });
    }

    public virtual ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_subscribed) L.Changed -= OnLanguageChanged;
        return ValueTask.CompletedTask;
    }
}
