using System.Text.Json;
using ScoutCore;

namespace ScreenScout;

internal sealed class SettingsStore
{
    public string DirectoryPath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenScoutTest");
    public string FilePath => Path.Combine(DirectoryPath, "ClickerSettingsV2.json");
    public ClickSettings Load()
    {
        // Copy once; the original settings are never opened for writing.
        if (!File.Exists(FilePath))
        {
            Directory.CreateDirectory(DirectoryPath);
            var original = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenScout", "ClickerSettingsV2.json");
            var imported = ClickSettings.Load(File.Exists(original) ? File.ReadAllText(original) : null);
            imported.SecondaryClickingEnabled = false;
            Save(imported);
        }
        try { return ClickSettings.Load(File.Exists(FilePath) ? File.ReadAllText(FilePath) : null); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(); }
    }
    public void Save(ClickSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, ClickSettings.JsonOptions));
        File.Move(temp, FilePath, true);
    }
}
