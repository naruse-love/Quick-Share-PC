using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace QuickShare.PC.Models
{
    public class TransferTask : INotifyPropertyChanged
    {
        private double _progress;
        private string _speed = "0 KB/s";
        private string _status = "等待中";
        private long _bytesTransferred;

        public string Id { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string Direction { get; set; } = "接收"; // "接收" or "发送"
        public long Size { get; set; }

        public long BytesTransferred
        {
            get => _bytesTransferred;
            set
            {
                if (_bytesTransferred != value)
                {
                    _bytesTransferred = value;
                    OnPropertyChanged();
                    Progress = Size > 0 ? (double)_bytesTransferred / Size * 100 : 0;
                }
            }
        }

        public double Progress
        {
            get => _progress;
            set
            {
                if (_progress != value)
                {
                    _progress = value;
                    OnPropertyChanged();
                }
            }
        }

        public string Speed
        {
            get => _speed;
            set
            {
                if (_speed != value)
                {
                    _speed = value;
                    OnPropertyChanged();
                }
            }
        }

        public string Status
        {
            get => _status;
            set
            {
                if (_status != value)
                {
                    _status = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
