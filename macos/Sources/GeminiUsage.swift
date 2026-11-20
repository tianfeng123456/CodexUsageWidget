import Foundation
import CoreServices
import SQLite3

struct WeeklyTokenDay: Equatable, Identifiable {
    var localDate: String
    var totalTokens: Int64?
    var eventCount: Int
    var id: String { localDate }
}

struct GeminiUsageResult {
    var home: String
    var snapshot: Snapshot
    var weeklyTokens: [WeeklyTokenDay]
    var todayTokens: Int64
    var recent5hTokens: Int64
    var taskModels: [String: [String]]
}

struct GeminiTokenEvent {
    var timestamp: Date
    var inputTokens: Int64
    var cachedInputTokens: Int64
    var outputTokens: Int64
    var model: String
    var totalTokens: Int64 { inputTokens + outputTokens }
}

final class GeminiUsageScanner {
    private struct FileCacheEntry {
        var mtime: TimeInterval
        var size: UInt64
        var events: [GeminiTokenEvent]
        var fallbackTitle: String?
    }

    private struct ConversationMeta {
        var titles: [String: String] = [:]
        var parents: [String: String] = [:]
        var dbMtime: TimeInterval = 0
        var dbSize: UInt64 = 0
    }

    private var fileCache: [String: FileCacheEntry] = [:]
    private var meta = ConversationMeta()
    private let calendar: Calendar

    init(calendar: Calendar = .current) {
        self.calendar = calendar
    }

    static func resolveHome(_ custom: String) -> String {
        let trimmed = custom.trimmingCharacters(in: .whitespacesAndNewlines)
        if !trimmed.isEmpty {
            return (trimmed as NSString).expandingTildeInPath
        }
        return FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".gemini/antigravity")
            .path
    }

    func resetCache() {
        fileCache.removeAll()
        meta = ConversationMeta()
    }

    func scan(home customHome: String, period: Int, now: Date = Date()) -> GeminiUsageResult {
        let home = Self.resolveHome(customHome)
        let fm = FileManager.default
        let brainDir = (home as NSString).appendingPathComponent("brain")
        let dbPath = (home as NSString).appendingPathComponent("conversation_summaries.db")

        refreshMetadataIfNeeded(dbPath: dbPath)

        var activePaths = Set<String>()
        var eventsByConversation: [String: [GeminiTokenEvent]] = [:]
        var fallbackTitles: [String: String] = [:]

        if let entries = try? fm.contentsOfDirectory(atPath: brainDir) {
            for conversationId in entries {
                if conversationId.hasPrefix(".") { continue }
                let transcriptPath = ((brainDir as NSString).appendingPathComponent(conversationId) as NSString)
                    .appendingPathComponent(".system_generated/logs/transcript.jsonl")
                guard let attrs = try? fm.attributesOfItem(atPath: transcriptPath),
                      let modDate = attrs[.modificationDate] as? Date,
                      let fileSize = attrs[.size] as? NSNumber else { continue }
                activePaths.insert(transcriptPath)
                let mtime = modDate.timeIntervalSince1970
                let size = fileSize.uint64Value
                let entry: FileCacheEntry
                if let cached = fileCache[transcriptPath], cached.mtime == mtime, cached.size == size {
                    entry = cached
                } else {
                    let parsed = parseTranscript(at: transcriptPath)
                    entry = FileCacheEntry(mtime: mtime, size: size, events: parsed.events, fallbackTitle: parsed.fallbackTitle)
                    fileCache[transcriptPath] = entry
                }
                if !entry.events.isEmpty {
                    eventsByConversation[conversationId] = entry.events
                }
                if let fallback = entry.fallbackTitle, !fallback.isEmpty {
                    fallbackTitles[conversationId] = fallback
                }
            }
        }

        // Prune deleted transcripts from cache.
        if fileCache.count > activePaths.count {
            fileCache = fileCache.filter { activePaths.contains($0.key) }
        }

        let startOfToday = calendar.startOfDay(for: now)
        let periodStart: Date? = {
            switch period {
            case 0: return startOfToday
            case 1: return calendar.date(byAdding: .day, value: -6, to: startOfToday)
            case 2:
                let comps = calendar.dateComponents([.year, .month], from: now)
                return calendar.date(from: comps)
            default: return nil
            }
        }()
        let fiveHoursAgo = now.addingTimeInterval(-5 * 3600)

        var todayTokens: Int64 = 0
        var recent5hTokens: Int64 = 0

        // Prepare 7-day local date buckets (today - 6 ... today).
        let dayFormatter = DateFormatter()
        dayFormatter.calendar = calendar
        dayFormatter.timeZone = calendar.timeZone
        dayFormatter.locale = Locale(identifier: "en_US_POSIX")
        dayFormatter.dateFormat = "yyyy-MM-dd"

        var weekDates: [String] = []
        var weekTotals: [String: Int64] = [:]
        var weekCounts: [String: Int] = [:]
        for offset in (-6)...0 {
            if let d = calendar.date(byAdding: .day, value: offset, to: startOfToday) {
                let key = dayFormatter.string(from: d)
                weekDates.append(key)
            }
        }
        let weekDateSet = Set(weekDates)

        // Aggregate per root task for the selected period.
        var taskUsage: [String: Tokens] = [:]
        var taskModelsSet: [String: Set<String>] = [:]
        var totalUsage = Tokens()

        for (conversationId, events) in eventsByConversation {
            let rootId = resolveRoot(conversationId)
            for ev in events {
                let evTotal = ev.totalTokens
                if ev.timestamp >= startOfToday && ev.timestamp <= now.addingTimeInterval(60) {
                    todayTokens += evTotal
                }
                if ev.timestamp >= fiveHoursAgo && ev.timestamp <= now.addingTimeInterval(60) {
                    recent5hTokens += evTotal
                }

                let dayKey = dayFormatter.string(from: ev.timestamp)
                if weekDateSet.contains(dayKey) {
                    weekTotals[dayKey, default: 0] += evTotal
                    weekCounts[dayKey, default: 0] += 1
                }

                if let start = periodStart, ev.timestamp < start { continue }
                var t = taskUsage[rootId] ?? Tokens()
                t.inputTokens += ev.inputTokens
                t.cachedInputTokens += ev.cachedInputTokens
                t.outputTokens += ev.outputTokens
                t.totalTokens += evTotal
                taskUsage[rootId] = t

                totalUsage.inputTokens += ev.inputTokens
                totalUsage.cachedInputTokens += ev.cachedInputTokens
                totalUsage.outputTokens += ev.outputTokens
                totalUsage.totalTokens += evTotal

                if !ev.model.isEmpty {
                    taskModelsSet[rootId, default: []].insert(ev.model)
                }
            }
        }

        let sortedTasks = taskUsage.sorted { lhs, rhs in
            if lhs.value.totalTokens != rhs.value.totalTokens {
                return lhs.value.totalTokens > rhs.value.totalTokens
            }
            return lhs.key < rhs.key
        }

        let topSlice = sortedTasks.prefix(9)
        let otherSlice = sortedTasks.dropFirst(9)

        let topTasks: [TaskUsage] = topSlice.map { (rootId, usage) in
            let pct = totalUsage.totalTokens > 0 ? Double(usage.totalTokens) / Double(totalUsage.totalTokens) * 100.0 : 0.0
            let title = resolveTitle(rootId: rootId, fallbackTitles: fallbackTitles)
            return TaskUsage(rootTaskId: rootId, title: title, usage: usage, percentOfPeriod: pct, isArchived: false)
        }

        var otherTotal = Tokens()
        for (_, usage) in otherSlice {
            otherTotal.inputTokens += usage.inputTokens
            otherTotal.cachedInputTokens += usage.cachedInputTokens
            otherTotal.outputTokens += usage.outputTokens
            otherTotal.totalTokens += usage.totalTokens
        }

        let iso = ISO8601DateFormatter()
        iso.formatOptions = [.withInternetDateTime]
        let snapshot = Snapshot(
            period: period,
            topTasks: topTasks,
            summary: Summary(total: totalUsage, taskCount: sortedTasks.count, otherTasksTotal: otherTotal),
            lastUpdated: iso.string(from: now)
        )

        let weeklyDays: [WeeklyTokenDay] = weekDates.map { key in
            let count = weekCounts[key] ?? 0
            return WeeklyTokenDay(localDate: key, totalTokens: count > 0 ? (weekTotals[key] ?? 0) : nil, eventCount: count)
        }

        let taskModels = taskModelsSet.mapValues { $0.sorted() }

        return GeminiUsageResult(
            home: home,
            snapshot: snapshot,
            weeklyTokens: weeklyDays,
            todayTokens: todayTokens,
            recent5hTokens: recent5hTokens,
            taskModels: taskModels
        )
    }

    private func resolveRoot(_ id: String) -> String {
        var current = id
        var visited = Set<String>([current])
        for _ in 0..<32 {
            guard let parent = meta.parents[current], !parent.isEmpty, !visited.contains(parent) else { break }
            visited.insert(parent)
            current = parent
        }
        return current
    }

    private func resolveTitle(rootId: String, fallbackTitles: [String: String]) -> String {
        if let explicit = meta.titles[rootId], !explicit.isEmpty {
            return explicit
        }
        if let fallback = fallbackTitles[rootId], !fallback.isEmpty {
            return fallback
        }
        return "Gemini " + String(rootId.prefix(8))
    }

    private func refreshMetadataIfNeeded(dbPath: String) {
        let fm = FileManager.default
        guard let attrs = try? fm.attributesOfItem(atPath: dbPath),
              let modDate = attrs[.modificationDate] as? Date,
              let size = attrs[.size] as? NSNumber else {
            meta = ConversationMeta()
            return
        }
        let walPath = dbPath + "-wal"
        let walAttrs = try? fm.attributesOfItem(atPath: walPath)
        let walMtime = (walAttrs?[.modificationDate] as? Date)?.timeIntervalSince1970 ?? 0
        let walSize = (walAttrs?[.size] as? NSNumber)?.uint64Value ?? 0
        let combinedMtime = max(modDate.timeIntervalSince1970, walMtime)
        let combinedSize = size.uint64Value &+ walSize

        if meta.dbMtime == combinedMtime && meta.dbSize == combinedSize {
            return
        }

        var db: OpaquePointer?
        guard sqlite3_open_v2(dbPath, &db, SQLITE_OPEN_READONLY, nil) == SQLITE_OK, let handle = db else {
            if let db { sqlite3_close(db) }
            return
        }
        defer { sqlite3_close(handle) }
        sqlite3_busy_timeout(handle, 200)

        var titles: [String: String] = [:]
        var parents: [String: String] = [:]

        var stmt: OpaquePointer?
        let query1 = "SELECT conversation_id, title, parent_conversation_id FROM conversation_summaries"
        if sqlite3_prepare_v2(handle, query1, -1, &stmt, nil) == SQLITE_OK {
            defer { sqlite3_finalize(stmt) }
            while sqlite3_step(stmt) == SQLITE_ROW {
                guard let cidPtr = sqlite3_column_text(stmt, 0) else { continue }
                let cid = String(cString: cidPtr)
                if let titlePtr = sqlite3_column_text(stmt, 1) {
                    let title = String(cString: titlePtr).trimmingCharacters(in: .whitespacesAndNewlines)
                    if !title.isEmpty { titles[cid] = title }
                }
                if let parentPtr = sqlite3_column_text(stmt, 2) {
                    let parent = String(cString: parentPtr).trimmingCharacters(in: .whitespacesAndNewlines)
                    if !parent.isEmpty { parents[cid] = parent }
                }
            }
        }

        var stmt2: OpaquePointer?
        let query2 = "SELECT conversation_id, parent_id FROM conversation_parents"
        if sqlite3_prepare_v2(handle, query2, -1, &stmt2, nil) == SQLITE_OK {
            defer { sqlite3_finalize(stmt2) }
            while sqlite3_step(stmt2) == SQLITE_ROW {
                guard let cidPtr = sqlite3_column_text(stmt2, 0),
                      let pidPtr = sqlite3_column_text(stmt2, 1) else { continue }
                let cid = String(cString: cidPtr)
                let pid = String(cString: pidPtr).trimmingCharacters(in: .whitespacesAndNewlines)
                if !pid.isEmpty && parents[cid] == nil { parents[cid] = pid }
            }
        }

        meta = ConversationMeta(titles: titles, parents: parents, dbMtime: combinedMtime, dbSize: combinedSize)
    }

    private func parseTranscript(at path: String) -> (events: [GeminiTokenEvent], fallbackTitle: String?) {
        guard let data = try? Data(contentsOf: URL(fileURLWithPath: path), options: .mappedIfSafe) else {
            return ([], nil)
        }
        var events: [GeminiTokenEvent] = []
        var fallbackTitle: String? = nil

        let tokenMarker = Data("\"input_tokens\"".utf8)
        let outputMarker = Data("\"output_tokens\"".utf8)
        let userMarker = Data("\"USER_INPUT\"".utf8)

        var start = data.startIndex
        while start < data.endIndex {
            let end = data[start...].firstIndex(of: 10) ?? data.endIndex
            let line = data[start..<end]
            start = end < data.endIndex ? data.index(after: end) : data.endIndex
            guard !line.isEmpty else { continue }

            let hasTokens = line.range(of: tokenMarker) != nil || line.range(of: outputMarker) != nil
            let needUserFallback = fallbackTitle == nil && line.range(of: userMarker) != nil
            guard hasTokens || needUserFallback else { continue }

            guard let obj = try? JSONSerialization.jsonObject(with: line) as? [String: Any] else { continue }

            if needUserFallback, let type = obj["type"] as? String, type == "USER_INPUT",
               let content = obj["content"] as? String {
                let cleaned = extractCleanUserSnippet(content)
                if !cleaned.isEmpty { fallbackTitle = cleaned }
            }

            if hasTokens {
                let rawIn = int64Value(obj["input_tokens"])
                let rawCache = int64Value(obj["cache_read_tokens"])
                let rawOut = int64Value(obj["output_tokens"])
                if rawIn == 0 && rawCache == 0 && rawOut == 0 { continue }
                guard let tsRaw = obj["created_at"] as? String, let ts = parsedDate(tsRaw) else { continue }
                let model = obj["model"] as? String ?? ""
                // Align with Codex token accounting: inputTokens includes cached reads; totalTokens = inputTokens + outputTokens.
                let totalInput = rawIn + rawCache
                events.append(GeminiTokenEvent(
                    timestamp: ts,
                    inputTokens: totalInput,
                    cachedInputTokens: rawCache,
                    outputTokens: rawOut,
                    model: model
                ))
            }
        }
        return (events, fallbackTitle)
    }

    private func int64Value(_ value: Any?) -> Int64 {
        if let n = value as? NSNumber { return max(0, n.int64Value) }
        if let s = value as? String, let n = Int64(s) { return max(0, n) }
        return 0
    }

    private func extractCleanUserSnippet(_ raw: String) -> String {
        var text = raw
        if let open = text.range(of: "<USER_REQUEST>"), let close = text.range(of: "</USER_REQUEST>") {
            text = String(text[open.upperBound..<close.lowerBound])
        }
        let firstLine = text.components(separatedBy: .newlines)
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .first { !$0.isEmpty } ?? ""
        if firstLine.count > 40 {
            return String(firstLine.prefix(40)) + "…"
        }
        return firstLine
    }
}

final class GeminiUsageMonitor {
    private let queue = DispatchQueue(label: "widget.gemini.monitor")
    private let scanner = GeminiUsageScanner()
    private var stream: FSEventStreamRef?
    private var debounceWork: DispatchWorkItem?
    private var firstChange: Date?
    private var watchedHome = ""
    private var currentPeriod = 0
    private var running = false
    var onUpdate: ((GeminiUsageResult) -> Void)?

    func start(home customHome: String, period: Int) {
        queue.async { [weak self] in
            guard let self else { return }
            self.stopInternal()
            self.running = true
            self.currentPeriod = period
            let resolved = GeminiUsageScanner.resolveHome(customHome)
            if self.watchedHome != resolved {
                self.scanner.resetCache()
                self.watchedHome = resolved
            }
            self.emitScan(home: resolved, period: period)
            self.startWatching(home: resolved)
        }
    }

    func refresh(home customHome: String, period: Int, resetCache: Bool = false) {
        queue.async { [weak self] in
            guard let self, self.running else { return }
            self.currentPeriod = period
            let resolved = GeminiUsageScanner.resolveHome(customHome)
            if resetCache || self.watchedHome != resolved {
                self.scanner.resetCache()
                if self.watchedHome != resolved {
                    self.stopWatching()
                    self.watchedHome = resolved
                    self.startWatching(home: resolved)
                }
            }
            self.emitScan(home: resolved, period: period)
        }
    }

    func stop() {
        queue.async { [weak self] in
            self?.stopInternal()
        }
    }

    private func stopInternal() {
        running = false
        debounceWork?.cancel()
        debounceWork = nil
        firstChange = nil
        stopWatching()
    }

    private func emitScan(home: String, period: Int) {
        let result = scanner.scan(home: home, period: period)
        DispatchQueue.main.async { [weak self] in
            self?.onUpdate?(result)
        }
    }

    private func startWatching(home: String) {
        guard FileManager.default.fileExists(atPath: home) else { return }
        var context = FSEventStreamContext(
            version: 0,
            info: Unmanaged.passUnretained(self).toOpaque(),
            retain: nil,
            release: nil,
            copyDescription: nil
        )
        let paths = [home] as CFArray
        let flags = FSEventStreamCreateFlags(
            kFSEventStreamCreateFlagUseCFTypes |
            kFSEventStreamCreateFlagFileEvents |
            kFSEventStreamCreateFlagNoDefer
        )
        let callback: FSEventStreamCallback = { _, info, numEvents, eventPaths, _, _ in
            guard let info else { return }
            let monitor = Unmanaged<GeminiUsageMonitor>.fromOpaque(info).takeUnretainedValue()
            let paths = unsafeBitCast(eventPaths, to: NSArray.self)
            var relevant = false
            for i in 0..<numEvents {
                if let path = paths[i] as? String,
                   path.hasSuffix("transcript.jsonl") || path.contains("conversation_summaries.db") {
                    relevant = true
                    break
                }
            }
            if relevant { monitor.scheduleDebouncedScan() }
        }
        guard let stream = FSEventStreamCreate(nil, callback, &context, paths, FSEventStreamEventId(kFSEventStreamEventIdSinceNow), 0.5, flags) else {
            return
        }
        FSEventStreamSetDispatchQueue(stream, queue)
        FSEventStreamStart(stream)
        self.stream = stream
    }

    private func stopWatching() {
        guard let stream else { return }
        FSEventStreamStop(stream)
        FSEventStreamInvalidate(stream)
        FSEventStreamRelease(stream)
        self.stream = nil
    }

    private func scheduleDebouncedScan() {
        guard running else { return }
        let now = Date()
        if firstChange == nil { firstChange = now }
        let elapsed = now.timeIntervalSince(firstChange ?? now)
        let delay = max(0, min(1.5, 3.0 - elapsed))
        debounceWork?.cancel()
        let work = DispatchWorkItem { [weak self] in
            guard let self, self.running else { return }
            self.firstChange = nil
            self.emitScan(home: self.watchedHome, period: self.currentPeriod)
        }
        debounceWork = work
        queue.asyncAfter(deadline: .now() + delay, execute: work)
    }

    deinit {
        stopWatching()
    }
}
