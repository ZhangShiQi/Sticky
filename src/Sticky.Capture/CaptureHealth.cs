using System.Threading;
using System.Threading.Tasks;
using Sticky.Capture.Services;

namespace Sticky.Capture;

public static class CaptureHealth
{
    public static async Task CheckAsync(CancellationToken token)
    {
        await StartupHealth.CheckAsync(token);
        var tools = await MediaTools.LoadAsync(token);
        foreach (var executable in new[] { tools.Probe, tools.Encoder })
        {
            using var probe = new MediaProcess(executable, new[] { "-version" });
            if (await probe.WaitAsync(token) != 0) throw new System.IO.IOException("录制工具启动自检失败。");
        }
    }
}
