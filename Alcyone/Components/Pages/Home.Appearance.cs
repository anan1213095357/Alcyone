namespace StateMachine.Components.Pages;

public partial class Home
{
    private bool _starshipSettingsOpen;
    private string? _lastStarshipImage;
    private async Task ApplyStarshipAppearanceAsync(StarshipAppearancePicker.AppearanceSelection selection)
    {
        if (selection.Appearance is not ("sn9" or "musk" or "custom")) return;
        var previous = (Machine.Settings.StarshipAppearance, Machine.Settings.StarshipImage, Machine.Settings.StarshipScale);
        Machine.Settings.StarshipAppearance = selection.Appearance;
        Machine.Settings.StarshipImage = selection.Image;
        Machine.Settings.StarshipScale = Math.Clamp(selection.Scale, .5, 3);
        try { await SaveConfigCoreAsync(reportFailure: true); }
        catch { (Machine.Settings.StarshipAppearance, Machine.Settings.StarshipImage, Machine.Settings.StarshipScale) = previous; throw; }
        _starshipSettingsOpen = false;
    }
}
