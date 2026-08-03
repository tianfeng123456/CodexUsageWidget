using System.Text;
using System.Text.Json;
using CodexUsageWidget.Core;

namespace CodexUsageWidget.Tests;

public sealed class AtomicJsonFileStorageTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "CodexUsageWidget.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReadAsync_PrefersValidPrimary()
    {
        var path = GetPath();
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, "{\"Value\":1}");
        await File.WriteAllTextAsync(
            AtomicJsonFileStorage.GetTemporaryPath(path),
            "{\"Value\":2}");
        await File.WriteAllTextAsync(
            AtomicJsonFileStorage.GetBackupPath(path),
            "{\"Value\":3}");

        var result = await ReadValueAsync(path);

        Assert.Equal(1, result.Value!.Value);
        Assert.Equal(JsonFileRecoverySource.Primary, result.Source);
        Assert.False(result.RequiresPrimaryRepair);
        Assert.Null(result.PrimaryFailure);
    }

    [Fact]
    public async Task ReadAsync_AcceptsUtf8BomWithoutFallingBack()
    {
        var path = GetPath();
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(
            path,
            [0xEF, 0xBB, 0xBF, .. Utf8("{\"Value\":6}").ToArray()]);

        var result = await ReadValueAsync(path);

        Assert.Equal(6, result.Value!.Value);
        Assert.Equal(JsonFileRecoverySource.Primary, result.Source);
        Assert.False(result.RequiresPrimaryRepair);
        Assert.Null(result.PrimaryFailure);
    }

    [Fact]
    public async Task ReadAsync_UsesTemporaryBeforeBackupWhenPrimaryIsCorrupt()
    {
        var path = GetPath();
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, "{broken");
        await File.WriteAllTextAsync(
            AtomicJsonFileStorage.GetTemporaryPath(path),
            "{\"Value\":2}");
        await File.WriteAllTextAsync(
            AtomicJsonFileStorage.GetBackupPath(path),
            "{\"Value\":3}");

        var result = await ReadValueAsync(path);

        Assert.Equal(2, result.Value!.Value);
        Assert.Equal(JsonFileRecoverySource.Temporary, result.Source);
        Assert.True(result.RequiresPrimaryRepair);
        Assert.IsAssignableFrom<JsonException>(result.PrimaryFailure);
    }

    [Fact]
    public async Task ReadAsync_UsesBackupWhenPrimaryIsSemanticallyInvalid()
    {
        var path = GetPath();
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, "{\"Value\":\"wrong\"}");
        await File.WriteAllTextAsync(
            AtomicJsonFileStorage.GetBackupPath(path),
            "{\"Value\":7}");

        var result = await ReadValueAsync(path);

        Assert.Equal(7, result.Value!.Value);
        Assert.Equal(JsonFileRecoverySource.Backup, result.Source);
        Assert.True(result.RequiresPrimaryRepair);
        Assert.IsAssignableFrom<JsonException>(result.PrimaryFailure);
    }

    [Fact]
    public async Task ReadAsync_OversizedPrimaryFallsBackWithoutAllocatingIt()
    {
        var path = GetPath();
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, new string('x', 129));
        await File.WriteAllTextAsync(
            AtomicJsonFileStorage.GetBackupPath(path),
            "{\"Value\":8}");

        var result = await AtomicJsonFileStorage.ReadAsync(
            path,
            Deserialize,
            maximumBytes: 128);

        Assert.Equal(8, result.Value!.Value);
        Assert.Equal(JsonFileRecoverySource.Backup, result.Source);
        Assert.IsType<InvalidDataException>(result.PrimaryFailure);
    }

    [Fact]
    public async Task ReadAsync_AllInvalidReturnsNoValueAndRequestsRepair()
    {
        var path = GetPath();
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, "not-json");
        await File.WriteAllTextAsync(
            AtomicJsonFileStorage.GetBackupPath(path),
            "also-not-json");

        var result = await ReadValueAsync(path);

        Assert.Null(result.Value);
        Assert.Equal(JsonFileRecoverySource.None, result.Source);
        Assert.True(result.RequiresPrimaryRepair);
        Assert.NotNull(result.PrimaryFailure);
    }

    [Fact]
    public async Task WriteAsync_FirstWriteAndReplacementAreRecoverable()
    {
        var path = GetPath();
        await AtomicJsonFileStorage.WriteAsync(
            path,
            Utf8("{\"Value\":1}"),
            replaceInvalidPrimary: false);
        await AtomicJsonFileStorage.WriteAsync(
            path,
            Utf8("{\"Value\":2}"),
            replaceInvalidPrimary: false);

        Assert.Equal("{\"Value\":2}", await File.ReadAllTextAsync(path));
        Assert.Equal(
            "{\"Value\":1}",
            await File.ReadAllTextAsync(
                AtomicJsonFileStorage.GetBackupPath(path)));
        Assert.False(File.Exists(AtomicJsonFileStorage.GetTemporaryPath(path)));
    }

    [Fact]
    public async Task WriteAsync_NormalizesUtf8BomOutOfPersistedState()
    {
        var path = GetPath();
        var json = Utf8("{\"Value\":9}").ToArray();
        byte[] withBomBytes = [0xEF, 0xBB, 0xBF, .. json];
        ReadOnlyMemory<byte> withBom = withBomBytes;

        await AtomicJsonFileStorage.WriteAsync(
            path,
            withBom,
            replaceInvalidPrimary: false);

        var persisted = await File.ReadAllBytesAsync(path);
        Assert.False(
            persisted.Length >= 3 &&
            persisted[0] == 0xEF &&
            persisted[1] == 0xBB &&
            persisted[2] == 0xBF);
        Assert.Equal("{\"Value\":9}", Encoding.UTF8.GetString(persisted));
    }

    [Fact]
    public async Task WriteAsync_RepairPreservesKnownGoodBackupAndQuarantinesPrimary()
    {
        var path = GetPath();
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, "broken");
        await File.WriteAllTextAsync(
            AtomicJsonFileStorage.GetBackupPath(path),
            "{\"Value\":4}");

        await AtomicJsonFileStorage.WriteAsync(
            path,
            Utf8("{\"Value\":5}"),
            replaceInvalidPrimary: true);

        Assert.Equal("{\"Value\":5}", await File.ReadAllTextAsync(path));
        Assert.Equal(
            "{\"Value\":4}",
            await File.ReadAllTextAsync(
                AtomicJsonFileStorage.GetBackupPath(path)));
        Assert.Equal(
            "broken",
            await File.ReadAllTextAsync(
                AtomicJsonFileStorage.GetInvalidPath(path)));
    }

    [Fact]
    public async Task WriteAsync_InvalidReplacementLeavesPrimaryUntouched()
    {
        var path = GetPath();
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, "{\"Value\":1}");

        await Assert.ThrowsAnyAsync<JsonException>(async () =>
            await AtomicJsonFileStorage.WriteAsync(
                path,
                Utf8("{broken"),
                replaceInvalidPrimary: false));

        Assert.Equal("{\"Value\":1}", await File.ReadAllTextAsync(path));
        Assert.False(File.Exists(AtomicJsonFileStorage.GetTemporaryPath(path)));
    }

    [Fact]
    public async Task ReadAsync_HonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await AtomicJsonFileStorage.ReadAsync(
                GetPath(),
                Deserialize,
                cancellationToken: cancellation.Token));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private string GetPath() => Path.Combine(directory, "settings.json");

    private static ReadOnlyMemory<byte> Utf8(string value) =>
        Encoding.UTF8.GetBytes(value);

    private static Task<JsonFileReadResult<ValueModel>> ReadValueAsync(
        string path) =>
        AtomicJsonFileStorage.ReadAsync(path, Deserialize);

    private static ValueModel Deserialize(JsonElement element) =>
        element.Deserialize<ValueModel>()
        ?? throw new JsonException("Missing model.");

    private sealed class ValueModel
    {
        public int Value { get; set; }
    }
}
