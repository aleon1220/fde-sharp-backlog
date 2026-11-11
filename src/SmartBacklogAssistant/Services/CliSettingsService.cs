using System.Text.Json;
using SmartBacklogAssistant.Domain;

namespace SmartBacklogAssistant.Services;

/// <summary>
/// Loads and persists CLI configuration settings (<see cref="CliSettings"/>) to a
/// <c>settings.json</c> file inside the resolved Smart Backlog Assistant workspace.
/// </summary>
public sealed class CliSettingsService
{
    public const string SettingsFileName = "settings.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public string GetSettingsFilePath(WorkspaceDirectories workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var separator = workspace.IsWindows ? '\\' : '/';
        return $"{workspace.ProjectRoot.TrimEnd('\\', '/')}{separator}{SettingsFileName}";
    }

    public CliSettings Load(WorkspaceDirectories workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return LoadFromPath(GetSettingsFilePath(workspace));
    }

    public CliSettings LoadFromPath(string settingsFilePath)
    {
        if (string.IsNullOrWhiteSpace(settingsFilePath) || !File.Exists(settingsFilePath))
        {
            return new CliSettings();
        }

        try
        {
            var json = File.ReadAllText(settingsFilePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new CliSettings();
            }

            return JsonSerializer.Deserialize<CliSettings>(json, JsonOptions) ?? new CliSettings();
        }
        catch (JsonException)
        {
            return new CliSettings();
        }
        catch (IOException)
        {
            return new CliSettings();
        }
    }

    public string Save(WorkspaceDirectories workspace, CliSettings settings)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(settings);

        var filePath = GetSettingsFilePath(workspace);
        SaveToPath(filePath, settings);
        return filePath;
    }

    public void SaveToPath(string settingsFilePath, CliSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settingsFilePath))
        {
            throw new ArgumentException("Settings file path cannot be empty.", nameof(settingsFilePath));
        }

        ArgumentNullException.ThrowIfNull(settings);

        var directory = Path.GetDirectoryName(settingsFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(settingsFilePath, json);
    }
}

