namespace QuickShare.PC.Models
{
    public class TransferHistory
    {
        public string FileName { get; set; } = string.Empty;
        public string Direction { get; set; } = "接收"; // "接收" or "发送"
        public string SizeString { get; set; } = string.Empty;
        public string TimeString { get; set; } = string.Empty;
        public string Status { get; set; } = "完成";
    }
}
