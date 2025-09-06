using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Files.App.Communication;
using Windows.Storage;

namespace Files.InteractionTests.Ipc
{
    [TestClass]
    public class EphemeralHelpersTests
    {
        [TestMethod]
        public void EphemeralPortAllocator_ReturnsValidPort()
        {
            var ports = new HashSet<int>();
            for (int i = 0; i < 5; i++)
            {
                var port = EphemeralPortAllocator.GetEphemeralTcpPort();
                Assert.IsTrue(port > 0);
                ports.Add(port);
            }
        }

        [TestMethod]
        public void EphemeralTokenHelper_GeneratesUniqueTokens()
        {
            var t1 = EphemeralTokenHelper.GenerateToken();
            var t2 = EphemeralTokenHelper.GenerateToken();
            Assert.IsFalse(string.IsNullOrWhiteSpace(t1));
            Assert.IsFalse(string.IsNullOrWhiteSpace(t2));
            Assert.AreNotEqual(t1, t2);
        }

        [TestMethod]
        public async Task RendezvousFile_CreatesAndDeletes()
        {
            // Write
            await IpcRendezvousFile.TryUpdateAsync("test-token", 12345, "pipe-test", 1);
            var localPath = ApplicationData.Current.LocalFolder.Path;
            var filePath = Path.Combine(localPath, "ipc.info");
            Assert.IsTrue(File.Exists(filePath));

            var json = await File.ReadAllTextAsync(filePath);
            using var doc = JsonDocument.Parse(json);
            Assert.AreEqual(12345, doc.RootElement.GetProperty("webSocketPort").GetInt32());
            Assert.AreEqual("pipe-test", doc.RootElement.GetProperty("pipeName").GetString());

            // Delete
            await IpcRendezvousFile.TryDeleteAsync();
            Assert.IsFalse(File.Exists(filePath));
        }
    }
}
