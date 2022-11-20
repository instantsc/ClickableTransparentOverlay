using System.Threading.Tasks;

namespace SingleThreadedOverlayWithCoroutines;

internal class Program
{
    private static async Task Main()
    {
        using var overlay = new SampleOverlay();
        await overlay.Run();
    }
}
