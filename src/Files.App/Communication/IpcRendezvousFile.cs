using System;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Tasks;

namespace Files.App.Communication
{
    public static class IpcRendezvousFile
    {
        private const string FileName = "ipc.info"; // single instance (multi-instance support can suffix pid later)
        private static readonly object _gate = new();
        private static bool _deleted;
        private static string? _cachedToken;

        private sealed record Model(int? webSocketPort, string? pipeName, string? token, int epoch, int serverPid, DateTime createdUtc)
        {
            public Model Merge(Model newer)
                => new(
                    newer.webSocketPort ?? webSocketPort,
                    newer.pipeName ?? pipeName,
                    token, // token is stable for runtime
                    epoch,
                    serverPid,
                    createdUtc);
        }

        // Public accessor for tests/clients
        public static string GetCurrentPath()
        {
            var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FilesIPC");
            Directory.CreateDirectory(baseDir);
            return Path.Combine(baseDir, FileName);
        }

        public static string GetOrCreateToken()
        {
            lock (_gate)
            {
                if (!string.IsNullOrEmpty(_cachedToken))
                    return _cachedToken!;

                // If file exists try read token
                try
                {
                    var path = GetCurrentPath();
                    if (File.Exists(path))
                    {
                        var json = File.ReadAllText(path);
                        var existing = JsonSerializer.Deserialize<Model>(json);
                        if (existing?.token is string t && t.Length > 0)
                        {
                            _cachedToken = t;
                            return t;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[IPC] Failed reading rendezvous file for token reuse: {ex.Message}");
                }

                _cachedToken = EphemeralTokenHelper.GenerateToken();
                return _cachedToken!;
            }
        }

        public static async Task UpdateAsync(int? webSocketPort = null, string? pipeName = null, int epoch = 0)
        {
            try
            {
                lock (_gate)
                {
                    if (_deleted) return; // do not resurrect after deletion

                    var path = GetCurrentPath();
                    Model? existing = null;
                    if (File.Exists(path))
                    {
                        try
                        {
                            var jsonOld = File.ReadAllText(path);
                            existing = JsonSerializer.Deserialize<Model>(jsonOld);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[IPC] Unable to parse existing rendezvous file: {ex.Message}");
                        }
                    }

                    var token = GetOrCreateToken();
                    var now = DateTime.UtcNow;
                    var incoming = new Model(webSocketPort, pipeName, token, epoch, Environment.ProcessId, existing?.createdUtc ?? now);
                    var final = existing is null ? incoming : existing.Merge(incoming);

                    var json = JsonSerializer.Serialize(final, new JsonSerializerOptions { WriteIndented = false });

                    // Atomic write via temp file + replace to avoid readers seeing partial content
                    var dir = Path.GetDirectoryName(path) ?? ".";
                    var tmp = Path.Combine(dir, Path.GetRandomFileName());
                    File.WriteAllText(tmp, json);
                    File.Copy(tmp, path, overwrite: true);
                    File.Delete(tmp);
                    Secure(path);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[IPC] Rendezvous update failed: {ex.Message}");
            }
            await Task.CompletedTask;
        }

        public static async Task TryDeleteAsync()
        {
            try
            {
                lock (_gate) _deleted = true;
                var path = GetCurrentPath();
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[IPC] Failed deleting rendezvous file: {ex.Message}");
            }
            await Task.CompletedTask;
        }

        private static void Secure(string filePath)
        {
            try
            {
                var current = WindowsIdentity.GetCurrent();
                if (current?.User is null) return;

                var security = new FileSecurity();
                security.SetOwner(current.User);
                security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new FileSystemAccessRule(current.User, FileSystemRights.FullControl, AccessControlType.Allow));

                new FileInfo(filePath).SetAccessControl(security);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[IPC] Failed securing rendezvous file: {ex.Message}");
            }
        }
    }
}
