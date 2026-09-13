using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QuickShare.PC.Models;
using QuickShare.PC.Services;

namespace QuickShare.PC.EmpiricalTests
{
    public static class Program
    {
        private static int _passedCount = 0;
        private static int _failedCount = 0;
        private static readonly List<string> _failures = new List<string>();

        public static async Task<int> Main(string[] args)
        {
            Console.WriteLine("===============================================================");
            Console.WriteLine(" Quick-Share-PC Empirical Verification & Adversarial Challenge ");
            Console.WriteLine("===============================================================\n");

            var sw = Stopwatch.StartNew();

            try
            {
                // Group 1: Protocol Constants & Codec
                RunTest("ProtocolConstants_ValuesMatchSpecification", TestProtocolConstants);
                RunTest("QuickShareStream_BigEndianPrimitivesCodec", TestQuickShareStreamBigEndianPrimitives);
                await RunTestAsync("QuickShareStream_AsyncPrimitivesCodec", TestQuickShareStreamAsyncPrimitives);
                await RunTestAsync("QuickShareStream_FragmentedNetworkStreamHandling", TestQuickShareStreamFragmentedReads);
                RunTest("QuickShareStream_UtfCodecAndBoundaries", TestQuickShareStreamUtfCodec);

                // Group 2: Models & FileBlock Math
                RunTest("FileBlock_MathAndBoundaries", TestFileBlockMath);
                RunTest("FileBlock_ComparableOrdering", TestFileBlockOrdering);
                RunTest("QuickShareDirectory_PathTranslationAndFileSystemNormalization", TestQuickShareDirectory);

                // Group 3: WriteFileCall & ReadFileCall Pipelines
                await RunTestAsync("WriteFileCall_SequentialWritingAndBufferRecycling", TestWriteFileCallSequential);
                await RunTestAsync("WriteFileCall_ZeroByteFileAndDirectoryCreation", TestWriteFileCallZeroByteAndDirectory);
                await RunTestAsync("WriteFileCall_CancelCleansUpBuffersWithoutLeak", TestWriteFileCallCancelLeakPrevention);
                await RunTestAsync("ReadFileCall_SlicingAndBufferManagement", TestReadFileCallSlicing);
                RunTest("ReadFileCall_ShutdownSentinelsAndErrorHandling", TestReadFileCallSentinels);

                // Group 4: NetworkService & IP Detection
                RunTest("NetworkService_AvailableInterfacesAndPrimaryLanIp", TestNetworkService);

                // Group 5: Server Handshake, Single-Connection & Error Paths
                await RunTestAsync("QuickShareServer_FullHandshakeAndSingleDataChannel", TestQuickShareServerHandshake);
                await RunTestAsync("QuickShareServer_RejectSecondConcurrentConnection", TestQuickShareServerRejectSecondConnection);
                await RunTestAsync("QuickShareServer_RejectInvalidMagicHeader", TestQuickShareServerRejectInvalidMagic);
                await RunTestAsync("QuickShareServer_RejectInvalidProtocolVersion", TestQuickShareServerRejectInvalidVersion);
                await RunTestAsync("QuickShareServer_DisconnectCleanupAndBufferReset", TestQuickShareServerDisconnectCleanup);

                // Group 6: Adversarial Network Failures & Abrupt Drop
                await RunTestAsync("Adversarial_ReceiveFileCallPrematureSocketTermination", TestReceiveFileCallAbruptDrop);
                await RunTestAsync("Adversarial_SendFileCallBrokenPipeHandling", TestSendFileCallBrokenPipe);
                RunTest("Adversarial_QuickShareDirectoryDeepPathScenarios", TestQuickShareDirectoryDeepPaths);

                // Group 7: End-to-End Loopback Transfer Simulation
                await RunTestAsync("E2E_SimulatedFileTransferOverSingleSocket", TestE2ESimulatedFileTransfer);

                // Group 8: QuickShareClient Verification & Dual-Mode Interop
                await RunTestAsync("QuickShareClient_FullHandshakeAndConnectedState", TestQuickShareClientHandshake);
                await RunTestAsync("QuickShareClient_SendFilesToServer", TestQuickShareClientSendFiles);
                await RunTestAsync("QuickShareClient_ReceiveFilesFromServer", TestQuickShareClientReceiveFiles);
                await RunTestAsync("QuickShareClient_ServerPushToClient", TestQuickShareClientServerPush);
                await RunTestAsync("QuickShareClient_ServerPullFromClient", TestQuickShareClientServerPull);
                await RunTestAsync("QuickShareClient_ConsecutiveBidirectionalTransfersOnSameConnection", TestQuickShareClientConsecutiveTransfers);
                await RunTestAsync("QuickShareClient_NestedDirectoryTransferAndTimestampPreservation", TestQuickShareClientNestedDirectoryTransfer);
                await RunTestAsync("QuickShareClient_ListRemoteFiles", TestQuickShareClientListRemoteFiles);
                await RunTestAsync("QuickShareClient_RejectVersionMismatch", TestQuickShareClientVersionMismatch);
                await RunTestAsync("QuickShareClient_AbruptDisconnectAndCleanup", TestQuickShareClientAbruptDisconnect);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n[FATAL RUNNER ERROR]: {ex}");
                Console.ResetColor();
                return 1;
            }

            sw.Stop();

            Console.WriteLine("\n===============================================================");
            Console.WriteLine($" Test Execution Completed in {sw.ElapsedMilliseconds} ms");
            Console.WriteLine($" Passed: {_passedCount}, Failed: {_failedCount}");
            Console.WriteLine("===============================================================");

            if (_failedCount > 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\nFAILURES:");
                foreach (var f in _failures)
                {
                    Console.WriteLine($"  - {f}");
                }
                Console.ResetColor();
                return 1;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("\nALL TESTS PASSED SUCCESSFULLY! (0 failures)");
            Console.ResetColor();
            return 0;
        }

        private static void RunTest(string name, Action test)
        {
            try
            {
                test();
                _passedCount++;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[PASS] {name}");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                _failedCount++;
                _failures.Add($"{name}: {ex.Message}");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[FAIL] {name}: {ex.Message}");
                Console.ResetColor();
            }
        }

        private static async Task RunTestAsync(string name, Func<Task> test)
        {
            try
            {
                await test();
                _passedCount++;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[PASS] {name}");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                _failedCount++;
                _failures.Add($"{name}: {ex.Message}");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[FAIL] {name}: {ex.Message}");
                Console.ResetColor();
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new Exception($"Assertion failed: {message}");
            }
        }

        // ====================================================================
        // Test Implementations
        // ====================================================================

        private static void TestProtocolConstants()
        {
            Assert(QuickShareConstants.CLIENT_HEADER == "HFXC", "Magic header must be 'HFXC'");
            Assert(QuickShareConstants.VERSION_CODE == 300, "Version code must be 300");
            Assert(QuickShareConstants.SHUTDOWN == 0, "SHUTDOWN opcode must be 0");
            Assert(QuickShareConstants.LIST_FILES == 1, "LIST_FILES opcode must be 1");
            Assert(QuickShareConstants.DELETE_FILE == 2, "DELETE_FILE opcode must be 2");
            Assert(QuickShareConstants.MKDIR == 3, "MKDIR opcode must be 3");
            Assert(QuickShareConstants.REQUEST_RECEIVE == 10, "REQUEST_RECEIVE opcode must be 10");
            Assert(QuickShareConstants.REQUEST_SEND == 11, "REQUEST_SEND opcode must be 11");

            Assert(QuickShareConstants.END_POINT == -1, "END_POINT sentinel must be -1");
            Assert(QuickShareConstants.FILE == 0, "FILE marker must be 0");
            Assert(QuickShareConstants.FOLDER == 1, "FOLDER marker must be 1");
            Assert(QuickShareConstants.FILE_SLICE == 2, "FILE_SLICE marker must be 2");
            Assert(QuickShareConstants.EOF == 3, "EOF marker must be 3");
            Assert(QuickShareConstants.END_OF_INTERRUPTED == 4, "END_OF_INTERRUPTED marker must be 4");
            Assert(QuickShareConstants.END_OF_READ_ERROR == 5, "END_OF_READ_ERROR marker must be 5");
            Assert(QuickShareConstants.END_OF_WRITE_ERROR == 6, "END_OF_WRITE_ERROR marker must be 6");

            Assert(FileBlock.BLOCK_SIZE == 1048576, "FileBlock.BLOCK_SIZE must be exactly 1MB (1048576 bytes)");
        }

        private static void TestQuickShareStreamBigEndianPrimitives()
        {
            using var ms = new MemoryStream();
            var stream = new QuickShareStream(ms);

            stream.WriteShort(0x1234);
            stream.WriteInt(0x12345678);
            stream.WriteLong(0x0102030405060708L);
            stream.WriteBoolean(true);
            stream.WriteBoolean(false);
            stream.WriteByte(0xAB);

            byte[] data = ms.ToArray();

            // Verify Big-Endian wire representation
            Assert(data[0] == 0x12 && data[1] == 0x34, "WriteShort must write Big-Endian bytes");
            Assert(data[2] == 0x12 && data[3] == 0x34 && data[4] == 0x56 && data[5] == 0x78, "WriteInt must write Big-Endian bytes");
            Assert(data[6] == 0x01 && data[13] == 0x08, "WriteLong must write Big-Endian bytes");
            Assert(data[14] == 1, "WriteBoolean true must be byte 1");
            Assert(data[15] == 0, "WriteBoolean false must be byte 0");
            Assert(data[16] == 0xAB, "WriteByte must write byte");

            // Read back
            ms.Position = 0;
            Assert(stream.ReadShort() == 0x1234, "ReadShort mismatch");
            Assert(stream.ReadInt() == 0x12345678, "ReadInt mismatch");
            Assert(stream.ReadLong() == 0x0102030405060708L, "ReadLong mismatch");
            Assert(stream.ReadBoolean() == true, "ReadBoolean true mismatch");
            Assert(stream.ReadBoolean() == false, "ReadBoolean false mismatch");
            Assert(stream.ReadByte() == 0xAB, "ReadByte mismatch");
        }

        private static async Task TestQuickShareStreamAsyncPrimitives()
        {
            using var ms = new MemoryStream();
            var stream = new QuickShareStream(ms);

            stream.WriteShort(-32000);
            stream.WriteInt(-123456789);
            stream.WriteLong(-9876543210123456L);
            stream.WriteBoolean(true);
            stream.WriteByte(255);
            stream.WriteUTF("Hello QuickShare Protocol 300 🚀 局域网快传");

            ms.Position = 0;
            Assert(await stream.ReadShortAsync() == -32000, "ReadShortAsync mismatch");
            Assert(await stream.ReadIntAsync() == -123456789, "ReadIntAsync mismatch");
            Assert(await stream.ReadLongAsync() == -9876543210123456L, "ReadLongAsync mismatch");
            Assert(await stream.ReadBooleanAsync() == true, "ReadBooleanAsync mismatch");
            Assert(await stream.ReadByteAsync() == 255, "ReadByteAsync mismatch");
            Assert(await stream.ReadUTFAsync() == "Hello QuickShare Protocol 300 🚀 局域网快传", "ReadUTFAsync mismatch");
        }

        private static async Task TestQuickShareStreamFragmentedReads()
        {
            // Simulate slow chunked stream where Read() returns 1 byte at a time
            var sourceData = new byte[100];
            new Random(42).NextBytes(sourceData);

            using var slowStream = new ChunkedReadStream(new MemoryStream(sourceData), chunkSize: 7);
            var stream = new QuickShareStream(slowStream);

            var readBuf = new byte[100];
            await stream.ReadFullyAsync(readBuf, 0, 100);

            for (int i = 0; i < 100; i++)
            {
                Assert(readBuf[i] == sourceData[i], $"Byte mismatch at index {i} during fragmented read");
            }
        }

        private static void TestQuickShareStreamUtfCodec()
        {
            using var ms = new MemoryStream();
            var stream = new QuickShareStream(ms);

            string testString = "Quick-Share 测试中文字符串 /path/to/test/file.mp4";
            stream.WriteUTF(testString);

            ms.Position = 0;
            string readBack = stream.ReadUTF();
            Assert(readBack == testString, "ReadUTF string mismatch");

            // Empty string
            ms.SetLength(0);
            stream.WriteUTF("");
            ms.Position = 0;
            Assert(stream.ReadUTF() == "", "Empty string UTF write/read failed");
        }

        private static void TestFileBlockMath()
        {
            // 0-byte file
            var zeroBlock = new FileBlock(true, 0, "zero.txt", 1000, 0, 0, null);
            Assert(zeroBlock.CalcBlockCount() == 1, "0-byte file should have 1 block count");
            Assert(zeroBlock.GetStartPosition() == 0, "0-byte start position should be 0");

            // 1-byte file
            var oneByteBlock = new FileBlock(true, 0, "one.txt", 1000, 1, 0, null);
            Assert(oneByteBlock.CalcBlockCount() == 1, "1-byte file should have 1 block count");

            // Exactly 1MB (1048576)
            var exact1MBBlock = new FileBlock(true, 0, "1mb.dat", 1000, 1048576, 0, null);
            Assert(exact1MBBlock.CalcBlockCount() == 1, "1MB file should have exactly 1 block");

            // 1MB + 1 byte
            var over1MBBlock = new FileBlock(true, 0, "over1mb.dat", 1000, 1048577, 1, null);
            Assert(over1MBBlock.CalcBlockCount() == 2, "1048577 bytes should have 2 blocks");
            Assert(over1MBBlock.GetStartPosition() == 1048576, "Block index 1 start position should be 1048576");

            // 5MB
            var fiveMBBlock = new FileBlock(true, 0, "5mb.dat", 1000, 5 * 1048576, 3, null);
            Assert(fiveMBBlock.CalcBlockCount() == 5, "5MB file should have 5 blocks");
            Assert(fiveMBBlock.GetStartPosition() == 3 * 1048576, "Block index 3 start position should be 3MB");
        }

        private static void TestFileBlockOrdering()
        {
            var b1 = new FileBlock(true, 0, "file1.txt", 100, 1000, 0, null);
            var b2 = new FileBlock(true, 0, "file1.txt", 100, 1000, 1, null);
            var b3 = new FileBlock(true, 1, "file2.txt", 100, 1000, 0, null);

            Assert(b1.CompareTo(b2) < 0, "b1 should come before b2 (same file, smaller chunk index)");
            Assert(b2.CompareTo(b1) > 0, "b2 should come after b1");
            Assert(b2.CompareTo(b3) < 0, "b2 should come before b3 (smaller fileIndex)");
            Assert(b1.CompareTo(b1) == 0, "b1 should equal b1");
        }

        private static void TestQuickShareDirectory()
        {
            var winDir = new QuickShareDirectory(@"C:\Users\test\Downloads", QuickShareDirectory.FILE_SYSTEM_WINDOWS);
            var unixDir = new QuickShareDirectory("/sdcard/Download", QuickShareDirectory.FILE_SYSTEM_UNIX);

            string relPath = @"C:\Users\test\Downloads\subfolder\test.jpg";
            string generated = winDir.GenerateTransferPath(relPath, unixDir);
            Assert(generated == "/sdcard/Download/subfolder/test.jpg", $"Path translation failed: expected /sdcard/Download/subfolder/test.jpg, got {generated}");
        }

        private static async Task TestWriteFileCallSequential()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "QuickShareTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string testFilePath = Path.Combine(tempDir, "write_test_3mb.bin");

            var buffers = new BlockingCollection<byte[]>();
            for (int i = 0; i < 8; i++)
            {
                buffers.Add(new byte[FileBlock.BLOCK_SIZE]);
            }

            var writeFileCall = new WriteFileCall(buffers, dequeCount: 1);
            var writeTask = Task.Run(() => writeFileCall.ExecuteAsync());

            int totalBlocks = 3;
            long totalSize = (long)totalBlocks * FileBlock.BLOCK_SIZE;

            for (int i = 0; i < totalBlocks; i++)
            {
                byte[] buf = writeFileCall.GetBuffer();
                // Fill with deterministic byte pattern
                for (int b = 0; b < FileBlock.BLOCK_SIZE; b++)
                {
                    buf[b] = (byte)((i * 37 + b) & 0xFF);
                }

                var block = new FileBlock(
                    isFile: true,
                    fileIndex: 0,
                    path: testFilePath,
                    lastModified: 1700000000000L,
                    totalSize: totalSize,
                    index: i,
                    data: buf,
                    dataLength: FileBlock.BLOCK_SIZE
                );

                writeFileCall.PutBlock(block);
            }

            writeFileCall.FinishChannel(0);
            await writeTask;

            // Verify file on disk
            Assert(File.Exists(testFilePath), "Written file must exist on disk");
            var fi = new FileInfo(testFilePath);
            Assert(fi.Length == totalSize, $"File size must be {totalSize}, got {fi.Length}");

            // Verify content
            using (var fs = new FileStream(testFilePath, FileMode.Open, FileAccess.Read))
            {
                byte[] readBuf = new byte[FileBlock.BLOCK_SIZE];
                for (int i = 0; i < totalBlocks; i++)
                {
                    int read = fs.Read(readBuf, 0, FileBlock.BLOCK_SIZE);
                    Assert(read == FileBlock.BLOCK_SIZE, $"Could not read full block {i}");
                    for (int b = 0; b < FileBlock.BLOCK_SIZE; b++)
                    {
                        byte expected = (byte)((i * 37 + b) & 0xFF);
                        if (readBuf[b] != expected)
                        {
                            throw new Exception($"Data corrupt at block {i}, offset {b}: expected {expected}, got {readBuf[b]}");
                        }
                    }
                }
            }

            // Verify all 8 buffers were returned to the pool (zero-copy recycling)
            Assert(buffers.Count == 8, $"Buffer leak! Expected 8 buffers recycled, but got {buffers.Count}");

            // Cleanup
            Directory.Delete(tempDir, true);
        }

        private static async Task TestWriteFileCallZeroByteAndDirectory()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "QuickShareTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string subDir = Path.Combine(tempDir, "sub", "folder");
            string zeroFilePath = Path.Combine(subDir, "empty.txt");

            var buffers = new BlockingCollection<byte[]>();
            for (int i = 0; i < 4; i++) buffers.Add(new byte[FileBlock.BLOCK_SIZE]);

            var writeFileCall = new WriteFileCall(buffers, 1);
            var writeTask = Task.Run(() => writeFileCall.ExecuteAsync());

            // Folder block
            writeFileCall.PutBlock(new FileBlock(false, 0, subDir, 1680000000000L, 0, 0, null));

            // 0-byte file block
            byte[] buf = writeFileCall.GetBuffer();
            writeFileCall.PutBlock(new FileBlock(true, 1, zeroFilePath, 1680000000000L, 0, 0, buf, 0));

            writeFileCall.FinishChannel(0);
            await writeTask;

            Assert(Directory.Exists(subDir), "Directory must have been created");
            Assert(File.Exists(zeroFilePath), "Zero-byte file must exist");
            Assert(new FileInfo(zeroFilePath).Length == 0, "File size must be 0");
            Assert(buffers.Count == 4, $"Buffer pool must have all 4 buffers, got {buffers.Count}");

            Directory.Delete(tempDir, true);
        }

        private static async Task TestWriteFileCallCancelLeakPrevention()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "QuickShareTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string filePath = Path.Combine(tempDir, "cancel_test.bin");

            var buffers = new BlockingCollection<byte[]>();
            for (int i = 0; i < 8; i++) buffers.Add(new byte[FileBlock.BLOCK_SIZE]);

            var writeFileCall = new WriteFileCall(buffers, 1);
            var writeTask = Task.Run(() => writeFileCall.ExecuteAsync());

            // Queue 3 blocks
            for (int i = 0; i < 3; i++)
            {
                byte[] b = writeFileCall.GetBuffer();
                writeFileCall.PutBlock(new FileBlock(true, 0, filePath, 1000, 3 * FileBlock.BLOCK_SIZE, i, b, FileBlock.BLOCK_SIZE));
            }

            // Immediately cancel
            writeFileCall.Cancel();
            try
            {
                await writeTask;
            }
            catch (Exception)
            {
                // Task cancellation or interruption is expected when cancelling
            }

            // All buffers should be recycled
            Assert(buffers.Count == 8, $"Buffer leak on cancel! Expected 8 buffers, got {buffers.Count}");

            try { Directory.Delete(tempDir, true); } catch { }
        }

        private static async Task TestReadFileCallSlicing()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "QuickShareReadTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string testFile = Path.Combine(tempDir, "read_test_2_5mb.bin");

            // Write 2.5MB test file (2 * 1048576 + 524288 bytes)
            long fileSize = 2 * FileBlock.BLOCK_SIZE + 524288;
            using (var fs = new FileStream(testFile, FileMode.Create, FileAccess.Write))
            {
                byte[] chunk = new byte[65536];
                for (int i = 0; i < chunk.Length; i++) chunk[i] = (byte)(i % 256);
                long written = 0;
                while (written < fileSize)
                {
                    int toWrite = (int)Math.Min(chunk.Length, fileSize - written);
                    fs.Write(chunk, 0, toWrite);
                    written += toWrite;
                }
            }

            var buffers = new BlockingCollection<byte[]>();
            for (int i = 0; i < 8; i++) buffers.Add(new byte[FileBlock.BLOCK_SIZE]);

            var remoteFiles = new List<RemoteFile>
            {
                new RemoteFile(Path.GetFileName(testFile), testFile, 1700000000000L, fileSize, false)
            };

            var localDir = new QuickShareDirectory(tempDir, QuickShareDirectory.FILE_SYSTEM_WINDOWS);
            var remoteDir = new QuickShareDirectory(@"C:\Dest", QuickShareDirectory.FILE_SYSTEM_WINDOWS);

            var readFileCall = new ReadFileCall(buffers, remoteFiles, localDir, remoteDir, operateThreadCount: 1);
            var readTask = Task.Run(() => readFileCall.ExecuteAsync());

            // Take blocks
            var takenBlocks = new List<FileBlock>();
            while (true)
            {
                var block = readFileCall.TakeBlock();
                if (block == ReadFileCall.END_POINT)
                {
                    break;
                }
                takenBlocks.Add(block);
            }

            await readTask;

            Assert(takenBlocks.Count == 3, $"Expected 3 sliced blocks, got {takenBlocks.Count}");
            Assert(takenBlocks[0].Index == 0 && takenBlocks[0].DataLength == FileBlock.BLOCK_SIZE, "Block 0 length mismatch");
            Assert(takenBlocks[1].Index == 1 && takenBlocks[1].DataLength == FileBlock.BLOCK_SIZE, "Block 1 length mismatch");
            Assert(takenBlocks[2].Index == 2 && takenBlocks[2].DataLength == 524288, "Block 2 length mismatch (should be 524288 bytes)");

            // Recycle taken buffers
            foreach (var b in takenBlocks)
            {
                if (b.Data != null) readFileCall.RecycleBuffer(b.Data);
            }

            Assert(buffers.Count == 8, $"All buffers should be recycled back to pool. Got {buffers.Count}");

            Directory.Delete(tempDir, true);
        }

        private static void TestReadFileCallSentinels()
        {
            var buffers = new BlockingCollection<byte[]>();
            for (int i = 0; i < 4; i++) buffers.Add(new byte[FileBlock.BLOCK_SIZE]);

            var readFileCall = new ReadFileCall(buffers, new List<RemoteFile>(), new QuickShareDirectory("", 1), new QuickShareDirectory("", 1), 1);

            // Put a buffer
            byte[] buf = buffers.Take();
            // Test ShutdownByWriteError
            readFileCall.ShutdownByWriteError();

            var sentinel = readFileCall.TakeBlock();
            Assert(sentinel == ReadFileCall.WRITE_ERROR, "Should take WRITE_ERROR sentinel");

            // Test ShutdownByConnectionBreak
            readFileCall.ShutdownByConnectionBreak();
            sentinel = readFileCall.TakeBlock();
            Assert(sentinel == ReadFileCall.INTERRUPT, "Should take INTERRUPT sentinel");
        }

        private static void TestNetworkService()
        {
            var service = new NetworkService();
            var nics = service.GetAvailableInterfaces();
            string primaryIp = service.GetPrimaryLanIpAddress();
            var primaryNic = service.GetPrimaryLanInterface();

            Assert(!string.IsNullOrEmpty(primaryIp), "Primary LAN IP must not be empty");
            Assert(IPAddress.TryParse(primaryIp, out _), $"Primary LAN IP ({primaryIp}) must be a valid IPv4 address");
            Assert(!primaryIp.StartsWith("127.") && !primaryIp.StartsWith("169.254.") || nics.Count == 0, "Primary IP should not be loopback or link-local unless no NIC is present");
        }

        private static async Task TestQuickShareServerHandshake()
        {
            int testPort = GetAvailablePort();
            var server = new QuickShareServer();
            server.Start(testPort);

            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, testPort);
                client.NoDelay = true;

                var stream = new QuickShareStream(client.GetStream());

                // 1. Send Header & Version
                stream.BaseStream.Write(Encoding.UTF8.GetBytes(QuickShareConstants.CLIENT_HEADER), 0, 4);
                stream.WriteInt(QuickShareConstants.VERSION_CODE);
                await stream.BaseStream.FlushAsync();

                // 2. Read versionMatched
                bool versionMatched = await stream.ReadBooleanAsync();
                Assert(versionMatched, "Handshake versionMatched must be true");

                // 3. Read advertised interfaces (single LAN stream -> count == 1)
                int serverNicCount = await stream.ReadIntAsync();
                Assert(serverNicCount == 1, $"Server must advertise serverNicCount = 1 for LAN streaming. Got {serverNicCount}");

                string serverNicName = await stream.ReadUTFAsync();
                int ipLen = await stream.ReadByteAsync();
                byte[] ipBytes = new byte[ipLen];
                await stream.ReadFullyAsync(ipBytes, 0, ipLen);
                byte bindAddressFlag = await stream.ReadByteAsync();
                Assert(bindAddressFlag == 0, "Bind address flag must be 0");

                // 4. Connect 1 data socket
                stream.WriteBoolean(true); // clientSucceed
                stream.WriteUTF("lan_0"); // clientInterfaceName
                await stream.BaseStream.FlushAsync();

                using var dataClient = new TcpClient();
                await dataClient.ConnectAsync(IPAddress.Loopback, testPort);
                dataClient.NoDelay = true;

                bool dataChannelAccepted = await stream.ReadBooleanAsync();
                Assert(dataChannelAccepted, "Server must accept data channel connection");

                // 5. Buffer Negotiation
                int serverBufCount = await stream.ReadIntAsync();
                Assert(serverBufCount == 8, $"Server should request 8 buffers, got {serverBufCount}");

                stream.WriteBoolean(true); // clientBufferOk
                await stream.BaseStream.FlushAsync();

                bool serverBufferOk = await stream.ReadBooleanAsync();
                Assert(serverBufferOk, "Server buffer allocation must succeed");

                // 6. Client File System Info
                stream.WriteInt(QuickShareDirectory.FILE_SYSTEM_UNIX);
                stream.WriteUTF("/sdcard/Download");
                await stream.BaseStream.FlushAsync();

                // Small delay to let server process handshake completion
                await Task.Delay(50);

                Assert(server.IsConnected, "Server IsConnected must be true");
                Assert(server.RemoteFileSystem == QuickShareDirectory.FILE_SYSTEM_UNIX, "Server must record remote filesystem");
                Assert(server.RemoteHomeDir == "/sdcard/Download", "Server must record remote home dir");

                // Test RPC operations
                var listTask = Task.Run(() => server.ListRemoteFilesAsync("/sdcard/Download"));
                // Client receives LIST_FILES command
                short op = await stream.ReadShortAsync();
                Assert(op == QuickShareConstants.LIST_FILES, $"Expected LIST_FILES opcode (1), got {op}");
                string reqPath = await stream.ReadUTFAsync();
                Assert(reqPath == "/sdcard/Download", "LIST_FILES path mismatch");

                // Client replies with 1 file
                stream.WriteInt(1);
                stream.WriteUTF("photo.jpg");
                stream.WriteUTF("/sdcard/Download/photo.jpg");
                stream.WriteLong(1700000000000L);
                stream.WriteLong(2048576L);
                stream.WriteBoolean(false);
                await stream.BaseStream.FlushAsync();

                var files = await listTask;
                Assert(files != null && files.Count == 1, "Server should receive 1 remote file");
                Assert(files![0].Name == "photo.jpg" && files[0].Size == 2048576L, "Remote file metadata mismatch");
            }
            finally
            {
                server.Stop();
            }
        }

        private static async Task TestQuickShareServerRejectSecondConnection()
        {
            int testPort = GetAvailablePort();
            var server = new QuickShareServer();
            server.Start(testPort);

            try
            {
                // First client connects
                using var client1 = new TcpClient();
                await client1.ConnectAsync(IPAddress.Loopback, testPort);
                var stream1 = new QuickShareStream(client1.GetStream());

                stream1.BaseStream.Write(Encoding.UTF8.GetBytes("HFXC"), 0, 4);
                stream1.WriteInt(300);
                await stream1.BaseStream.FlushAsync();
                Assert(await stream1.ReadBooleanAsync(), "Client 1 version matched");
                await stream1.ReadIntAsync(); // serverNicCount
                await stream1.ReadUTFAsync(); // nicName
                int ipLen = await stream1.ReadByteAsync();
                byte[] ipB = new byte[ipLen];
                await stream1.ReadFullyAsync(ipB, 0, ipLen);
                await stream1.ReadByteAsync(); // flag

                stream1.WriteBoolean(true);
                stream1.WriteUTF("lan_0");
                await stream1.BaseStream.FlushAsync();

                using var dataClient1 = new TcpClient();
                await dataClient1.ConnectAsync(IPAddress.Loopback, testPort);
                await stream1.ReadBooleanAsync(); // dataChannelAccepted
                await stream1.ReadIntAsync(); // bufCount
                stream1.WriteBoolean(true);
                await stream1.BaseStream.FlushAsync();
                await stream1.ReadBooleanAsync(); // serverBufferOk
                stream1.WriteInt(1);
                stream1.WriteUTF("C:\\");
                await stream1.BaseStream.FlushAsync();

                await Task.Delay(50);
                Assert(server.IsConnected, "Server should be connected to Client 1");

                // Second client attempts to connect
                using var client2 = new TcpClient();
                await client2.ConnectAsync(IPAddress.Loopback, testPort);

                // Server should reject and close client2
                var stream2 = client2.GetStream();
                byte[] dummy = new byte[4];
                int read = 0;
                try
                {
                    read = await stream2.ReadAsync(dummy, 0, 4);
                }
                catch { }

                Assert(read == 0, "Second client socket should be closed by server because already connected");
            }
            finally
            {
                server.Stop();
            }
        }

        private static async Task TestQuickShareServerRejectInvalidMagic()
        {
            int testPort = GetAvailablePort();
            var server = new QuickShareServer();
            server.Start(testPort);

            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, testPort);
                var stream = new QuickShareStream(client.GetStream());

                // Send bad magic "BAD!"
                stream.BaseStream.Write(Encoding.UTF8.GetBytes("BAD!"), 0, 4);
                await stream.BaseStream.FlushAsync();

                await Task.Delay(100);
                Assert(!server.IsConnected, "Server should not connect with bad magic header");
            }
            finally
            {
                server.Stop();
            }
        }

        private static async Task TestQuickShareServerRejectInvalidVersion()
        {
            int testPort = GetAvailablePort();
            var server = new QuickShareServer();
            server.Start(testPort);

            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, testPort);
                var stream = new QuickShareStream(client.GetStream());

                stream.BaseStream.Write(Encoding.UTF8.GetBytes("HFXC"), 0, 4);
                stream.WriteInt(999); // Invalid version
                await stream.BaseStream.FlushAsync();

                bool versionMatched = await stream.ReadBooleanAsync();
                Assert(!versionMatched, "Server should reject version 999");
                int serverVer = await stream.ReadIntAsync();
                Assert(serverVer == 300, $"Server should return supported version 300, got {serverVer}");

                await Task.Delay(50);
                Assert(!server.IsConnected, "Server should not be connected after version mismatch");
            }
            finally
            {
                server.Stop();
            }
        }

        private static async Task TestQuickShareServerDisconnectCleanup()
        {
            int testPort = GetAvailablePort();
            var server = new QuickShareServer();
            server.Start(testPort);

            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, testPort);
                var stream = new QuickShareStream(client.GetStream());

                // Complete handshake
                stream.BaseStream.Write(Encoding.UTF8.GetBytes("HFXC"), 0, 4);
                stream.WriteInt(300);
                await stream.BaseStream.FlushAsync();
                await stream.ReadBooleanAsync();
                await stream.ReadIntAsync();
                await stream.ReadUTFAsync();
                int iLen = await stream.ReadByteAsync();
                byte[] ipB = new byte[iLen];
                await stream.ReadFullyAsync(ipB, 0, iLen);
                await stream.ReadByteAsync();

                stream.WriteBoolean(true);
                stream.WriteUTF("lan_0");
                await stream.BaseStream.FlushAsync();

                using var dataClient = new TcpClient();
                await dataClient.ConnectAsync(IPAddress.Loopback, testPort);
                await stream.ReadBooleanAsync();
                await stream.ReadIntAsync();
                stream.WriteBoolean(true);
                await stream.BaseStream.FlushAsync();
                await stream.ReadBooleanAsync();
                stream.WriteInt(0);
                stream.WriteUTF("/home");
                await stream.BaseStream.FlushAsync();

                await Task.Delay(50);
                Assert(server.IsConnected, "Server should be connected");

                // Disconnect
                server.DisconnectCurrentDevice();
                Assert(!server.IsConnected, "Server IsConnected should be false after DisconnectCurrentDevice");
                Assert(string.IsNullOrEmpty(server.ConnectedDeviceIP), "ConnectedDeviceIP should be cleared");
                Assert(server.RemoteFileSystem == 0, "RemoteFileSystem should be reset");
            }
            finally
            {
                server.Stop();
            }
        }

        private static async Task TestE2ESimulatedFileTransfer()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "QuickShareE2ETest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string srcFile = Path.Combine(tempDir, "src_payload.bin");
            string dstFile = Path.Combine(tempDir, "dst_payload.bin");

            // Create 3.2MB file (3 chunks)
            long payloadSize = 3 * 1048576 + 200000;
            byte[] originalBytes = new byte[payloadSize];
            new Random(12345).NextBytes(originalBytes);
            File.WriteAllBytes(srcFile, originalBytes);

            int testPort = 58496;
            var server = new QuickShareServer();
            server.Start(testPort);

            try
            {
                // Connect simulated Android client
                using var ctrlClient = new TcpClient();
                await ctrlClient.ConnectAsync(IPAddress.Loopback, testPort);
                ctrlClient.NoDelay = true;
                var ctrlStream = new QuickShareStream(ctrlClient.GetStream());

                // Handshake
                ctrlStream.BaseStream.Write(Encoding.UTF8.GetBytes("HFXC"), 0, 4);
                ctrlStream.WriteInt(300);
                await ctrlStream.BaseStream.FlushAsync();
                await ctrlStream.ReadBooleanAsync();
                await ctrlStream.ReadIntAsync();
                await ctrlStream.ReadUTFAsync();
                int ipL = await ctrlStream.ReadByteAsync();
                byte[] ipB = new byte[ipL];
                await ctrlStream.ReadFullyAsync(ipB, 0, ipL);
                await ctrlStream.ReadByteAsync();

                ctrlStream.WriteBoolean(true);
                ctrlStream.WriteUTF("lan_0");
                await ctrlStream.BaseStream.FlushAsync();

                using var dataClient = new TcpClient();
                await dataClient.ConnectAsync(IPAddress.Loopback, testPort);
                dataClient.NoDelay = true;
                var dataStream = new QuickShareStream(dataClient.GetStream());

                await ctrlStream.ReadBooleanAsync();
                await ctrlStream.ReadIntAsync();
                ctrlStream.WriteBoolean(true);
                await ctrlStream.BaseStream.FlushAsync();
                await ctrlStream.ReadBooleanAsync();
                ctrlStream.WriteInt(1);
                ctrlStream.WriteUTF("C:\\Destination");
                await ctrlStream.BaseStream.FlushAsync();

                await Task.Delay(50);

                // Simulation: PC Server calls SendFilesToRemoteAsync -> client receives over single data channel
                var serverSendTask = Task.Run(async () =>
                {
                    await server.SendFilesToRemoteAsync(new List<string> { srcFile }, @"C:\Destination");
                });

                // Client side receives REQUEST_RECEIVE on control channel
                short cmd = await ctrlStream.ReadShortAsync();
                Assert(cmd == QuickShareConstants.REQUEST_RECEIVE, $"Expected REQUEST_RECEIVE (10), got {cmd}");

                // Client reads data frames from data channel
                var clientReceivedBytes = new List<byte>();
                while (true)
                {
                    short frameHeader = await dataStream.ReadShortAsync();
                    if (frameHeader == QuickShareConstants.EOF)
                    {
                        break;
                    }
                    Assert(frameHeader == QuickShareConstants.FILE, $"Expected FILE frame (0), got {frameHeader}");

                    int fIndex = await dataStream.ReadIntAsync();
                    string path = await dataStream.ReadUTFAsync();
                    long lastMod = await dataStream.ReadLongAsync();
                    long totSize = await dataStream.ReadLongAsync();
                    int chunkIdx = await dataStream.ReadIntAsync();
                    int chunkLen = await dataStream.ReadIntAsync();

                    byte[] chunkBuf = new byte[chunkLen];
                    await dataStream.ReadFullyAsync(chunkBuf, 0, chunkLen);
                    clientReceivedBytes.AddRange(chunkBuf);
                }

                // Client writes true (write ok) on control channel
                ctrlStream.WriteBoolean(true);
                await ctrlStream.BaseStream.FlushAsync();

                // Client reads server completion ack
                bool serverAck = await ctrlStream.ReadBooleanAsync();
                Assert(serverAck, "Server should ack successful completion");

                await serverSendTask;

                Assert(clientReceivedBytes.Count == payloadSize, $"Transferred byte count mismatch: expected {payloadSize}, got {clientReceivedBytes.Count}");

                for (int i = 0; i < payloadSize; i++)
                {
                    if (clientReceivedBytes[i] != originalBytes[i])
                    {
                        throw new Exception($"Byte corrupted at offset {i}: expected {originalBytes[i]}, got {clientReceivedBytes[i]}");
                    }
                }
            }
            finally
            {
                server.Stop();
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private static async Task TestReceiveFileCallAbruptDrop()
        {
            // Simulate socket abruptly dropping in middle of 1MB chunk
            var buffers = new BlockingCollection<byte[]>();
            for (int i = 0; i < 4; i++) buffers.Add(new byte[FileBlock.BLOCK_SIZE]);

            var writeFileCall = new WriteFileCall(buffers, 1);

            // Construct pipe stream with incomplete payload
            using var ms = new MemoryStream();
            var stream = new QuickShareStream(ms);

            // Write FILE frame header indicating 1MB payload
            stream.WriteShort(QuickShareConstants.FILE);
            stream.WriteInt(0); // fileIndex
            stream.WriteUTF("partial.bin");
            stream.WriteLong(1700000000000L);
            stream.WriteLong(1048576L); // totalSize
            stream.WriteInt(0); // chunk index
            stream.WriteInt(1048576); // claimed chunk length = 1MB

            // Write only 100 bytes of payload instead of 1MB, then rewind
            byte[] truncatedBytes = new byte[100];
            ms.Write(truncatedBytes, 0, 100);
            ms.Position = 0;

            var conn = new TransferConnection("lan_0", stream);
            bool errorTriggered = false;

            var recvCall = new ReceiveFileCall(
                0,
                conn,
                writeFileCall,
                (i, p, c, t) => { },
                (i, t, m) => { },
                (i, code, err) =>
                {
                    errorTriggered = true;
                }
            );

            try
            {
                await recvCall.ExecuteAsync();
                Assert(false, "ReceiveFileCall should have thrown EndOfStreamException on truncated stream");
            }
            catch (EndOfStreamException)
            {
                // Expected
                Assert(errorTriggered, "onError callback should have fired on unexpected stream termination");
            }
        }

        private static async Task TestSendFileCallBrokenPipe()
        {
            var buffers = new BlockingCollection<byte[]>();
            for (int i = 0; i < 4; i++) buffers.Add(new byte[FileBlock.BLOCK_SIZE]);

            string tempDir = Path.Combine(Path.GetTempPath(), "QuickShareBrokenPipe_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string testFile = Path.Combine(tempDir, "broken_pipe.txt");
            File.WriteAllText(testFile, "Hello Broken Pipe");

            var remoteFiles = new List<RemoteFile>
            {
                new RemoteFile("broken_pipe.txt", testFile, 1000L, 17L, false)
            };

            var readFileCall = new ReadFileCall(buffers, remoteFiles, new QuickShareDirectory(tempDir, 1), new QuickShareDirectory("", 1), 1);
            var readTask = Task.Run(() => readFileCall.ExecuteAsync());

            // Create a closed memory stream to simulate broken socket
            var ms = new MemoryStream();
            ms.Close(); // closed

            var conn = new TransferConnection("lan_0", new QuickShareStream(ms));
            bool errorTriggered = false;

            var sendCall = new SendFileCall(
                readFileCall,
                conn,
                (i, p, c, t) => { },
                (i, t, m) => { },
                (i, code, err) => { errorTriggered = true; }
            );

            try
            {
                await sendCall.ExecuteAsync();
            }
            catch (Exception)
            {
                Assert(errorTriggered, "onError callback should have fired when sending on broken channel");
            }
            finally
            {
                try { await readTask; } catch { }
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private static void TestQuickShareDirectoryDeepPaths()
        {
            var winDir = new QuickShareDirectory(@"D:\Data\Transfer\", QuickShareDirectory.FILE_SYSTEM_WINDOWS);
            var unixDir = new QuickShareDirectory("/storage/emulated/0/Download", QuickShareDirectory.FILE_SYSTEM_UNIX);

            string deepWinPath = @"D:\Data\Transfer\level1\level2\level3\deep_file.txt";
            string outUnix = winDir.GenerateTransferPath(deepWinPath, unixDir);
            Assert(outUnix == "/storage/emulated/0/Download/level1/level2/level3/deep_file.txt", $"Deep path mismatch: got {outUnix}");

            var reverseUnix = new QuickShareDirectory("/storage/emulated/0/Download", QuickShareDirectory.FILE_SYSTEM_UNIX);
            var reverseWin = new QuickShareDirectory(@"D:\Data\Transfer", QuickShareDirectory.FILE_SYSTEM_WINDOWS);
            string deepUnixPath = "/storage/emulated/0/Download/a/b/c/file.pdf";
            string outWin = reverseUnix.GenerateTransferPath(deepUnixPath, reverseWin);
            Assert(outWin == @"D:\Data\Transfer\a\b\c\file.pdf", $"Reverse path mismatch: got {outWin}");
        }

        // ====================================================================
        // Group 8: QuickShareClient Tests & Interoperability
        // ====================================================================

        private static int GetAvailablePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static async Task TestQuickShareClientHandshake()
        {
            int testPort = GetAvailablePort();
            var server = new QuickShareServer();
            server.Start(testPort);

            var client = new QuickShareClient();

            try
            {
                bool connected = await client.ConnectAsync("127.0.0.1", testPort);
                Assert(connected, "ConnectAsync should return true");
                Assert(client.IsConnected, "client.IsConnected must be true");
                Assert(client.ConnectedServerIp == "127.0.0.1", $"IP mismatch: {client.ConnectedServerIp}");
                Assert(client.ConnectedServerPort == testPort, $"Port mismatch: {client.ConnectedServerPort}");
                Assert(server.IsConnected, "server.IsConnected must be true");

                client.Disconnect();
                Assert(!client.IsConnected, "client.IsConnected must be false after Disconnect");
            }
            finally
            {
                client.Disconnect();
                server.Stop();
            }
        }

        private static async Task TestQuickShareClientSendFiles()
        {
            int testPort = GetAvailablePort();
            var server = new QuickShareServer();
            server.Start(testPort);

            string serverDir = Path.Combine(Path.GetTempPath(), "QSServerSave_" + Guid.NewGuid().ToString("N"));
            string clientDir = Path.Combine(Path.GetTempPath(), "QSClientSend_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(serverDir);
            Directory.CreateDirectory(clientDir);
            server.SaveDirectory = serverDir;

            var client = new QuickShareClient();

            try
            {
                // Create a 2.5MB test file to test multi-block streaming
                string srcFile = Path.Combine(clientDir, "test_client_upload.bin");
                byte[] data = new byte[2621440];
                new Random(12345).NextBytes(data);
                File.WriteAllBytes(srcFile, data);

                await client.ConnectAsync("127.0.0.1", testPort);
                Assert(client.IsConnected, "Client must be connected");

                bool success = await client.SendFilesAsync(new List<string> { srcFile }, serverDir);
                Assert(success, "SendFilesAsync should succeed");

                await Task.Delay(100);

                string receivedFile = Path.Combine(serverDir, "test_client_upload.bin");
                Assert(File.Exists(receivedFile), $"Server did not receive file at {receivedFile}");

                byte[] receivedData = File.ReadAllBytes(receivedFile);
                Assert(receivedData.Length == data.Length, $"Data length mismatch: {receivedData.Length} != {data.Length}");

                for (int i = 0; i < data.Length; i++)
                {
                    if (data[i] != receivedData[i])
                    {
                        throw new Exception($"Data mismatch at byte offset {i}");
                    }
                }
            }
            finally
            {
                client.Disconnect();
                server.Stop();
                try { Directory.Delete(serverDir, true); } catch { }
                try { Directory.Delete(clientDir, true); } catch { }
            }
        }

        private static async Task TestQuickShareClientReceiveFiles()
        {
            int testPort = GetAvailablePort();
            var server = new QuickShareServer();
            server.Start(testPort);

            string serverDir = Path.Combine(Path.GetTempPath(), "QSServerData_" + Guid.NewGuid().ToString("N"));
            string clientDir = Path.Combine(Path.GetTempPath(), "QSClientRecv_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(serverDir);
            Directory.CreateDirectory(clientDir);

            var client = new QuickShareClient();
            client.SaveDirectory = clientDir;

            try
            {
                // Create a 1.2MB test file on server
                string serverFile = Path.Combine(serverDir, "test_client_pull.bin");
                byte[] data = new byte[1258291];
                new Random(54321).NextBytes(data);
                File.WriteAllBytes(serverFile, data);

                await client.ConnectAsync("127.0.0.1", testPort);
                Assert(client.IsConnected, "Client must be connected");

                bool success = await client.ReceiveFilesAsync(new List<string> { serverFile }, serverDir, clientDir);
                Assert(success, "ReceiveFilesAsync should succeed");

                await Task.Delay(100);

                string receivedFile = Path.Combine(clientDir, "test_client_pull.bin");
                Assert(File.Exists(receivedFile), $"Client did not receive pulled file at {receivedFile}");

                byte[] receivedData = File.ReadAllBytes(receivedFile);
                Assert(receivedData.Length == data.Length, $"Pulled data length mismatch: {receivedData.Length} != {data.Length}");

                for (int i = 0; i < data.Length; i++)
                {
                    if (data[i] != receivedData[i])
                    {
                        throw new Exception($"Data mismatch at byte offset {i}");
                    }
                }
            }
            finally
            {
                client.Disconnect();
                server.Stop();
                try { Directory.Delete(serverDir, true); } catch { }
                try { Directory.Delete(clientDir, true); } catch { }
            }
        }

        private static async Task TestQuickShareClientServerPush()
        {
            int testPort = GetAvailablePort();
            var server = new QuickShareServer();
            server.Start(testPort);

            string serverDir = Path.Combine(Path.GetTempPath(), "QSServerSrc_" + Guid.NewGuid().ToString("N"));
            string clientDir = Path.Combine(Path.GetTempPath(), "QSClientDest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(serverDir);
            Directory.CreateDirectory(clientDir);

            var client = new QuickShareClient();
            client.SaveDirectory = clientDir;

            try
            {
                // Create file on server to push to client
                string serverFile = Path.Combine(serverDir, "server_push.bin");
                byte[] data = new byte[524288]; // 512KB
                new Random(9999).NextBytes(data);
                File.WriteAllBytes(serverFile, data);

                await client.ConnectAsync("127.0.0.1", testPort);
                Assert(client.IsConnected, "Client must be connected");

                // Server pushes to client
                await server.SendFilesToRemoteAsync(new List<string> { serverFile }, clientDir);

                await Task.Delay(100);

                string receivedFile = Path.Combine(clientDir, "server_push.bin");
                Assert(File.Exists(receivedFile), $"Client did not receive pushed file at {receivedFile}");

                byte[] receivedData = File.ReadAllBytes(receivedFile);
                Assert(receivedData.Length == data.Length, $"Data length mismatch: {receivedData.Length} != {data.Length}");

                for (int i = 0; i < data.Length; i++)
                {
                    if (data[i] != receivedData[i])
                    {
                        throw new Exception($"Data mismatch at byte offset {i}");
                    }
                }
            }
            finally
            {
                client.Disconnect();
                server.Stop();
                try { Directory.Delete(serverDir, true); } catch { }
                try { Directory.Delete(clientDir, true); } catch { }
            }
        }

        private static async Task TestQuickShareClientServerPull()
        {
            int testPort = GetAvailablePort();
            var server = new QuickShareServer();
            string serverDir = Path.Combine(Path.GetTempPath(), "QSServerPullDst_" + Guid.NewGuid().ToString("N"));
            string clientDir = Path.Combine(Path.GetTempPath(), "QSClientPullSrc_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(serverDir);
            Directory.CreateDirectory(clientDir);
            server.SaveDirectory = serverDir;
            server.Start(testPort);

            var client = new QuickShareClient();
            client.SaveDirectory = clientDir;

            try
            {
                // Create file on client to be pulled by server
                string clientFile = Path.Combine(clientDir, "client_pull_target.bin");
                byte[] data = new byte[768000]; // 750KB
                new Random(8888).NextBytes(data);
                File.WriteAllBytes(clientFile, data);

                await client.ConnectAsync("127.0.0.1", testPort);
                Assert(client.IsConnected, "Client must be connected");

                // Server pulls from client
                await server.PullFilesFromRemoteAsync(new List<string> { clientFile }, serverDir);

                await Task.Delay(100);

                string receivedFile = Path.Combine(serverDir, "client_pull_target.bin");
                Assert(File.Exists(receivedFile), $"Server did not receive pulled file at {receivedFile}");

                byte[] receivedData = File.ReadAllBytes(receivedFile);
                Assert(receivedData.Length == data.Length, $"Data length mismatch: {receivedData.Length} != {data.Length}");

                for (int i = 0; i < data.Length; i++)
                {
                    if (data[i] != receivedData[i])
                    {
                        throw new Exception($"Data mismatch at byte offset {i}");
                    }
                }
            }
            finally
            {
                client.Disconnect();
                server.Stop();
                try { Directory.Delete(serverDir, true); } catch { }
                try { Directory.Delete(clientDir, true); } catch { }
            }
        }

        private static async Task TestQuickShareClientConsecutiveTransfers()
        {
            int testPort = GetAvailablePort();
            var server = new QuickShareServer();
            string serverDir = Path.Combine(Path.GetTempPath(), "QSServerMulti_" + Guid.NewGuid().ToString("N"));
            string clientDir = Path.Combine(Path.GetTempPath(), "QSClientMulti_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(serverDir);
            Directory.CreateDirectory(clientDir);
            server.SaveDirectory = serverDir;
            server.Start(testPort);

            var client = new QuickShareClient();
            client.SaveDirectory = clientDir;

            try
            {
                await client.ConnectAsync("127.0.0.1", testPort);
                Assert(client.IsConnected, "Client must be connected");

                // 1. Client push to Server
                string f1 = Path.Combine(clientDir, "multi_1_c2s.bin");
                byte[] d1 = new byte[102400];
                new Random(111).NextBytes(d1);
                File.WriteAllBytes(f1, d1);
                bool ok1 = await client.SendFilesAsync(new List<string> { f1 }, serverDir);
                Assert(ok1, "Multi step 1 (Client push) should succeed");
                Assert(File.Exists(Path.Combine(serverDir, "multi_1_c2s.bin")), "Server should have multi_1_c2s.bin");

                // 2. Server pull from Client
                string f2 = Path.Combine(clientDir, "multi_2_c2s_pull.bin");
                byte[] d2 = new byte[150000];
                new Random(222).NextBytes(d2);
                File.WriteAllBytes(f2, d2);
                await server.PullFilesFromRemoteAsync(new List<string> { f2 }, serverDir);
                Assert(File.Exists(Path.Combine(serverDir, "multi_2_c2s_pull.bin")), "Server should have multi_2_c2s_pull.bin");

                // 3. Server push to Client
                string f3 = Path.Combine(serverDir, "multi_3_s2c.bin");
                byte[] d3 = new byte[204800];
                new Random(333).NextBytes(d3);
                File.WriteAllBytes(f3, d3);
                await server.SendFilesToRemoteAsync(new List<string> { f3 }, clientDir);
                Assert(File.Exists(Path.Combine(clientDir, "multi_3_s2c.bin")), "Client should have multi_3_s2c.bin");

                // 4. Client pull from Server
                string f4 = Path.Combine(serverDir, "multi_4_s2c_pull.bin");
                byte[] d4 = new byte[256000];
                new Random(444).NextBytes(d4);
                File.WriteAllBytes(f4, d4);
                bool ok4 = await client.ReceiveFilesAsync(new List<string> { f4 }, serverDir, clientDir);
                Assert(ok4, "Multi step 4 (Client pull) should succeed");
                Assert(File.Exists(Path.Combine(clientDir, "multi_4_s2c_pull.bin")), "Client should have multi_4_s2c_pull.bin");

                // 5. RPC List Remote Files on same active connection
                var remoteFiles = await client.ListRemoteFilesAsync(serverDir);
                Assert(remoteFiles != null && remoteFiles.Count >= 4, "RPC ListRemoteFiles on persistent connection should return all files");

                // 6. RPC Create Remote Dir on same active connection
                bool mkOk = await client.CreateRemoteDirAsync(serverDir, "sub_created_by_client");
                Assert(mkOk, "RPC CreateRemoteDir on persistent connection should succeed");
                Assert(Directory.Exists(Path.Combine(serverDir, "sub_created_by_client")), "Created remote directory should exist");
            }
            finally
            {
                client.Disconnect();
                server.Stop();
                try { Directory.Delete(serverDir, true); } catch { }
                try { Directory.Delete(clientDir, true); } catch { }
            }
        }

        private static async Task TestQuickShareClientNestedDirectoryTransfer()
        {
            int testPort = GetAvailablePort();
            var server = new QuickShareServer();
            string serverDir = Path.Combine(Path.GetTempPath(), "QSServerDirDst_" + Guid.NewGuid().ToString("N"));
            string clientDir = Path.Combine(Path.GetTempPath(), "QSClientDirSrc_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(serverDir);
            Directory.CreateDirectory(clientDir);
            server.SaveDirectory = serverDir;
            server.Start(testPort);

            var client = new QuickShareClient();
            client.SaveDirectory = clientDir;

            try
            {
                // Create nested tree: clientDir/rootFolder/subFolder/nested.bin
                string rootFolder = Path.Combine(clientDir, "rootFolder");
                string subFolder = Path.Combine(rootFolder, "subFolder");
                Directory.CreateDirectory(subFolder);

                string rootFile = Path.Combine(rootFolder, "root.txt");
                File.WriteAllText(rootFile, "Hello from root level!");

                string nestedFile = Path.Combine(subFolder, "nested.bin");
                byte[] nestedData = new byte[65536];
                new Random(7777).NextBytes(nestedData);
                File.WriteAllBytes(nestedFile, nestedData);

                // Set past timestamps to verify preservation
                var expectedTime = new DateTime(2023, 5, 10, 14, 30, 0, DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(rootFile, expectedTime);
                File.SetLastWriteTimeUtc(nestedFile, expectedTime);
                Directory.SetLastWriteTimeUtc(subFolder, expectedTime);
                Directory.SetLastWriteTimeUtc(rootFolder, expectedTime);

                await client.ConnectAsync("127.0.0.1", testPort);
                Assert(client.IsConnected, "Client must be connected");

                // Send folder from Client to Server
                bool sendOk = await client.SendFilesAsync(new List<string> { rootFolder }, serverDir);
                Assert(sendOk, "Send nested directory should succeed");

                await Task.Delay(200);

                string destRoot = Path.Combine(serverDir, "rootFolder");
                string destSub = Path.Combine(destRoot, "subFolder");
                string destRootFile = Path.Combine(destRoot, "root.txt");
                string destNestedFile = Path.Combine(destSub, "nested.bin");

                Assert(Directory.Exists(destRoot), $"Root folder not created at {destRoot}");
                Assert(Directory.Exists(destSub), $"Sub folder not created at {destSub}");
                Assert(File.Exists(destRootFile), $"Root file not created at {destRootFile}");
                Assert(File.Exists(destNestedFile), $"Nested file not created at {destNestedFile}");

                byte[] recvNested = File.ReadAllBytes(destNestedFile);
                Assert(recvNested.Length == nestedData.Length, "Nested file length mismatch");

                // Verify timestamps preserved within reasonable margin (3 seconds)
                var actualFileTime = File.GetLastWriteTimeUtc(destRootFile);
                Assert(Math.Abs((actualFileTime - expectedTime).TotalSeconds) < 3, $"Root file timestamp mismatch: {actualFileTime} vs {expectedTime}");

                var actualDirTime = Directory.GetLastWriteTimeUtc(destSub);
                Assert(Math.Abs((actualDirTime - expectedTime).TotalSeconds) < 3, $"Sub directory timestamp mismatch: {actualDirTime} vs {expectedTime}");
            }
            finally
            {
                client.Disconnect();
                server.Stop();
                try { Directory.Delete(serverDir, true); } catch { }
                try { Directory.Delete(clientDir, true); } catch { }
            }
        }

        private static async Task TestQuickShareClientListRemoteFiles()
        {
            int testPort = GetAvailablePort();
            var server = new QuickShareServer();
            server.Start(testPort);

            string tempDir = Path.Combine(Path.GetTempPath(), "QSRemoteList_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, "file1.txt"), "Hello 1");
            File.WriteAllText(Path.Combine(tempDir, "file2.txt"), "Hello 2");
            File.WriteAllText(Path.Combine(tempDir, "file3.txt"), "Hello 3");

            var client = new QuickShareClient();

            try
            {
                await client.ConnectAsync("127.0.0.1", testPort);
                var list = await client.ListRemoteFilesAsync(tempDir);

                Assert(list != null, "Remote file list should not be null");
                Assert(list!.Count == 3, $"Expected 3 files, got {list!.Count}");

                var names = new HashSet<string>(list!.Select(f => f.Name));
                Assert(names.Contains("file1.txt") && names.Contains("file2.txt") && names.Contains("file3.txt"), "All files should be present in listing");
            }
            finally
            {
                client.Disconnect();
                server.Stop();
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private static async Task TestQuickShareClientVersionMismatch()
        {
            int testPort = GetAvailablePort();
            var mockListener = new TcpListener(IPAddress.Loopback, testPort);
            mockListener.Start();

            var mockServerTask = Task.Run(async () =>
            {
                using var socket = await mockListener.AcceptTcpClientAsync();
                var stream = new QuickShareStream(socket.GetStream());
                byte[] header = new byte[4];
                await stream.ReadFullyAsync(header, 0, 4);
                int clientVer = await stream.ReadIntAsync();

                // Reject version
                stream.WriteBoolean(false);
                stream.WriteInt(999); // server supports version 999
                await stream.BaseStream.FlushAsync();
            });

            var client = new QuickShareClient();

            try
            {
                bool threw = false;
                try
                {
                    await client.ConnectAsync("127.0.0.1", testPort, 2000);
                }
                catch (InvalidOperationException ex)
                {
                    threw = true;
                    Assert(ex.Message.Contains("版本不匹配"), $"Exception should mention version mismatch, got: {ex.Message}");
                }

                Assert(threw, "ConnectAsync must throw on version mismatch");
                Assert(!client.IsConnected, "Client must not be connected after failed handshake");
                await mockServerTask;
            }
            finally
            {
                client.Disconnect();
                mockListener.Stop();
            }
        }

        private static async Task TestQuickShareClientAbruptDisconnect()
        {
            int testPort = GetAvailablePort();
            var server = new QuickShareServer();
            server.Start(testPort);

            var client = new QuickShareClient();

            try
            {
                await client.ConnectAsync("127.0.0.1", testPort);
                Assert(client.IsConnected, "Client should be connected");

                // Abruptly shut down server
                server.Stop();

                // Client disconnect should gracefully clean up without deadlock
                client.Disconnect();
                Assert(!client.IsConnected, "Client should be clean after Disconnect");
            }
            finally
            {
                client.Disconnect();
                server.Stop();
            }
        }
    }

    /// <summary>
    /// Stream decorator simulating network packet fragmentation.
    /// </summary>
    public class ChunkedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly int _chunkSize;

        public ChunkedReadStream(Stream inner, int chunkSize)
        {
            _inner = inner;
            _chunkSize = chunkSize;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            int toRead = Math.Min(count, _chunkSize);
            return _inner.Read(buffer, offset, toRead);
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int toRead = Math.Min(count, _chunkSize);
            return await _inner.ReadAsync(buffer.AsMemory(offset, toRead), cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    }
}
