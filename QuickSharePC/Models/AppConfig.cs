using System;
using System.IO;

namespace QuickShare.PC.Models
{
    public class AppConfig
    {
        public int Port { get; set; } = 5740;
        public string SaveDirectory { get; set; } = GetDefaultSaveDirectory();
        public bool AutoStart { get; set; } = false;
        public bool AutoStartServer { get; set; } = true;
        public string TargetIp { get; set; } = "192.168.1.100";
        public int TargetPort { get; set; } = 5740;
        public bool IsClientMode { get; set; } = false;
        public bool Enable4KFriendly { get; set; } = false;

        public static string GetDefaultSaveDirectory()
        {
            try
            {
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string downloads = Path.Combine(userProfile, "Downloads");
                if (Directory.Exists(downloads))
                {
                    return downloads;
                }
                return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            }
            catch
            {
                return AppDomain.CurrentDomain.BaseDirectory;
            }
        }
    }
}
