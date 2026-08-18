namespace QuickShare.PC.Models
{
    public class TrafficInfo
    {
        public string IName { get; set; } = string.Empty;
        public long UploadTraffic { get; set; }
        public long DownloadTraffic { get; set; }

        public TrafficInfo() { }

        public TrafficInfo(string iName)
        {
            IName = iName;
        }

        public TrafficInfo(string iName, long uploadTraffic, long downloadTraffic)
        {
            IName = iName;
            UploadTraffic = uploadTraffic;
            DownloadTraffic = downloadTraffic;
        }
    }
}
