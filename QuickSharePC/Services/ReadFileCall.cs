using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QuickShare.PC.Models;

namespace QuickShare.PC.Services
{
    public class ReadFileCall
    {
        public const long SMALL_FILE_THRESHOLD = 128 * 1024; // 128KB threshold for 4K friendliness

        public static readonly FileBlock END_POINT = new FileBlock(true, -1, "END_POINT", 0, 0, -1, null);
        public static readonly FileBlock INTERRUPT = new FileBlock(true, -1, "INTERRUPT", 0, 0, -1, null);
        public static readonly FileBlock READ_ERROR = new FileBlock(true, -1, "READ_ERROR", 0, 0, -1, null);
        public static readonly FileBlock WRITE_ERROR = new FileBlock(true, -1, "WRITE_ERROR", 0, 0, -1, null);

        private readonly BlockingCollection<FileBlock> _deque = new BlockingCollection<FileBlock>();
        private readonly BlockingCollection<byte[]> _buffers;
        private readonly List<RemoteFile> _files;
        private readonly QuickShareDirectory _localDir;
        private readonly QuickShareDirectory _remoteDir;
        private readonly int _operateThreadCount;
        private readonly bool _enable4KFriendly;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private volatile bool _isShutdown = false;
        private int _fileIndex = -1;
        private FileStream? _currentFileStream = null;

        public ReadFileCall(BlockingCollection<byte[]> buffers, List<RemoteFile> files, QuickShareDirectory localDir, QuickShareDirectory remoteDir, int operateThreadCount, bool enable4KFriendly = false)
        {
            _buffers = buffers;
            _files = files;
            _localDir = localDir;
            _remoteDir = remoteDir;
            _operateThreadCount = operateThreadCount;
            _enable4KFriendly = enable4KFriendly;
        }

        public async Task ExecuteAsync()
        {
            if (_enable4KFriendly)
            {
                await ExecuteWith4KFriendlyAsync();
            }
            else
            {
                await ExecuteNormalAsync();
            }
        }

        public async Task ExecuteNormalAsync()
        {
            try
            {
                foreach (var file in _files)
                {
                    if (_isShutdown) break;
                    if (!FileExists(file.Path))
                    {
                        continue;
                    }
                    await ReadToDequeAsync(file);
                    if (file.IsDirectory && !_isShutdown)
                    {
                        await ListFilesAndReadAsync(file);
                    }
                }

                if (!_isShutdown)
                {
                    for (int i = 0; i < _operateThreadCount; i++)
                    {
                        _deque.Add(END_POINT);
                    }
                }
            }
            catch (Exception)
            {
                if (!_isShutdown)
                {
                    for (int i = 0; i < _operateThreadCount; i++)
                    {
                        _deque.Add(READ_ERROR);
                    }
                }
                throw;
            }
        }

        public async Task ExecuteWith4KFriendlyAsync()
        {
            try
            {
                var looseFiles = new List<RemoteFile>();
                var allDirs = new List<RemoteFile>();
                var bigFiles = new List<RemoteFile>();
                var smallFilesByDir = new Dictionary<string, List<RemoteFile>>(StringComparer.OrdinalIgnoreCase);

                // 1. First pass: separate loose files and folder hierarchies
                foreach (var file in _files)
                {
                    if (_isShutdown) break;
                    if (!FileExists(file.Path)) continue;

                    if (file.IsDirectory)
                    {
                        TraverseDirectoryTree(file, allDirs, bigFiles, smallFilesByDir);
                    }
                    else
                    {
                        looseFiles.Add(file);
                    }
                }

                // 2. Start background compression pipeline
                var zipQueue = new BlockingCollection<(string transferPath, byte[] zipData, long lastModified)>(boundedCapacity: 32);

                var compressionTask = Task.Run(async () =>
                {
                    try
                    {
                        foreach (var kvp in smallFilesByDir)
                        {
                            if (_cts.IsCancellationRequested || _isShutdown) break;

                            string dirPath = kvp.Key;
                            var smallFiles = kvp.Value;
                            if (smallFiles == null || smallFiles.Count == 0) continue;

                            byte[] zipBytes = await CreateZipArchiveForFilesAsync(smallFiles, _cts.Token);
                            if (_cts.IsCancellationRequested || _isShutdown) break;

                            string batchFileName = $"__qs_batch_{((uint)dirPath.GetHashCode()):x8}.zip";
                            string zipLocalPath = Path.Combine(dirPath, batchFileName);
                            string transferPath = _localDir.GenerateTransferPath(zipLocalPath, _remoteDir);
                            long lastModified = smallFiles.Max(f => f.LastModified);

                            zipQueue.Add((transferPath, zipBytes, lastModified), _cts.Token);
                        }
                    }
                    catch (Exception)
                    {
                        try { _cts.Cancel(); } catch { }
                        throw;
                    }
                    finally
                    {
                        zipQueue.CompleteAdding();
                    }
                }, _cts.Token);

                // 3. Phase 1 (Main thread): Stream directories, loose files, and big files
                foreach (var dir in allDirs)
                {
                    if (_isShutdown || _cts.IsCancellationRequested) break;
                    _fileIndex++;
                    string transferPath = _localDir.GenerateTransferPath(dir.Path, _remoteDir);
                    _deque.Add(new FileBlock(false, _fileIndex, transferPath, dir.LastModified, 0, 0, null));
                }

                foreach (var file in looseFiles)
                {
                    if (_isShutdown || _cts.IsCancellationRequested) break;
                    await ReadToDequeAsync(file);
                }

                foreach (var file in bigFiles)
                {
                    if (_isShutdown || _cts.IsCancellationRequested) break;
                    await ReadToDequeAsync(file);
                }

                // 4. Phase 2 (Main thread): Stream prepared batch ZIP packages
                foreach (var (transferPath, zipBytes, lastModified) in zipQueue.GetConsumingEnumerable(_cts.Token))
                {
                    if (_isShutdown) break;
                    EnqueueZipData(transferPath, zipBytes, lastModified);
                }

                await compressionTask;

                if (!_isShutdown)
                {
                    for (int i = 0; i < _operateThreadCount; i++)
                    {
                        _deque.Add(END_POINT);
                    }
                }
            }
            catch (Exception)
            {
                try { _cts.Cancel(); } catch { }
                if (!_isShutdown)
                {
                    for (int i = 0; i < _operateThreadCount; i++)
                    {
                        _deque.Add(READ_ERROR);
                    }
                }
                throw;
            }
        }

        private static readonly DateTimeOffset MinZipTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private static readonly DateTimeOffset MaxZipTime = new DateTimeOffset(2107, 12, 31, 23, 59, 58, TimeSpan.Zero);

        public static DateTimeOffset ClampZipTimestamp(long lastModifiedMs)
        {
            try
            {
                var dto = DateTimeOffset.FromUnixTimeMilliseconds(lastModifiedMs).ToLocalTime();
                if (dto < MinZipTime) return MinZipTime;
                if (dto > MaxZipTime) return MaxZipTime;
                return dto;
            }
            catch
            {
                return MinZipTime;
            }
        }

        private void TraverseDirectoryTree(
            RemoteFile folder,
            List<RemoteFile> allDirs,
            List<RemoteFile> bigFiles,
            Dictionary<string, List<RemoteFile>> smallFilesByDir)
        {
            allDirs.Add(folder);
            if (!smallFilesByDir.ContainsKey(folder.Path))
            {
                smallFilesByDir[folder.Path] = new List<RemoteFile>();
            }

            var subItems = ListFiles(folder.Path);
            foreach (var item in subItems)
            {
                if (item.IsDirectory)
                {
                    TraverseDirectoryTree(item, allDirs, bigFiles, smallFilesByDir);
                }
                else
                {
                    if (item.Size > SMALL_FILE_THRESHOLD)
                    {
                        bigFiles.Add(item);
                    }
                    else
                    {
                        smallFilesByDir[folder.Path].Add(item);
                    }
                }
            }
        }

        private async Task<byte[]> CreateZipArchiveForFilesAsync(List<RemoteFile> smallFiles, CancellationToken ct)
        {
            using var ms = new MemoryStream();
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: Encoding.UTF8))
            {
                foreach (var file in smallFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    var entry = archive.CreateEntry(file.Name, CompressionLevel.Fastest);
                    entry.LastWriteTime = ClampZipTimestamp(file.LastModified);
                    using var entryStream = entry.Open();
                    using var fileStream = OpenFile(file.Path);
                    await fileStream.CopyToAsync(entryStream, ct);
                }
            }
            return ms.ToArray();
        }

        private void EnqueueZipData(string transferPath, byte[] zipBytes, long lastModified)
        {
            _fileIndex++;
            long length = zipBytes.Length;
            if (length == 0)
            {
                byte[] buffer = _buffers.Take();
                if (_isShutdown)
                {
                    RecycleBuffer(buffer);
                    return;
                }
                _deque.Add(new FileBlock(true, _fileIndex, transferPath, lastModified, 0, 0, buffer, 0));
                return;
            }

            long remaining = length;
            int blockIdx = 0;
            int zipOffset = 0;
            while (remaining > 0 && !_isShutdown)
            {
                int blkSize = (int)Math.Min(remaining, FileBlock.BLOCK_SIZE);
                byte[] buffer = _buffers.Take();
                if (_isShutdown)
                {
                    RecycleBuffer(buffer);
                    return;
                }
                Buffer.BlockCopy(zipBytes, zipOffset, buffer, 0, blkSize);
                _deque.Add(new FileBlock(true, _fileIndex, transferPath, lastModified, length, blockIdx, buffer, blkSize));
                remaining -= blkSize;
                zipOffset += blkSize;
                blockIdx++;
            }
        }

        private async Task ListFilesAndReadAsync(RemoteFile folder)
        {
            List<RemoteFile> subFiles = ListFiles(folder.Path);
            foreach (var file in subFiles)
            {
                await ReadToDequeAsync(file);
                if (file.IsDirectory)
                {
                    await ListFilesAndReadAsync(file);
                }
            }
        }

        private async Task ReadToDequeAsync(RemoteFile file)
        {
            _fileIndex++;
            string transferPath = _localDir.GenerateTransferPath(file.Path, _remoteDir);

            if (file.IsDirectory)
            {
                _deque.Add(new FileBlock(false, _fileIndex, transferPath, file.LastModified, 0, 0, null));
                return;
            }

            _currentFileStream = OpenFile(file.Path);
            long length = _currentFileStream.Length;
            long lastModified = file.LastModified;
            long remaining = length;

            if (length == 0)
            {
                byte[] buffer = _buffers.Take();
                _deque.Add(new FileBlock(true, _fileIndex, transferPath, lastModified, length, 0, buffer, 0));
                CloseFile();
                return;
            }

            int i = 0;
            while (remaining > 0)
            {
                int blkSize = (int)Math.Min(remaining, FileBlock.BLOCK_SIZE);
                byte[] buffer = _buffers.Take();
                
                int offset = 0;
                while (offset < blkSize)
                {
                    int read = await _currentFileStream.ReadAsync(buffer, offset, blkSize - offset);
                    if (read <= 0)
                    {
                        throw new EndOfStreamException($"Unexpected end of stream reading file {file.Path}");
                    }
                    offset += read;
                }

                _deque.Add(new FileBlock(true, _fileIndex, transferPath, lastModified, length, i, buffer, blkSize));
                remaining -= blkSize;
                i++;
            }
            CloseFile();
        }

        public void RecycleBuffer(byte[] buffer)
        {
            _buffers.Add(buffer);
        }

        public FileBlock TakeBlock()
        {
            return _deque.Take();
        }

        public void ShutdownByWriteError()
        {
            _isShutdown = true;
            try { _cts.Cancel(); } catch { }
            RecycleAllBuffers();
            ClearAndAddAll(WRITE_ERROR);
        }

        public void ShutdownByConnectionBreak()
        {
            _isShutdown = true;
            try { _cts.Cancel(); } catch { }
            RecycleAllBuffers();
            ClearAndAddAll(INTERRUPT);
        }

        private void RecycleAllBuffers()
        {
            while (_deque.TryTake(out var block))
            {
                if (block.Data != null)
                {
                    RecycleBuffer(block.Data);
                }
            }
        }

        private void ClearAndAddAll(FileBlock block)
        {
            // Drain the queue
            while (_deque.TryTake(out _)) { }

            for (int i = 0; i < _operateThreadCount; i++)
            {
                _deque.Add(block);
            }
        }

        private bool FileExists(string path)
        {
            return File.Exists(path) || Directory.Exists(path);
        }

        private List<RemoteFile> ListFiles(string path)
        {
            var result = new List<RemoteFile>();
            try
            {
                if (Directory.Exists(path))
                {
                    var di = new DirectoryInfo(path);
                    foreach (var entry in di.GetFileSystemInfos())
                    {
                        bool isDir = entry is DirectoryInfo;
                        long size = isDir ? 0 : ((FileInfo)entry).Length;
                        long lastMod = new DateTimeOffset(entry.LastWriteTimeUtc).ToUnixTimeMilliseconds();
                        result.Add(new RemoteFile(entry.Name, entry.FullName, lastMod, size, isDir));
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error listing directory {path}: {ex.Message}");
            }
            return result;
        }

        private FileStream OpenFile(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        }

        private void CloseFile()
        {
            if (_currentFileStream != null)
            {
                _currentFileStream.Dispose();
                _currentFileStream = null;
            }
        }
    }
}
