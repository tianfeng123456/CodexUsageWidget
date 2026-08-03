using System.IO;
using System.Text;
using System.Text.Json;
using CodexUsageWidget.Core;

namespace CodexUsageWidget.Services;

[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "This process-lifetime service never accesses SemaphoreSlim.AvailableWaitHandle. Disposing the gate could race with a final asynchronous settings save.")]
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly SemaphoreSlim gate = new(1, 1);
    private bool primaryNeedsRepair;

    public SettingsService(string? appDataDirectory = null)
    {
        AppDataDirectory = appDataDirectory
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodexUsageWidget");
        SettingsPath = Path.Combine(AppDataDirectory, "settings.json");
    }

    public string AppDataDirectory { get; }

    public string SettingsPath { get; }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await AtomicJsonFileStorage.ReadAsync(
                SettingsPath,
                DeserializeSettings,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            primaryNeedsRepair = result.RequiresPrimaryRepair;
            if (result.PrimaryFailure is not null)
            {
                LocalDiagnosticLog.TryWrite(
                    AppDataDirectory,
                    result.Value is null
                        ? "settings-load-defaults"
                        : $"settings-load-recovered-{result.Source}",
                    result.PrimaryFailure);
            }

            return result.Value ?? new AppSettings();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Normalize();

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var json = JsonSerializer.Serialize(settings, SerializerOptions);
            await AtomicJsonFileStorage.WriteAsync(
                SettingsPath,
                Encoding.UTF8.GetBytes(json),
                primaryNeedsRepair,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            primaryNeedsRepair = false;
        }
        finally
        {
            gate.Release();
        }
    }

    private static AppSettings DeserializeSettings(JsonElement root)
    {
        var settings = root.Deserialize<AppSettings>(SerializerOptions)
            ?? throw new JsonException("Settings JSON deserialized to null.");
        var hasTransparencySemanticsVersion =
            root.ValueKind == JsonValueKind.Object &&
            root.EnumerateObject().Any(
                property => string.Equals(
                    property.Name,
                    "glassTransparencySemanticsVersion",
                    StringComparison.OrdinalIgnoreCase));
        if (!hasTransparencySemanticsVersion ||
            settings.GlassTransparencySemanticsVersion <
            GlassTransparencyPolicy.CurrentSemanticsVersion)
        {
            settings.GlassTransparencyPercent =
                GlassTransparencyPolicy.MigrateLegacyPercent(
                    settings.GlassTransparencyPercent);
            settings.GlassTransparencySemanticsVersion =
                GlassTransparencyPolicy.CurrentSemanticsVersion;
        }

        return settings.Normalize();
    }
}
