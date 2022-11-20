using System;
using System.Numerics;
using ImGuiNET;

namespace MultiThreadedOverlay;

public class OverlaySample2RandomCircles
{
    public bool Show = false;
    private static Random _randomGen = new Random();
    private Vector2[] _circleCenters = new Vector2[200];

    public void Update()
    {
        if (!Show)
        {
            return;
        }

        for (var i = 0; i < _circleCenters.Length; i++)
        {
            _circleCenters[i].X = _randomGen.Next(0, 2560);
            _circleCenters[i].Y = _randomGen.Next(0, 1440);
        }
    }

    public void Render()
    {
        ImGui.SetNextWindowContentSize(ImGui.GetIO().DisplaySize);
        ImGui.SetNextWindowPos(new Vector2(0, 0));
        ImGui.Begin(
            "Background Screen",
            ref Show,
            ImGuiWindowFlags.NoInputs |
            ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoBringToFrontOnFocus |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoTitleBar);
        var windowPtr = ImGui.GetWindowDrawList();
        foreach (var center in _circleCenters)
        {
            windowPtr.AddCircleFilled(center, 10.0f, (uint)(((255 << 24) | (00 << 16) | (00 << 8) | 255) & 0xffffffffL));
        }

        ImGui.End();
    }
}
