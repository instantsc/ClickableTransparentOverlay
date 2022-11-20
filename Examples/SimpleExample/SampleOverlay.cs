using ClickableTransparentOverlay;
using ImGuiNET;

namespace SimpleExample;

internal class SampleOverlay : Overlay
{
    private bool wantKeepDemoWindow = true;

    protected override Task PostInitialized()
    {
        return Task.CompletedTask;
    }

    protected override void Render()
    {
        ImGui.ShowDemoWindow(ref wantKeepDemoWindow);
        if (!wantKeepDemoWindow)
        {
            Close();
        }
    }
}
