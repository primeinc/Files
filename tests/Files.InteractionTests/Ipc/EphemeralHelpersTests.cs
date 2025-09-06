using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Files.App.Communication;

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
        public void EphemeralTokenHelper_GeneratesStableToken()
        {
            var t1 = IpcRendezvousFile.GetOrCreateToken();
            var t2 = IpcRendezvousFile.GetOrCreateToken();
            Assert.AreEqual(t1, t2); // now stable across calls in same session
            Assert.IsTrue(t1.Length >= 40);
        }

        [TestMethod]
        public async Task RendezvousFile_UpdateAndMerge()
        {
            await IpcRendezvousFile.UpdateAsync(webSocketPort: 11111, epoch: 1);
            await IpcRendezvousFile.UpdateAsync(pipeName: "pipe-xyz", epoch: 1);

            var path = IpcRendezvousFile.GetCurrentPath();
            Assert.IsTrue(File.Exists(path));
            var json = await File.ReadAllTextAsync(path);
            using var doc = JsonDocument.Parse(json);
            Assert.AreEqual(11111, doc.RootElement.GetProperty("webSocketPort").GetInt32());
            Assert.AreEqual("pipe-xyz", doc.RootElement.GetProperty("pipeName").GetString());
            Assert.IsFalse(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("token").GetString()));
        }
    }
}
