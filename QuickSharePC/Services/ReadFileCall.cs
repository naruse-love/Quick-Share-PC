using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using QuickShare.PC.Models;

namespace QuickShare.PC.Services
{
    public class ReadFileCall
    {
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
        private int _fileIndex = -1;
        private FileStream? _currentFileStream = null;

        public ReadFileCall(BlockingCollection<byte[]> buffers, List<RemoteFile> files, QuickShareDirectory localDir, QuickShareDirectory remoteDir, int operateThreadCount)
        {
            _buffers = buffers;
            _files = files;
            _localDir = localDir;
            _remoteDir = remoteDir;
            _operateThreadCount = operateThreadCount;
        }

        public async Task ExecuteAsync()
        {
            try
            {
                foreach (var file in _files)
                {
                    if (!FileExists(file.Path))
                    {
                        continue;
                    }
                    await ReadToDequeAsync(file);
                    if (file.IsDirectory)
                    {
                        await ListFilesAndReadAsync(file);
                    }
                }

                for (int i = 0; i < _operateThreadCount; i++)
                {
                    _deque.Add(END_POINT);
                }
            }
            catch (Exception)
            {
                for (int i = 0; i < _operateThreadCount; i++)
                {
                    _deque.Add(READ_ERROR);
                }
                throw;
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
            RecycleAllBuffers();
            for (int i = 0; i < _operateThreadCount; i++)
            {
                // Put WRITE_ERROR at the head
                // In C# BlockingCollection we don't have AddFirst.
                // But we can recreate the collection or just clear and add WRITE_ERROR.
                // Wait! Let's clear the queue and add WRITE_ERROR because once a write error occurs, the transfer is aborted.
                // Let's implement a clear logic.
            }
            ClearAndAddAll(WRITE_ERROR);
        }

        public void ShutdownByConnectionBreak()
        {
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
