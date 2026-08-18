using System;
using System.IO;
using System.Text.Json;
using QuickShare.PC.Models;

namespace QuickShare.PC.Services
{
    public class ConfigService
    {
        private readonly string _configDirectory;
        private readonly string _configFilePath;

        public ConfigService()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _configDirectory = Path.Combine(appData, "QuickShare");
            _configFilePath = Path.Combine(_configDirectory, "config.json");
        }

        public AppConfig LoadConfig()
        {
            try
            {
                if (File.Exists(_configFilePath))
                {
                    string json = File.ReadAllText(_configFilePath);
                    var config = JsonSerializer.Deserialize<AppConfig>(json);
                    if (config != null)
                    {
                        // Validate save directory
                        if (string.IsNullOrWhiteSpace(config.SaveDirectory) || !Directory.Exists(config.SaveDirectory))
                        {
                            try
                            {
                                if (!string.IsNullOrWhiteSpace(config.SaveDirectory))
                                {
                                    Directory.CreateDirectory(config.SaveDirectory);
                                }
                                else
                                {
                                    config.SaveDirectory = AppConfig.GetDefaultSaveDirectory();
                                }
                            }
                            catch
                            {
                                config.SaveDirectory = AppConfig.GetDefaultSaveDirectory();
                            }
                        }
                        return config;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading config: {ex.Message}");
            }

            // Return default config
            var defConfig = new AppConfig();
            SaveConfig(defConfig);
            return defConfig;
        }

        public void SaveConfig(AppConfig config)
        {
            try
            {
                if (!Directory.Exists(_configDirectory))
                {
                    Directory.CreateDirectory(_configDirectory);
                }

                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(config, options);
                File.WriteAllText(_configFilePath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving config: {ex.Message}");
            }
        }
    }
}
