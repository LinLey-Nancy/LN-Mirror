using System.Diagnostics;
using System.Text.Json;
using LNMirror.Core.Configuration;
using LNMirror.Core.Interop;

namespace LNMirror.Core.Windowing;

public sealed class WindowRecoveryStore
{
    private sealed record RecoveryRecord
    {
        public long Handle { get; init; }
        public uint ProcessId { get; init; }
        public long ProcessStartTimeTicks { get; init; }
        public WindowStyleSnapshot? Snapshot { get; init; }
    }

    private readonly string _path;

    public WindowRecoveryStore(string path) => _path = path;

    public void Save(DesktopWindowInfo target, WindowStyleSnapshot snapshot)
    {
        if (SettingsStore.ReadOnly) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var record = new RecoveryRecord
            {
                Handle = target.Handle.ToInt64(),
                ProcessId = target.ProcessId,
                ProcessStartTimeTicks = target.ProcessStartTimeTicks,
                Snapshot = snapshot,
            };
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(record));
            File.Move(temp, _path, overwrite: true);
        }
        catch { }
    }

    public void Recover()
    {
        if (SettingsStore.ReadOnly || !File.Exists(_path)) return;
        RecoveryRecord? record = null;
        try { record = JsonSerializer.Deserialize<RecoveryRecord>(File.ReadAllText(_path)); }
        catch { }
        if (record?.Snapshot is null)
        {
            Clear();
            return;
        }

        var handle = new IntPtr(record.Handle);
        if (!NativeMethods.IsWindow(handle))
        {
            Clear();
            return;
        }
        NativeMethods.GetWindowThreadProcessId(handle, out uint processId);
        if (processId != record.ProcessId)
        {
            Clear();
            return;
        }
        if (record.ProcessStartTimeTicks != 0)
        {
            try
            {
                using var process = Process.GetProcessById(checked((int)processId));
                if (process.StartTime.ToUniversalTime().Ticks != record.ProcessStartTimeTicks)
                {
                    Clear();
                    return;
                }
            }
            catch (ArgumentException)
            {
                Clear();
                return;
            }
            catch
            {
                return;
            }
        }

        try
        {
            using var controller = new WindowStyleController();
            controller.Attach(handle);
            controller.RestoreSnapshot(record.Snapshot);
        }
        finally
        {
            Clear();
        }
    }

    public void Clear()
    {
        if (SettingsStore.ReadOnly) return;
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
            string temp = _path + ".tmp";
            if (File.Exists(temp)) File.Delete(temp);
        }
        catch { }
    }
}
