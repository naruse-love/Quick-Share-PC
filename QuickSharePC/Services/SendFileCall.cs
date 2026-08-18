using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using QuickShare.PC.Models;

namespace QuickShare.PC.Services
{
    public class SendFileCall
    {
        private readonly ReadFileCall _readFileCall;
        private readonly TransferConnection _connection;
        private readonly Action<string, string, long, long> _onProgress;
        private readonly Action<string, long, long> _onComplete;
        private readonly Action<string, int, string?> _onError;

        public SendFileCall(
            ReadFileCall readFileCall,
            TransferConnection connection,
            Action<string, string, long, long> onProgress,
            Action<string, long, long> onComplete,
            Action<string, int, string?> onError)
        {
            _readFileCall = readFileCall;
            _connection = connection;
            _onProgress = onProgress;
            _onComplete = onComplete;
            _onError = onError;
            _connection.ResetTotalTrafficInfo();
        }

        public async Task ExecuteAsync()
        {
            FileBlock? fileBlock = null;
            var stopwatch = Stopwatch.StartNew();
            var channel = _connection.Channel;
            var iName = _connection.IName;

            try
            {
                while (true)
                {
                    fileBlock = _readFileCall.TakeBlock();

                    if (fileBlock.FileIndex == -1)
                    {
                        if (fileBlock == ReadFileCall.END_POINT)
                        {
                            channel.WriteShort(QuickShareConstants.EOF);
                            _onComplete?.Invoke(iName, _connection.GetTotalTraffic().UploadTraffic, stopwatch.ElapsedMilliseconds);
                        }
                        else if (fileBlock == ReadFileCall.INTERRUPT)
                        {
                            channel.WriteShort(QuickShareConstants.END_OF_INTERRUPTED);
                            _onError?.Invoke(iName, 4, null);
                        }
                        else if (fileBlock == ReadFileCall.READ_ERROR)
                        {
                            channel.WriteShort(QuickShareConstants.END_OF_READ_ERROR);
                            _onError?.Invoke(iName, 5, null);
                        }
                        else if (fileBlock == ReadFileCall.WRITE_ERROR)
                        {
                            channel.WriteShort(QuickShareConstants.END_OF_WRITE_ERROR);
                            _onError?.Invoke(iName, 6, null);
                        }
                        break;
                    }

                    channel.WriteShort(fileBlock.IsFile ? QuickShareConstants.FILE : QuickShareConstants.FOLDER);
                    channel.WriteInt(fileBlock.FileIndex);
                    channel.WriteUTF(fileBlock.Path);
                    channel.WriteLong(fileBlock.LastModified);

                    if (!fileBlock.IsFile) continue;

                    channel.WriteLong(fileBlock.TotalSize);
                    channel.WriteInt(fileBlock.Index);
                    channel.WriteInt(fileBlock.DataLength);

                    _onProgress?.Invoke(iName, fileBlock.Path, fileBlock.GetStartPosition() + fileBlock.DataLength, fileBlock.TotalSize);

                    if (fileBlock.Data != null)
                    {
                        await channel.BaseStream.WriteAsync(fileBlock.Data.AsMemory(0, fileBlock.DataLength));
                        _readFileCall.RecycleBuffer(fileBlock.Data);
                        _connection.AddUploadedBytes(fileBlock.DataLength);
                    }
                }
            }
            catch (Exception ex)
            {
                if (fileBlock != null && fileBlock.Data != null)
                {
                    _readFileCall.RecycleBuffer(fileBlock.Data);
                }
                _readFileCall.ShutdownByConnectionBreak();
                _onError?.Invoke(iName, -1, ex.Message);
                throw;
            }
        }
    }
}
