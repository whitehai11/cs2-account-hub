using System.Text.Json;
using CS2AccountHub.Contracts;

internal sealed class SettingsStore
{
    private readonly object _gate = new();
    private readonly string _loadoutPath;
    private readonly string _personalizePath;
    private LoadoutPresetDto _loadout;
    private PersonalizePresetDto _personalize;

    public SettingsStore()
    {
        var directory = Environment.GetEnvironmentVariable("CS2_HUB_DATA_DIR")
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(directory);
        _loadoutPath = Path.Combine(directory, "loadout.json");
        _personalizePath = Path.Combine(directory, "personalize.json");
        _loadout = Load(_loadoutPath, DefaultLoadout());
        _personalize = Load(_personalizePath, new PersonalizePresetDto(null, null, null, null, false));
    }

    public LoadoutPresetDto GetLoadout() { lock (_gate) return _loadout; }
    public PersonalizePresetDto GetPersonalize() { lock (_gate) return _personalize; }

    public void SetLoadout(LoadoutPresetDto value)
    {
        lock (_gate) { _loadout = value; Save(_loadoutPath, value); }
    }

    public void SetPersonalize(PersonalizePresetDto value)
    {
        lock (_gate) { _personalize = value; Save(_personalizePath, value); }
    }

    private static T Load<T>(string path, T fallback)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? fallback
                : fallback;
        }
        catch { return fallback; }
    }

    private static void Save<T>(string path, T value)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, true);
    }

    private static LoadoutPresetDto DefaultLoadout() => new(
        "Default",
        new Dictionary<uint, uint> { [2] = 4, [3] = 2, [4] = 36, [5] = 1, [6] = 64, [8] = 17, [9] = 29, [10] = 23, [11] = 24, [12] = 26, [14] = 13, [15] = 7, [16] = 40, [17] = 9, [18] = 11 },
        new Dictionary<uint, uint> { [2] = 61, [3] = 2, [4] = 63, [5] = 1, [6] = 64, [8] = 27, [9] = 23, [10] = 26, [11] = 33, [12] = 34, [14] = 10, [15] = 60, [16] = 40, [17] = 9, [18] = 38 });
}
