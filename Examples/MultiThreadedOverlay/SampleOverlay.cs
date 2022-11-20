using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Numerics;
using System.Threading;
using ClickableTransparentOverlay;
using ClickableTransparentOverlay.Win32;
using ImGuiNET;

namespace MultiThreadedOverlay;

/// <summary>
/// Render Loop and Logic Loop are independent from each other. 
/// </summary>
public class SampleOverlay : Overlay
{
    private volatile State _state;
    private readonly Thread _logicThread;

    public SampleOverlay()
    {
        _state = new State();
        _logicThread = new Thread(() =>
        {
            var lastRunTickStamp = _state.Watch.ElapsedTicks;

            while (_state.IsRunning)
            {
                var currentRunTickStamp = _state.Watch.ElapsedTicks;
                var delta = currentRunTickStamp - lastRunTickStamp;
                LogicUpdate(delta);
                lastRunTickStamp = currentRunTickStamp;
            }
        });

        _logicThread.Start();
    }

    public override void Close()
    {
        base.Close();
        _state.IsRunning = false;
    }

    private void LogicUpdate(float updateDeltaTicks)
    {
        _state.LogicTicksCounter.Increment();
        _state.LogicalDelta = updateDeltaTicks;

        if (_state.RequestLogicThreadSleep)
        {
            Thread.Sleep(TimeSpan.FromSeconds(_state.SleepInSeconds));
            _state.RequestLogicThreadSleep = false;
        }

        if (_state.LogicThreadCloseOverlay)
        {
            Close();
            _state.LogicThreadCloseOverlay = false;
        }

        _state.OverlaySample2.Update();
        Thread.Sleep(_state.LogicTickDelayInMilliseconds); //Not accurate at all as a mechanism for limiting thread runs
    }

    protected override void Render()
    {
        var deltaSeconds = ImGui.GetIO().DeltaTime;

        if (!_state.Visible)
        {
            _state.ReappearTimeRemaining -= deltaSeconds;
            if (_state.ReappearTimeRemaining < 0)
            {
                _state.Visible = true;
            }

            return;
        }

        _state.RenderFramesCounter.Increment();

        if (Utils.IsKeyPressedAndNotTimeout(VK.F12)) //F12.
        {
            _state.ShowClickableMenu = !_state.ShowClickableMenu;
            ImGui.GetIO().WantCaptureMouse = true; // workaround: where overlay gets stuck in non-clickable mode forever.
        }

        if (_state.ShowImGuiDemo)
        {
            ImGui.ShowDemoWindow(ref _state.ShowImGuiDemo);
        }

        if (_state.ShowOverlaySample1)
        {
            RenderOverlaySample1();
        }

        if (_state.OverlaySample2.Show)
        {
            _state.OverlaySample2.Render();
        }

        if (_state.ShowClickableMenu)
        {
            RenderMainMenu();
        }
    }

    private void RenderMainMenu()
    {
        var isCollapsed = !ImGui.Begin(
            "Overlay Main Menu",
            ref _state.IsRunning,
            ImGuiWindowFlags.NoResize | ImGuiWindowFlags.AlwaysAutoResize);

        if (!_state.IsRunning || isCollapsed)
        {
            ImGui.End();
            if (!_state.IsRunning)
            {
                Close();
            }

            return;
        }

        ImGui.Text("Try pressing F12 button to show/hide this menu.");
        ImGui.Text("Click X on top right of this menu to close the overlay.");
        ImGui.Checkbox("Show non-clickable transparent overlay Sample 1.", ref _state.ShowOverlaySample1);
        ImGui.Checkbox("Show full-screen non-clickable transparent overlay sample 2.", ref _state.OverlaySample2.Show);

        ImGui.NewLine();
        ImGui.SliderInt2("Set Position", ref _state.ResizeHelper[0], 0, 3840);
        ImGui.SliderInt2("Set Size", ref _state.ResizeHelper[2], 0, 3840);
        if (ImGui.Button("Resize"))
        {
            Position = new Point(_state.ResizeHelper[0], _state.ResizeHelper[1]);
            Size = new Size(_state.ResizeHelper[2], _state.ResizeHelper[3]);
        }

        ImGui.NewLine();
        ImGui.SliderInt("###time(sec)", ref _state.Seconds, 1, 30);
        if (ImGui.Button($"Hide for {_state.Seconds} seconds"))
        {
            _state.Visible = false;
            _state.ReappearTimeRemaining = _state.Seconds;
        }

        ImGui.NewLine();
        ImGui.SliderInt("###sleeptime(sec)", ref _state.SleepInSeconds, 1, 30);
        if (ImGui.Button($"Sleep Render Thread for {_state.SleepInSeconds} seconds"))
        {
            Thread.Sleep(TimeSpan.FromSeconds(_state.SleepInSeconds));
        }

        if (ImGui.Button($"Sleep Logic Thread for {_state.SleepInSeconds} seconds"))
        {
            _state.RequestLogicThreadSleep = true;
        }

        ImGui.NewLine();
        if (ImGui.Button($"Request Logic Thread to close Overlay."))
        {
            _state.LogicThreadCloseOverlay = true;
        }

        ImGui.NewLine();
        ImGui.SliderInt("Logical Thread Delay(ms)", ref _state.LogicTickDelayInMilliseconds, 1, 1000);
        ImGui.NewLine();
        if (ImGui.Button("Toggle ImGui Demo"))
        {
            _state.ShowImGuiDemo = !_state.ShowImGuiDemo;
        }

        ImGui.NewLine();
        if (File.Exists("image.png"))
        {
            AddOrGetImagePointer(
                "image.png",
                false,
                out var imgPtr,
                out var w,
                out var h);
            ImGui.Image(imgPtr, new Vector2(w, h));
        }
        else
        {
            ImGui.Text("Put any image where the exe is, name is 'image.png'");
        }

        ImGui.End();
    }

    private void RenderOverlaySample1()
    {
        ImGui.SetNextWindowPos(new Vector2(0f, 0f));
        ImGui.SetNextWindowBgAlpha(0.9f);
        ImGui.Begin(
            "Sample Overlay",
            ImGuiWindowFlags.NoInputs |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoResize);

        ImGui.Text("I am sample Overlay to display stuff.");
        ImGui.Text("You can not click me.");
        ImGui.NewLine();
        ImGui.Text($"Number of displays {NumberVideoDisplays}");
        ImGui.Text($"Current Date: {DateTime.Now.Date.ToShortDateString()}");
        ImGui.Text($"Current Time: {DateTime.Now.TimeOfDay}");
        ImGui.Text($"Total Rendered Frames: {_state.RenderFramesCounter.Count}");
        ImGui.Text($"Render Delta (seconds): {ImGui.GetIO().DeltaTime:F4}");
        ImGui.Text($"Render FPS: {ImGui.GetIO().Framerate:F1}");
        ImGui.Text($"Total Logic Frames: {_state.LogicTicksCounter.Count}");
        ImGui.Text($"Logic Delta (seconds): {_state.LogicalDelta / Stopwatch.Frequency:F4}");
        ImGui.End();
    }
}
