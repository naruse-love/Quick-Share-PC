using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using QuickShare.PC.Models;

namespace QuickShare.PC.Services
{
    public class ReceiveFileCall
    {
        private readonly int _tIndex;
        private readonly TransferConnection _connection;
        private readonly WriteFileCall _writeFileCall;
        private readonly Action<string, string, long, long> _onProgress;
        private readonly Action<string, long, long> _onComplete;
        private readonly Action<string, int, string?> _onError;

        public ReceiveFileCall(
            int tIndex,
            TransferConnection connection,
            WriteFileCall writeFileCall,
            Action<string, string, long, long> onProgress,
            Action<string, long, long> onComplete,
            Action<string, int, string?> onError)
        {
            _tIndex = tIndex;
            _connection = connection;
            _writeFileCall = writeFileCall;
            _onProgress = onProgress;
            _onComplete = onComplete;
            _onError = onError;
            _connection.ResetTotalTrafficInfo();
        }

        public async Task ExecuteAsync()
        {
            var stopwatch = Stopwatch.StartNew();
            var channel = _connection.Channel;
            var iName = _connection.IName;

            try
            {
                while (true)
                {
                    short header = await channel.ReadShortAsync();
                    switch (header)
                    {
                        case QuickShareConstants.FOLDER:
                            {
                                int fileIndex = await channel.ReadIntAsync();
                                string path = await channel.ReadUTFAsync();
                                long lastModified = await channel.ReadLongAsync();
                                _writeFileCall.PutBlock(new FileBlock(false, fileIndex, path, lastModified, 0, 0, null), _tIndex);
                                break;
                            }
                        case QuickShareConstants.FILE:
                            {
                                int fileIndex = await channel.ReadIntAsync();
                                string path = await channel.ReadUTFAsync();
                                long lastModified = await channel.ReadLongAsync();
                                long totalSize = await channel.ReadLongAsync();
                                int index = await channel.ReadIntAsync();
                                int length = await channel.ReadIntAsync();

                                _onProgress?.Invoke(iName, path, (long)index * FileBlock.BLOCK_SIZE + length, totalSize);

                                byte[] buffer = _writeFileCall.GetBuffer();
                                int offset = 0;
                                while (offset < length)
                                {
                                    int read = await channel.BaseStream.ReadAsync(buffer, offset, length - offset);
                                    if (read <= 0)
                                    {
                                        throw new EndOfStreamException($"Connection closed prematurely while receiving block index {index} for {path}");
                                    }
                                    _connection.AddDownloadedBytes(read);
                                    offset += read;
                                }

                                _writeFileCall.PutBlock(new FileBlock(true, fileIndex, path, lastModified, totalSize, index, buffer, length), _tIndex);
                                break;
                            }
                        case QuickShareConstants.EOF:
                            _writeFileCall.FinishChannel(_tIndex);
                            _onComplete?.Invoke(iName, _connection.GetTotalTraffic().DownloadTraffic, stopwatch.ElapsedMilliseconds);
                            return;
                        case QuickShareConstants.END_OF_INTERRUPTED:
                            _writeFileCall.Cancel();
                            _onError?.Invoke(iName, 4, null);
                            return;
                        case QuickShareConstants.END_OF_READ_ERROR:
                            _writeFileCall.Cancel();
                            _onError?.Invoke(iName, 5, null);
                            return;
                        case QuickShareConstants.END_OF_WRITE_ERROR:
                            _writeFileCall.Cancel();
                            _onError?.Invoke(iName, 6, null);
                            return;
                    }
                }
            }
            catch (Exception ex)
            {
                _writeFileCall.FinishChannel(_tIndex);
                _onError?.Invoke(iName, -1, ex.Message);
                throw;
            }
        }
    }
}
