using ClickableTransparentOverlay;
using ImGuiNET;

namespace SimpleExample;

internal class SampleOverlay : Overlay
{
    private bool _wantKeepDemoWindow = true;

    protected override Task PostInitialized()
    {
        return Task.CompletedTask;
    }

    protected override void Render()
    {
        ImGui.ShowDemoWindow(ref _wantKeepDemoWindow);
        if (!_wantKeepDemoWindow)
        {
            Close();
        }
    }
}
