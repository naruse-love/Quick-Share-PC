namespace QuickShare.PC.Models
{
    public class NetworkInterfaceInfo
    {
        public string Name { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public string InterfaceType { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public bool IsSelected { get; set; } = true;
    }
}
