using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using LNMirror.Core.Interop;

namespace LNMirror.Core.Windowing;

public sealed class WindowHelperRequest
{
    public string Command { get; set; } = string.Empty;
    public long Handle { get; set; }
    public uint ProcessId { get; set; }
    public long ProcessStartTimeTicks { get; set; }
    public int Value { get; set; }
}

public sealed class WindowHelperResponse
{
    public bool Ok { get; set; }
    public int Error { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class WindowHelperConnection : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Stream _stream;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _requests = new(1, 1);
    private bool _disposed;

    public WindowHelperConnection(Stream stream)
    {
        _stream = stream;
        _reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
        _writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
    }

    public async Task<WindowHelperResponse> SendAsync(WindowHelperRequest request, CancellationToken cancellationToken = default)
    {
        await _requests.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string json = JsonSerializer.Serialize(request, JsonOptions);
            await _writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            string? response = await _reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            if (response is null) throw new IOException("提权辅助进程已断开。");
            return JsonSerializer.Deserialize<WindowHelperResponse>(response, JsonOptions)
                ?? throw new IOException("提权辅助进程返回了无效响应。");
        }
        finally
        {
            _requests.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _reader.Dispose(); } catch (ObjectDisposedException) { }
        try { _writer.Dispose(); } catch (ObjectDisposedException) { }
        try { _stream.Dispose(); } catch (ObjectDisposedException) { }
        _requests.Dispose();
    }
}

public static class WindowHelperServer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task RunAsync(string pipeName, int parentProcessId)
    {
        using var parent = Process.GetProcessById(parentProcessId);
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(10000).ConfigureAwait(false);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        WindowStyleController? controller = null;
        try
        {
            while (true)
            {
                string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null) break;
                WindowHelperResponse response;
                try
                {
                    var request = JsonSerializer.Deserialize<WindowHelperRequest>(line, JsonOptions);
                    response = request is null
                        ? new WindowHelperResponse { Error = 4000, Message = "请求格式无效" }
                        : Execute(request, ref controller);
                }
                catch (Exception ex)
                {
                    response = new WindowHelperResponse { Error = 4000, Message = ex.Message };
                }
                string json = JsonSerializer.Serialize(response, JsonOptions);
                await writer.WriteLineAsync(json).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            controller?.Dispose();
        }
    }

    private static WindowHelperResponse Execute(WindowHelperRequest request, ref WindowStyleController? controller)
    {
        if (request.Command == "ping") return new WindowHelperResponse { Ok = true };
        if (request.Command == "detach")
        {
            controller?.Dispose();
            controller = null;
            return new WindowHelperResponse { Ok = true };
        }
        if (request.Command == "attach") return Attach(request, ref controller);
        if (controller is null) return new WindowHelperResponse { Error = 4005, Message = "尚未绑定窗口" };

        bool ok;
        switch (request.Command)
        {
            case "opacity":
                ok = controller.ApplyOpacity(request.Value) is not null;
                break;
            case "passthrough":
                ok = controller.ApplyPassThrough(request.Value != 0);
                break;
            case "topmost":
                ok = controller.ApplyTopmost(request.Value != 0);
                break;
            case "switcher":
                ok = controller.ApplyTaskSwitcherHidden(request.Value != 0);
                break;
            default:
                return new WindowHelperResponse { Error = 4000, Message = "不支持的命令" };
        }

        return new WindowHelperResponse
        {
            Ok = ok,
            Error = ok ? 0 : controller.LastError,
            Message = ok ? string.Empty : "窗口操作失败",
        };
    }

    private static WindowHelperResponse Attach(WindowHelperRequest request, ref WindowStyleController? controller)
    {
        controller?.Dispose();
        controller = null;
        var handle = new IntPtr(request.Handle);
        if (handle == IntPtr.Zero || !NativeMethods.IsWindow(handle))
            return new WindowHelperResponse { Error = 4001, Message = "窗口已经失效" };
        NativeMethods.GetWindowThreadProcessId(handle, out uint processId);
        if (processId != request.ProcessId)
            return new WindowHelperResponse { Error = 4002, Message = "窗口进程身份不一致" };
        if (request.ProcessStartTimeTicks != 0)
        {
            try
            {
                using var process = Process.GetProcessById(checked((int)processId));
                if (process.StartTime.ToUniversalTime().Ticks != request.ProcessStartTimeTicks)
                    return new WindowHelperResponse { Error = 4003, Message = "进程已经重新启动" };
            }
            catch (Exception ex)
            {
                return new WindowHelperResponse { Error = 4004, Message = ex.Message };
            }
        }

        controller = new WindowStyleController();
        controller.Attach(handle);
        return new WindowHelperResponse { Ok = true };
    }
}
