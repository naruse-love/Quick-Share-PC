using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
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
        private readonly string? _baseSaveDir;
        private readonly List<(string Path, long LastModified)> _createdDirectories = new List<(string Path, long LastModified)>();
        private FileStream? _currentFileStream = null;
        private volatile bool _isCanceled = false;

        public WriteFileCall(BlockingCollection<byte[]> buffers, int dequeCount = 1, string? baseSaveDir = null)
        {
            _buffers = buffers;
            _baseSaveDir = baseSaveDir;
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

                    string targetPath = ResolveTargetPath(currentBlock.Path);

                    if (!currentBlock.IsFile)
                    {
                        // Folder creation
                        TryMkdirs(targetPath);
                        if (currentBlock.LastModified > 0)
                        {
                            _createdDirectories.Add((targetPath, currentBlock.LastModified));
                        }
                        continue;
                    }

                    CreateParentDirIfNotExists(targetPath);

                    // When transitioning to a new file, close previous and open current
                    if (lastPath == null || lastPath != targetPath)
                    {
                        if (_currentFileStream != null)
                        {
                            HandleCompletedFile(lastPath, lastModified);
                        }

                        _currentFileStream = CreateAndOpenFile(targetPath, currentBlock.TotalSize);
                        cursor = 0;
                    }

                    lastPath = targetPath;
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
                    HandleCompletedFile(lastPath, lastModified);
                }

                // Re-apply directory timestamps in reverse order (bottom-up / deepest first)
                // because creating/writing child files in NTFS updates the parent directory's LastWriteTime.
                for (int i = _createdDirectories.Count - 1; i >= 0; i--)
                {
                    SetLastModified(_createdDirectories[i].Path, _createdDirectories[i].LastModified);
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

        private string ResolveTargetPath(string originalPath)
        {
            if (string.IsNullOrEmpty(_baseSaveDir)) return originalPath;

            string normBase = Path.GetFullPath(_baseSaveDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normPath = originalPath.Replace('/', Path.DirectorySeparatorChar);

            // If already inside _baseSaveDir
            if (normPath.StartsWith(normBase, StringComparison.OrdinalIgnoreCase))
            {
                return normPath;
            }

            // Strip leading Windows drive or Android /sdcard/Download specifiers
            string relPath = normPath;
            if (Regex.IsMatch(relPath, @"^[A-Za-z]:\\"))
            {
                relPath = relPath.Substring(3);
            }
            else if (relPath.StartsWith(@"\sdcard\Download\", StringComparison.OrdinalIgnoreCase))
            {
                relPath = relPath.Substring(17);
            }
            else if (relPath.StartsWith(@"sdcard\Download\", StringComparison.OrdinalIgnoreCase))
            {
                relPath = relPath.Substring(16);
            }

            while (relPath.StartsWith(Path.DirectorySeparatorChar.ToString()) || relPath.StartsWith(Path.AltDirectorySeparatorChar.ToString()))
            {
                relPath = relPath.Substring(1);
            }

            return Path.Combine(normBase, relPath);
        }

        private void SetLastModified(string path, long time)
        {
            try
            {
                var dt = DateTimeOffset.FromUnixTimeMilliseconds(time).LocalDateTime;
                if (File.Exists(path))
                {
                    File.SetLastWriteTime(path, dt);
                }
                else if (Directory.Exists(path))
                {
                    Directory.SetLastWriteTime(path, dt);
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

        private void HandleCompletedFile(string? filePath, long lastModified)
        {
            if (filePath == null) return;
            CloseFile();
            if (lastModified > 0)
            {
                SetLastModified(filePath, lastModified);
            }
            TryExtractAndCleanupBatchZip(filePath);
        }

        private void TryExtractAndCleanupBatchZip(string filePath)
        {
            try
            {
                string fileName = Path.GetFileName(filePath);
                if (fileName.StartsWith("__qs_batch_", StringComparison.OrdinalIgnoreCase) &&
                    fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    string? extractDir = Path.GetDirectoryName(filePath);
                    if (string.IsNullOrEmpty(extractDir))
                    {
                        extractDir = _baseSaveDir ?? AppDomain.CurrentDomain.BaseDirectory;
                    }

                    string fullExtractDir = Path.GetFullPath(extractDir);
                    if (!fullExtractDir.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                    {
                        fullExtractDir += Path.DirectorySeparatorChar;
                    }

                    using (var archive = ZipFile.OpenRead(filePath))
                    {
                        foreach (var entry in archive.Entries)
                        {
                            if (string.IsNullOrEmpty(entry.Name) || entry.Name == "." || entry.Name == "..") continue;

                            string destPath = Path.Combine(extractDir, entry.Name);
                            string fullDestPath = Path.GetFullPath(destPath);
                            if (!fullDestPath.StartsWith(fullExtractDir, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            entry.ExtractToFile(destPath, overwrite: true);
                            try
                            {
                                File.SetLastWriteTime(destPath, entry.LastWriteTime.LocalDateTime);
                            }
                            catch { }
                        }
                    }

                    try
                    {
                        File.Delete(filePath);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Warning: Failed to delete batch zip {filePath}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to extract batch zip {filePath}: {ex.Message}");
            }
        }
    }
}
