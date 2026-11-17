using CodexUsageWidget.Core;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;

namespace CodexUsageWidget.Tests;

public sealed class TaskTitleCatalogTests
{
    [Fact]
    public async Task ExplicitAppNameWins_AndLegacyTitleOnlyFillsMissingIndexTitle()
    {
        using var temp = new TemporaryDirectory();
        var home = temp.GetPath("home"); Directory.CreateDirectory(home);
        var db = Path.Combine(home, "state_5.sqlite");
        await CreateAsync(db, "CREATE TABLE threads(id TEXT, name TEXT, title TEXT);");
        await InsertAsync(db, "named-id", "恢复的原任务名称", "Old initial prompt");
        await InsertAsync(db, "legacy-id", null, "简短旧标题");
        var before = SHA256.HashData(await File.ReadAllBytesAsync(db));
        var catalog = await TaskTitleCatalog.ReadAsync(home);
        Assert.Equal("恢复的原任务名称", catalog.Resolve("named-id", "Outdated index title"));
        Assert.Equal("简短旧标题", catalog.Resolve("legacy-id", "未命名任务 legacy-i"));
        Assert.Equal("Updated session index", catalog.Resolve("legacy-id", "Updated session index"));
        Assert.Equal("未命名任务 absent", catalog.Resolve("absent", "未命名任务 absent"));
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(db)));
    }

    [Fact]
    public async Task DoesNotUseLongOrMultilinePromptFallbacks()
    {
        using var temp = new TemporaryDirectory();
        var home = temp.GetPath("home"); Directory.CreateDirectory(home);
        var db = Path.Combine(home, "state_5.sqlite");
        await CreateAsync(db, "CREATE TABLE threads(id TEXT, name TEXT, title TEXT);");
        await InsertAsync(db, "long", null, new string('x', 300));
        await InsertAsync(db, "multiline", null, "# Files mentioned\nPrompt body");
        await InsertAsync(db, "named", "  真正的\t名称  ", new string('x', 10000));
        var catalog = await TaskTitleCatalog.ReadAsync(home);
        Assert.Equal("未命名任务 long", catalog.Resolve("long", "未命名任务 long"));
        Assert.Equal("未命名任务 multilin", catalog.Resolve("multiline", "未命名任务 multilin"));
        Assert.Equal("真正的 名称", catalog.Resolve("named", ""));
    }

    [Fact]
    public async Task UsesNewestNumericSchema_AndReloadsRenamesOnNextRead()
    {
        using var temp = new TemporaryDirectory();
        var home = temp.GetPath("home"); Directory.CreateDirectory(home);
        foreach (var version in new[] { 5, 10 })
        {
            var db = Path.Combine(home, $"state_{version}.sqlite");
            await CreateAsync(db, "CREATE TABLE threads(id TEXT, name TEXT, title TEXT);");
            await InsertAsync(db, "task", $"Version {version}", null);
        }
        Assert.Equal("Version 10", (await TaskTitleCatalog.ReadAsync(home)).Resolve("task", ""));
        await using (var db = new SqliteConnection($"Data Source={Path.Combine(home, "state_10.sqlite")};Pooling=False"))
        {
            await db.OpenAsync(); await using var update = db.CreateCommand();
            update.CommandText = "UPDATE threads SET name='Renamed';"; await update.ExecuteNonQueryAsync();
        }
        Assert.Equal("Renamed", (await TaskTitleCatalog.ReadAsync(home)).Resolve("task", ""));
        var otherHome = temp.GetPath("other"); Directory.CreateDirectory(otherHome);
        Assert.Equal("Existing", (await TaskTitleCatalog.ReadAsync(otherHome)).Resolve("task", "Existing"));
        Assert.Empty(Directory.GetFiles(otherHome));
    }

    [Fact]
    public async Task SupportsLegacySchemaWithoutName_AndIgnoresCorruptNewerDatabase()
    {
        using var temp = new TemporaryDirectory();
        var home = temp.GetPath("home"); Directory.CreateDirectory(home);
        var db = Path.Combine(home, "state_4.sqlite");
        await CreateAsync(db, "CREATE TABLE threads(id TEXT, title TEXT); INSERT INTO threads VALUES('task', 'Legacy title');");
        await File.WriteAllTextAsync(Path.Combine(home, "state_5.sqlite"), "corrupt");
        Assert.Equal("Legacy title", (await TaskTitleCatalog.ReadAsync(home)).Resolve("task", "未命名任务 task"));
        Assert.Equal("Existing", (await TaskTitleCatalog.ReadAsync(temp.GetPath("missing"))).Resolve("task", "Existing"));
    }

    private static async Task CreateAsync(string path, string sql)
    {
        await using var db = new SqliteConnection($"Data Source={path};Pooling=False");
        await db.OpenAsync(); await using var command = db.CreateCommand(); command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
    private static async Task InsertAsync(string path, string id, string? name, string? title)
    {
        await using var db = new SqliteConnection($"Data Source={path};Pooling=False");
        await db.OpenAsync(); await using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO threads VALUES($id,$name,$title);";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$name", (object?)name ?? DBNull.Value);
        command.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value); await command.ExecuteNonQueryAsync();
    }
}
