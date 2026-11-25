using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;

namespace LNMirror.Core.Windowing;

public sealed class WindowHelperException : Exception
{
    public WindowHelperException(int error, string message) : base(message) => Error = error;
    public int Error { get; }
}

public sealed class ElevatedWindowSession : IDisposable
{
    private readonly Process _process;
    private readonly WindowHelperConnection _connection;
    private bool _disposed;

    private ElevatedWindowSession(Process process, WindowHelperConnection connection)
    {
        _process = process;
        _connection = connection;
    }

    public static async Task<ElevatedWindowSession> StartAsync(DesktopWindowInfo target, CancellationToken cancellationToken = default)
    {
        string pipeName = $"ln-mirror-window-{Guid.NewGuid():N}";
        var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Process? process = null;
        WindowHelperConnection? connection = null;
        try
        {
            string fileName = Environment.ProcessPath ?? throw new InvalidOperationException("找不到当前程序路径。");
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            if (string.Equals(Path.GetFileName(fileName), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
                startInfo.ArgumentList.Add(typeof(ElevatedWindowSession).Assembly.Location);
            startInfo.ArgumentList.Add("--window-helper");
            startInfo.ArgumentList.Add("--pipe");
            startInfo.ArgumentList.Add(pipeName);
            startInfo.ArgumentList.Add("--parent-pid");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动提权辅助进程。");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            Task connect = server.WaitForConnectionAsync(timeout.Token);
            Task exit = process.WaitForExitAsync(timeout.Token);
            Task completed = await Task.WhenAny(connect, exit).ConfigureAwait(false);
            if (completed == exit) throw new IOException("提权辅助进程在连接前退出。");
            await connect.ConfigureAwait(false);

            connection = new WindowHelperConnection(server);
            var response = await connection.SendAsync(new WindowHelperRequest
            {
                Command = "attach",
                Handle = target.Handle.ToInt64(),
                ProcessId = target.ProcessId,
                ProcessStartTimeTicks = target.ProcessStartTimeTicks,
            }, timeout.Token).ConfigureAwait(false);
            if (!response.Ok) throw new WindowHelperException(response.Error, response.Message);

            var session = new ElevatedWindowSession(process, connection);
            process = null;
            connection = null;
            server = null!;
            return session;
        }
        finally
        {
            connection?.Dispose();
            server?.Dispose();
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited && !process.WaitForExit(2000)) process.Kill(true);
                }
                catch { }
                process.Dispose();
            }
        }
    }

    public Task<WindowHelperResponse> SendAsync(WindowHelperRequest request, CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ElevatedWindowSession));
        return _connection.SendAsync(request, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _connection.SendAsync(new WindowHelperRequest { Command = "detach" }).GetAwaiter().GetResult(); }
        catch { }
        _connection.Dispose();
        try { _process.WaitForExit(2000); }
        catch { }
        try
        {
            if (!_process.HasExited) _process.Kill(true);
        }
        catch { }
        _process.Dispose();
    }
}
