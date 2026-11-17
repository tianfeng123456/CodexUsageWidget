using System.Text.Json;
using CodexUsageWidget.Core;

// Private stdio protocol. No network listener and no credential access.
await using var bridge = new Bridge();
await bridge.RunAsync();

sealed class Bridge : IAsyncDisposable
{
    readonly JsonSerializerOptions json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    readonly object outputLock = new();
    readonly object changeLock = new();
    readonly SemaphoreSlim gate = new(1, 1);
    readonly HashSet<string> changed = new(StringComparer.Ordinal);
    readonly List<FileSystemWatcher> watchers = [];
    readonly RateLimitTailMonitor tail = new();
    UsageIndexService? index;
    RateLimitSnapshot? latest;
    Timer? debounce;
    DateTime firstChange;
    bool disposed;
    bool recalibrate;
    int period;

    void Emit(string type, object? data) {
        lock (outputLock) Console.WriteLine(JsonSerializer.Serialize(new { type, data }, json));
    }

    public async Task RunAsync() {
        while (await Console.In.ReadLineAsync() is { } line) {
            try {
                using var message = JsonDocument.Parse(line);
                var root = message.RootElement;
                var command = root.GetProperty("command").GetString();
                if (root.TryGetProperty("period", out var requestedPeriod)) period = Math.Clamp(requestedPeriod.GetInt32(), 0, 3);
                await gate.WaitAsync();
                try {
                    if (command == "init") {
                        if (index != null) throw new InvalidOperationException("Already initialized");
                        var home = root.TryGetProperty("home", out var h) ? h.GetString() : null;
                        var data = root.GetProperty("dataDirectory").GetString()!;
                        var paths = CodexHomeLocator.Detect(home);
                        Directory.CreateDirectory(data);
                        var db = UsageIndexDatabasePath.ForHome(data, paths.HomeDirectory);
                        index = new(new UsageIndexOptions(paths.HomeDirectory, db, TimeZoneInfo.Local));
                        index.ProgressChanged += (_, p) => Emit("progress", new { p.Progress, stage = p.Stage.ToString() });
                        await index.OpenAsync();
                        Emit("ready", new { home = paths.HomeDirectory, database = db });
                        await CalibrateAsync();
                        Watch(paths.HomeDirectory);
                        // Initial history creation only; later launches use the existing index.
                        if (!index.HasCompletedInitialIndex) await index.RefreshAsync();
                        await SnapshotAsync();
                    } else if (command == "quit") { break; }
                    else {
                        if (index == null) throw new InvalidOperationException("Not initialized");
                        if (command == "refresh" || command == "weekly" || command == "rebuild") {
                            if (command == "rebuild") await index.RebuildIndexAsync();
                            else await index.RefreshAsync();
                            await CalibrateAsync();
                            if (command == "weekly") {
                                var now = DateTimeOffset.Now;
                                var start = DateTime.Today.AddDays(-6);
                                var from = new DateTimeOffset(start, TimeZoneInfo.Local.GetUtcOffset(start));
                                Emit("weekly", await index.QueryWeeklyRateLimitDailyUsageAsync(from, now));
                            }
                            await SnapshotAsync();
                        } else if (command == "calibrate") await CalibrateAsync();
                        else throw new InvalidOperationException("Unknown command");
                    }
                } finally { gate.Release(); }
            } catch (Exception e) {
                Emit("error", new { message = e.GetBaseException().Message, kind = e.GetType().Name });
            }
        }
    }
    async Task SnapshotAsync() {
        var snapshot = await index!.QueryPeriodAsync((UsagePeriod)period);
        var titles = await TaskTitleCatalog.ReadAsync(index.Paths.HomeDirectory);
        Emit("snapshot", snapshot with {
            TopTasks = snapshot.TopTasks.Select(task => task with {
                Title = titles.Resolve(task.RootTaskId, task.Title)
            }).ToArray()
        });
    }
    async Task CalibrateAsync() => Accept(await index!.ReadLatestRateLimitsFromRecentLogsAsync(), true);
    void Accept(RateLimitSnapshot? candidate, bool force = false) {
        if (candidate != null && (latest == null || candidate.Timestamp >= latest.Timestamp)) {
            var previous = latest;
            latest = candidate;
            if (force || previous != latest) Emit("quota", latest);
        } else if (force) Emit("quota", latest);
    }
    void Watch(string home) {
        // Monitor only the two usage trees, never auth.json or chat attachments.
        foreach (var folder in new[] { "sessions", "archived_sessions" }) {
            var directory = Path.Combine(home, folder);
            if (!Directory.Exists(directory)) continue;
            var w = new FileSystemWatcher(directory, "*.jsonl") {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.DirectoryName
            };
            w.Changed += (_, e) => Changed(e.FullPath);
            w.Created += (_, e) => Changed(e.FullPath);
            w.Renamed += (_, e) => { tail.MoveCheckpoint(e.OldFullPath, e.FullPath); Changed(e.FullPath); };
            w.Deleted += (_, e) => tail.ForgetFile(e.FullPath);
            w.Error += (_, _) => { lock(changeLock) recalibrate = true; Changed(null); };
            w.EnableRaisingEvents = true;
            watchers.Add(w);
        }
    }
    void Changed(string? path) {
        lock(changeLock) {
            if (disposed) return;
            if (firstChange == default) firstChange = DateTime.UtcNow;
            if (path != null) changed.Add(path);
            var delay = Math.Max(0, Math.Min(1500, 3000 - (DateTime.UtcNow-firstChange).TotalMilliseconds));
            debounce ??= new Timer(async _ => await TailAsync(), null, Timeout.Infinite, Timeout.Infinite);
            debounce.Change((int)delay, Timeout.Infinite);
        }
    }
    async Task TailAsync() {
        await gate.WaitAsync();
        try {
            if (disposed || index == null) return;
            string[] paths; bool full;
            lock(changeLock) { paths = changed.ToArray(); changed.Clear(); firstChange = default; full = recalibrate; recalibrate = false; }
            if (full) await CalibrateAsync();
            else Accept((await tail.ReadChangedFilesAsync(paths)).LatestSnapshot);
        } catch (Exception e) { Emit("error", new { message = e.GetBaseException().Message, kind = e.GetType().Name }); }
        finally { gate.Release(); }
    }
    public async ValueTask DisposeAsync() {
        lock(changeLock) { disposed = true; debounce?.Dispose(); }
        foreach(var w in watchers) w.Dispose();
        await gate.WaitAsync();
        try { if (index != null) await index.DisposeAsync(); } finally { gate.Release(); }
    }
}
