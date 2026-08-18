namespace QuickShare.PC.Models
{
    public class RemoteFile
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public long LastModified { get; set; }
        public long Size { get; set; }
        public bool IsDirectory { get; set; }

        public RemoteFile() { }

        public RemoteFile(string name, string path, long lastModified, long size, bool isDirectory)
        {
            Name = name;
            Path = path;
            LastModified = lastModified;
            Size = size;
            IsDirectory = isDirectory;
        }
    }
}
