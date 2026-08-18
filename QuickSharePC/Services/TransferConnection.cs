using System.IO;
using QuickShare.PC.Models;

namespace QuickShare.PC.Services
{
    public class TransferConnection
    {
        public string IName { get; }
        public QuickShareStream Channel { get; }

        private TrafficInfo _currentTraffic;
        private TrafficInfo _totalTraffic;

        public TransferConnection(string iName, QuickShareStream channel)
        {
            IName = iName;
            Channel = channel;
            _currentTraffic = new TrafficInfo(iName);
            _totalTraffic = new TrafficInfo(iName);
        }

        public void AddUploadedBytes(long byteCount)
        {
            lock (this)
            {
                _currentTraffic.UploadTraffic += byteCount;
                _totalTraffic.UploadTraffic += byteCount;
            }
        }

        public void AddDownloadedBytes(long byteCount)
        {
            lock (this)
            {
                _currentTraffic.DownloadTraffic += byteCount;
                _totalTraffic.DownloadTraffic += byteCount;
            }
        }

        public TrafficInfo ResetCurrentTrafficInfo()
        {
            lock (this)
            {
                var info = _currentTraffic;
                _currentTraffic = new TrafficInfo(IName);
                return info;
            }
        }

        public TrafficInfo ResetTotalTrafficInfo()
        {
            lock (this)
            {
                var info = _totalTraffic;
                _totalTraffic = new TrafficInfo(IName);
                return info;
            }
        }

        public TrafficInfo GetTotalTraffic()
        {
            lock (this)
            {
                return _totalTraffic;
            }
        }

        public void Close()
        {
            Channel.Close();
        }
    }
}
