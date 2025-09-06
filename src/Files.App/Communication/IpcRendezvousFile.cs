using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage;

namespace Files.App.Communication
{
    public static class IpcRendezvousFile
    {
        private const string FileName = "ipc.info"; // single instance (multi-instance support can suffix pid later)
        private static readonly object _gate = new();
        private static bool _deleted;

        private sealed record Model(int? webSocketPort, string? pipeName, string? token, int epoch, int serverPid, DateTime createdUtc);

        private static string GetPath()
        {
            var dir = ApplicationData.Current.LocalFolder.Path; // sandbox safe
            return Path.Combine(dir, FileName);
        }

        public static async Task TryUpdateAsync(string? token, int? webSocketPort, string? pipeName, int epoch)
        {
            try
            {
                lock (_gate)
                {
                    if (_deleted) return; // do not resurrect after deletion
                }

                var path = GetPath();
                var model = new Model(webSocketPort, pipeName, token, epoch, Environment.ProcessId, DateTime.UtcNow);
                var json = JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = false });
                File.WriteAllText(path, json);
                Secure(path);
            }
            catch
            {
                // swallow – rendezvous is best effort
            }
            await Task.CompletedTask;
        }

        public static async Task TryDeleteAsync()
        {
            try
            {
                lock (_gate) _deleted = true;
                var path = GetPath();
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
            await Task.CompletedTask;
        }

        private static void Secure(string filePath)
        {
            try
            {
                var current = WindowsIdentity.GetCurrent();
                var security = new FileSecurity();
                security.SetOwner(current.User!);
                security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new FileSystemAccessRule(current.User!, FileSystemRights.FullControl, AccessControlType.Allow));
                new FileInfo(filePath).SetAccessControl(security);
            }
            catch
            {
                // ignore
            }
        }
    }
}
