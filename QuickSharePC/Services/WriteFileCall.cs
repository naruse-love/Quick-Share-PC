using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using QuickShare.PC.Models;

namespace QuickShare.PC.Services
{
    /// <summary>
    /// High-performance sequential file writing pipeline for pure LAN single-stream transfers.
    /// Eliminates multi-queue priority reordering overhead while maintaining 1MB chunk slicing,
    /// buffer recycling, and timestamp preservation.
    /// </summary>
    public class WriteFileCall
    {
        private readonly BlockingCollection<byte[]> _buffers;
        private readonly BlockingCollection<FileBlock> _queue = new BlockingCollection<FileBlock>();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private FileStream? _currentFileStream = null;
        private volatile bool _isCanceled = false;

        public WriteFileCall(BlockingCollection<byte[]> buffers, int dequeCount = 1)
        {
            _buffers = buffers;
        }

        public async Task ExecuteAsync()
        {
            FileBlock? currentBlock = null;
            try
            {
                string? lastPath = null;
                long lastModified = 0;
                long cursor = 0;

                while (!_queue.IsCompleted && !_isCanceled)
                {
                    currentBlock = null;
                    try
                    {
                        currentBlock = _queue.Take(_cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (InvalidOperationException)
                    {
                        // Adding was completed and queue is empty
                        break;
                    }

                    if (currentBlock == null) break;

                    if (!currentBlock.IsFile)
                    {
                        // Folder creation
                        TryMkdirs(currentBlock.Path);
                        SetLastModified(currentBlock.Path, currentBlock.LastModified);
                        continue;
                    }

                    CreateParentDirIfNotExists(currentBlock.Path);

                    // When transitioning to a new file, close previous and open current
                    if (lastPath == null || lastPath != currentBlock.Path)
                    {
                        if (_currentFileStream != null)
                        {
                            CloseFile();
                            if (lastPath != null)
                            {
                                SetLastModified(lastPath, lastModified);
                            }
                        }

                        _currentFileStream = CreateAndOpenFile(currentBlock.Path, currentBlock.TotalSize);
                        cursor = 0;
                    }

                    lastPath = currentBlock.Path;
                    lastModified = currentBlock.LastModified;

                    // Seek to block position if required
                    if (cursor != currentBlock.GetStartPosition())
                    {
                        cursor = currentBlock.GetStartPosition();
                        _currentFileStream!.Position = cursor;
                    }

                    // Write chunk data
                    if (currentBlock.Data != null && currentBlock.DataLength > 0)
                    {
                        try
                        {
                            await _currentFileStream!.WriteAsync(currentBlock.Data.AsMemory(0, currentBlock.DataLength), _cts.Token);
                            cursor += currentBlock.DataLength;
                        }
                        finally
                        {
                            // Recycle buffer immediately
                            _buffers.Add(currentBlock.Data);
                            currentBlock = null;
                        }
                    }
                    else if (currentBlock.Data != null)
                    {
                        _buffers.Add(currentBlock.Data);
                        currentBlock = null;
                    }
                }

                if (lastPath != null)
                {
                    CloseFile();
                    SetLastModified(lastPath, lastModified);
                }
            }
            catch (Exception)
            {
                Cancel();
                throw;
            }
            finally
            {
                if (currentBlock != null && currentBlock.Data != null)
                {
                    _buffers.Add(currentBlock.Data);
                    currentBlock = null;
                }
                CloseFile();
                RecycleRemainingBuffers();
            }
        }

        public byte[] GetBuffer()
        {
            return _buffers.Take();
        }

        public byte[]? GetBuffer(int timeoutMs)
        {
            if (_isCanceled) return null;
            if (_buffers.TryTake(out var buffer, timeoutMs))
            {
                return buffer;
            }
            return null;
        }

        public void PutBlock(FileBlock block, int tIndex = 0)
        {
            if (_isCanceled || _queue.IsAddingCompleted)
            {
                if (block.Data != null)
                {
                    _buffers.Add(block.Data);
                }
                return;
            }

            try
            {
                _queue.Add(block);
            }
            catch (InvalidOperationException)
            {
                if (block.Data != null)
                {
                    _buffers.Add(block.Data);
                }
            }
        }

        public void FinishChannel(int tIndex = 0)
        {
            try
            {
                if (!_queue.IsAddingCompleted)
                {
                    _queue.CompleteAdding();
                }
            }
            catch { }
        }

        public void Cancel()
        {
            if (_isCanceled) return;
            _isCanceled = true;

            try
            {
                _cts.Cancel();
            }
            catch { }

            try
            {
                if (!_queue.IsAddingCompleted)
                {
                    _queue.CompleteAdding();
                }
            }
            catch { }

            CloseFile();
            RecycleRemainingBuffers();
        }

        private void RecycleRemainingBuffers()
        {
            while (_queue.TryTake(out var block))
            {
                if (block.Data != null)
                {
                    _buffers.Add(block.Data);
                }
            }
        }

        private void SetLastModified(string path, long time)
        {
            try
            {
                if (File.Exists(path) || Directory.Exists(path))
                {
                    var dt = DateTimeOffset.FromUnixTimeMilliseconds(time).LocalDateTime;
                    File.SetLastWriteTime(path, dt);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: cannot set last modified for {path}: {ex.Message}");
            }
        }

        private void CreateParentDirIfNotExists(string path)
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }

        private void TryMkdirs(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error creating directory {path}: {ex.Message}");
            }
        }

        private FileStream CreateAndOpenFile(string path, long length)
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite, 64 * 1024, useAsync: true);
            if (length > 0 && stream.Length < length)
            {
                stream.SetLength(length);
            }
            return stream;
        }

        private void CloseFile()
        {
            if (_currentFileStream != null)
            {
                try
                {
                    _currentFileStream.Flush();
                    _currentFileStream.Dispose();
                }
                catch { }
                finally
                {
                    _currentFileStream = null;
                }
            }
        }
    }
}
