using System.Collections.Generic;
using System.Numerics;
using ClickableTransparentOverlay;
using Coroutine;
using ImGuiNET;

namespace SingleThreadedOverlayWithCoroutines;

/// <summary>
/// Render Loop and Logic Loop are synchronized.
/// </summary>
internal class SampleOverlay : Overlay
{
    private readonly ushort[] _custom = new ushort[3] { 0x0020, 0xFFFF, 0x00 };
    private int _fontSize = 13;
    private int _data;
    private string _data2;
    private bool _isRunning = true;
    private bool _demoWindow = false;
    private readonly Event _myevent = new();
    private readonly ActiveCoroutine _myRoutine1;
    private readonly ActiveCoroutine _myRoutine2;

    public SampleOverlay()
        : base(true)
    {
        _myRoutine1 = CoroutineHandler.Start(TickServiceAsync(), name: "MyRoutine-1");
        _myRoutine2 = CoroutineHandler.Start(EventServiceAsync(), name: "MyRoutine-2");
    }

    private IEnumerator<Wait> TickServiceAsync()
    {
        var counter = 0;
        while (true)
        {
            counter++;
            yield return new Wait(3);
            _data = counter;
        }
    }

    private IEnumerator<Wait> EventServiceAsync()
    {
        var counter = 0;
        _data2 = "Initializing Event Routine";
        while (true)
        {
            yield return new Wait(_myevent);
            _data2 = $"Event Raised x {++counter}";
        }
    }

    private static float _x = 0.0f;
    private static float _y = 619.500f;
    private static float _w = 351.0f;
    private static float _h = 248.0f;

    protected override void Render()
    {
        CoroutineHandler.Tick(ImGui.GetIO().DeltaTime);
        if (_data % 5 == 1)
        {
            CoroutineHandler.RaiseEvent(_myevent);
        }

        ImGui.DragFloat("X", ref _x);
        ImGui.DragFloat("Y", ref _y);
        ImGui.DragFloat("W", ref _w);
        ImGui.DragFloat("H", ref _h);
        ImGui.GetBackgroundDrawList().AddRect(new Vector2(_x, _y), new Vector2(_x + _w, _y + _h), 0xFFFFFFFF);
        ImGui.Begin("Sample Overlay", ref _isRunning, ImGuiWindowFlags.AlwaysAutoResize);
        ImGui.Text($"Total Time/Delta Time: {ImGui.GetTime():F3}/{ImGui.GetIO().DeltaTime:F3}");
        ImGui.NewLine();

        ImGui.Text($"Counter: {_data}");
        ImGui.Text($"{_data2}");
        ImGui.NewLine();

        ImGui.Text($"Event Coroutines: {CoroutineHandler.EventCount}");
        ImGui.Text($"Ticking Coroutines: {CoroutineHandler.TickingCount}");
        ImGui.NewLine();

        ImGui.Text($"Coroutine Name: {_myRoutine1.Name}");
        ImGui.Text($"Total Executions: {_myRoutine1.MoveNextCount}");
        ImGui.Text($"Total Execution Time: {_myRoutine1.TotalMoveNextTime.TotalMilliseconds}");
        ImGui.Text($"Avg Execution Time: {_myRoutine1.TotalMoveNextTime.TotalMilliseconds / _myRoutine1.MoveNextCount}");
        ImGui.NewLine();

        ImGui.Text($"Coroutine Name: {_myRoutine2.Name}");
        ImGui.Text($"Total Executions: {_myRoutine2.MoveNextCount}");
        ImGui.Text($"Total Execution Time: {_myRoutine2.TotalMoveNextTime.TotalMilliseconds}");
        ImGui.Text($"Avg Execution Time: {_myRoutine2.TotalMoveNextTime.TotalMilliseconds / _myRoutine2.MoveNextCount}");
        ImGui.DragInt("Font Size", ref _fontSize, 0.1f, 13, 40);

        if (ImGui.Button("Change Font (更改字体)"))
        {
            ReplaceFont(@"C:\Windows\Fonts\msyh.ttc", _fontSize, FontGlyphRangeType.ChineseSimplifiedCommon);
        }

        if (ImGui.Button("Change Font (更改字体) Custom Range"))
        {
            ReplaceFont(@"C:\Windows\Fonts\msyh.ttc", _fontSize, _custom);
        }

        if (ImGui.Button("Show/Hide Demo Window"))
        {
            _demoWindow = !_demoWindow;
        }

        ImGui.End();
        if (!_isRunning)
        {
            Close();
        }

        if (_demoWindow)
        {
            ImGui.ShowDemoWindow(ref _demoWindow);
        }
    }
}
