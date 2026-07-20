using System.IO;
using System.Text.Json;
using CodexUsageWidget.Core;

namespace CodexUsageWidget.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly SemaphoreSlim gate = new(1, 1);

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
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            await using var stream = new FileStream(
                SettingsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var settings = document.RootElement.Deserialize<AppSettings>(
                SerializerOptions);
            settings ??= new AppSettings();

            var hasTransparencySemanticsVersion =
                document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.EnumerateObject().Any(
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
        catch (JsonException)
        {
            return new AppSettings();
        }
        catch (IOException)
        {
            return new AppSettings();
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
            Directory.CreateDirectory(AppDataDirectory);
            var temporaryPath = SettingsPath + ".tmp";
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    settings,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        finally
        {
            gate.Release();
        }
    }
}
