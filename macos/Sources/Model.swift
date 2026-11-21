import Foundation
import AppKit
import Combine
import ServiceManagement

enum UsageProvider: String, Codable, CaseIterable {
    case codex
    case gemini
}

struct Preferences: Codable, Equatable {
    var theme = "dark"
    var language = "system"
    var idleStyle = "circle"
    // Optional storage keeps settings written before mac.5 fully compatible.
    var iconScale: Double? = nil
    var idleScale: Double {
        get { let value = iconScale ?? 1; return value.isFinite ? min(1.25,max(0.5,value)) : 1 }
        set { iconScale = newValue.isFinite ? min(1.25,max(0.5,newValue)) : 1 }
    }
    var transparency = 0.5
    var alwaysOnTop = true
    var autoCollapse = true
    var pauseOnSleep = true
    var home = ""
    // Optional storage keeps settings written before mac.7 fully compatible.
    var provider: String? = nil
    var activeProvider: UsageProvider {
        get { UsageProvider(rawValue: provider ?? "codex") ?? .codex }
        set { provider = newValue.rawValue }
    }
    var geminiHome: String? = nil
    var geminiHomePath: String {
        get { geminiHome ?? "" }
        set { geminiHome = newValue.isEmpty ? nil : newValue }
    }
    var period = 0
    var x: Double? = nil
    var y: Double? = nil
    func normalized() -> Preferences {
        var p = self
        if !["system","light","dark"].contains(p.theme) { p.theme = "dark" }
        if !["system","zh","en"].contains(p.language) { p.language = "system" }
        if !["circle","glow","capsule"].contains(p.idleStyle) { p.idleStyle = "circle" }
        if p.iconScale != nil { p.idleScale = p.idleScale }
        if let rawProvider = p.provider, UsageProvider(rawValue: rawProvider) == nil { p.provider = "codex" }
        p.period = min(3,max(0,p.period)); p.transparency = min(1,max(0,p.transparency))
        return p
    }
}
struct Tokens: Decodable {
    var inputTokens: Int64 = 0
    var cachedInputTokens: Int64 = 0
    var outputTokens: Int64 = 0
    var reasoningOutputTokens: Int64 = 0
    var totalTokens: Int64 = 0
}
struct TaskUsage: Decodable, Identifiable {
    var rootTaskId: String
    var title: String
    var usage: Tokens
    var percentOfPeriod: Double
    var isArchived: Bool
    var id: String { rootTaskId }
}
struct Summary: Decodable {
    var total: Tokens
    var taskCount: Int
    var otherTasksTotal: Tokens
}
struct Snapshot: Decodable {
    var period: Int
    var topTasks: [TaskUsage]
    var summary: Summary
    var lastUpdated: String
}
struct QuotaWindow: Decodable {
    var usedPercent: Double
    var windowMinutes: Int?
    var resetsAt: String?
    var remainingPercent: Double { max(0, min(100, 100-usedPercent)) }
}
struct Quota: Decodable {
    var timestamp: String
    var limitId: String?
    var planType: String?
    var primary: QuotaWindow?
    var secondary: QuotaWindow?
    var windows: [QuotaWindow] { [primary,secondary].compactMap { $0 } }
    var remaining: Double? { windows.map(\.remainingPercent).min() }
    var weekly: QuotaWindow? { windows.first { $0.windowMinutes == 10080 } }
}
struct WeeklyDay: Decodable, Identifiable {
    var localDate: String
    var consumedPercentagePoints: Double?
    var changeFromPreviousDayPercentagePoints: Double?
    var lastObservedUsedPercent: Double?
    var observationCount: Int
    var isPartial: Bool
    var id: String { localDate }
}
func parsedDate(_ text: String) -> Date? {
    let parser = ISO8601DateFormatter()
    parser.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
    if let d = parser.date(from: text) { return d }
    parser.formatOptions = [.withInternetDateTime]
    return parser.date(from: text)
}
func formatTokens(_ value: Int64, chinese: Bool) -> String {
    let n = Double(value)
    let units: [(Double,String)] = chinese ? [(1e12,"万亿"),(1e8,"亿"),(1e4,"万")] : [(1e12,"T"),(1e9,"B"),(1e6,"M"),(1e3,"K")]
    let formatter = NumberFormatter(); formatter.locale = Locale(identifier: chinese ? "zh_CN" : "en_US")
    if let (scale,suffix) = units.first(where: { n >= $0.0 }) {
        formatter.maximumFractionDigits = 2
        return (formatter.string(from: NSNumber(value: n/scale)) ?? "0") + suffix
    }
    formatter.numberStyle = .decimal; formatter.maximumFractionDigits = 0
    return formatter.string(from: NSNumber(value: value)) ?? "0"
}
func formatCompactTokens(_ value: Int64, chinese: Bool) -> String {
    if value <= 0 { return "0" }
    let n = Double(value)
    let units: [(Double,String)] = chinese ? [(1e12,"万亿"),(1e8,"亿"),(1e4,"万")] : [(1e12,"T"),(1e9,"B"),(1e6,"M"),(1e3,"K")]
    if let (scale,suffix) = units.first(where: { n >= $0.0 }) {
        let scaled = n / scale
        if scaled >= 100 { return String(format: "%.0f%@", scaled, suffix) }
        if scaled >= 10 { return String(format: "%.1f%@", scaled, suffix) }
        return String(format: "%.2f%@", scaled, suffix)
    }
    return String(value)
}

private final class BridgeLineReader {
    var buffer = Data()
    func consume(_ data: Data) -> [Data] {
        buffer.append(data)
        var lines: [Data] = []
        while let end = buffer.firstIndex(of: 10) {
            lines.append(Data(buffer[..<end])); buffer.removeSubrange(...end)
        }
        return lines
    }
}

final class Model: ObservableObject {
    static let dataDirectory = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support/CodexUsageWidget")
    static var settingsURL: URL { dataDirectory.appendingPathComponent("settings.json") }
    @Published var preferences: Preferences { didSet { save(); settingsChanged?(oldValue) } }
    @Published var loginEnabled = SMAppService.mainApp.status == .enabled || SMAppService.mainApp.status == .requiresApproval
    @Published var loginError: String?
    @Published var quota: Quota?
    @Published var snapshot: Snapshot?
    @Published var weekly: [WeeklyDay] = []
    @Published var geminiQuota: Quota?
    @Published var geminiSnapshot: Snapshot?
    @Published var geminiWeeklyTokens: [WeeklyTokenDay] = []
    @Published var geminiTodayTokens: Int64 = 0
    @Published var geminiRecent5hTokens: Int64 = 0
    @Published var geminiSourceHome = ""
    @Published var geminiTaskModels: [String: [String]] = [:]
    @Published var geminiBusy = false
    @Published var geminiError: String?
    @Published var headerControls: [CGRect] = []
    @Published var settingsOpen = false
    @Published var expanded = false
    @Published var pinned = false
    @Published var showingWeekly = false
    @Published var busy = false
    @Published var paused = false
    @Published var progress: Double = 0
    @Published var progressStage = ""
    @Published var error: String?
    @Published var sourceHome = ""
    @Published var updatedAt: Date?
    private let persistSettings: Bool
    var settingsChanged: ((Preferences) -> Void)?
    var quotaChanged: (() -> Void)?
    private let geminiMonitor = GeminiUsageMonitor()
    private let geminiQuotaFetcher = GeminiQuotaFetcher()
    private var process: Process?
    private var input: FileHandle?
    private var output: FileHandle?
    private let readQueue = DispatchQueue(label: "widget.bridge.reader")
    private var generation = UUID()
    private var ready = false
    private var pendingCommand: [String:Any]?

    init(persistSettings: Bool = true) {
        self.persistSettings = persistSettings
        preferences = persistSettings ? ((try? JSONDecoder().decode(Preferences.self, from: Data(contentsOf: Self.settingsURL))) ?? Preferences()) : Preferences()
        preferences = preferences.normalized()
        geminiMonitor.onUpdate = { [weak self] result in
            guard let self else { return }
            self.geminiSourceHome = result.home
            if result.snapshot.period == self.preferences.period {
                self.geminiSnapshot = result.snapshot
            }
            self.geminiWeeklyTokens = result.weeklyTokens
            self.geminiTodayTokens = result.todayTokens
            self.geminiRecent5hTokens = result.recent5hTokens
            self.geminiTaskModels = result.taskModels
            self.geminiBusy = false
            self.geminiError = nil
            self.quotaChanged?()
            if !self.paused { self.fetchGeminiQuota(force: false) }
        }
    }
    var activeProvider: UsageProvider { preferences.activeProvider }
    var isGemini: Bool { activeProvider == .gemini }
    var activeSnapshot: Snapshot? { isGemini ? geminiSnapshot : snapshot }
    var activeBusy: Bool { isGemini ? geminiBusy : busy }
    var activeError: String? { isGemini ? geminiError : error }
    var activeQuota: Quota? { isGemini ? geminiQuota : quota }
    var chinese: Bool {
        preferences.language == "zh" || (preferences.language == "system" && Locale.preferredLanguages.first?.hasPrefix("zh") == true)
    }
    func t(_ zh: String, _ en: String) -> String { chinese ? zh : en }
    func number(_ n: Int64) -> String { formatTokens(n, chinese: chinese) }
    func compactNumber(_ n: Int64) -> String { formatCompactTokens(n, chinese: chinese) }
    var displayedRemaining: Int? { activeQuota?.remaining.map { Int($0.rounded(.toNearestOrAwayFromZero)) } }
    var remainingText: String {
        if isGemini && geminiQuota == nil {
            if geminiSnapshot == nil && geminiBusy { return "--" }
            return compactNumber(geminiTodayTokens)
        }
        return displayedRemaining.map { "\($0)%" } ?? "--"
    }
    var menuBarTitle: String {
        if isGemini {
            return "G " + remainingText
        }
        return remainingText
    }
    var geminiWeeklyTotal: Int64 {
        geminiWeeklyTokens.compactMap(\.totalTokens).reduce(0, +)
    }
    var statusText: String {
        if paused { return t("监控已暂停", "Monitoring paused") }
        if isGemini && geminiQuota == nil {
            if geminiSnapshot == nil && geminiBusy { return t("正在读取", "Reading") }
            return t("近5h ", "5h ") + compactNumber(geminiRecent5hTokens)
        }
        guard let r = displayedRemaining else { return busy ? t("正在同步", "Syncing") : t("等待数据", "Waiting for data") }
        switch r {
        case 70...: return t("额度充足", "Plenty remaining")
        case 30..<70: return t("用量平稳", "Usage steady")
        case 10..<30: return t("额度偏低", "Running low")
        case 1..<10: return t("即将用尽", "Almost exhausted")
        default: return t("已用尽", "Exhausted")
        }
    }
    var progressText: String {
        if isGemini { return t("正在读取本地日志…", "Reading local logs…") }
        return progressStage.isEmpty ? t("正在刷新…","Refreshing…") :
        progress >= 1 ? t("正在汇总…","Summarizing…") :
        t("正在索引 ","Indexing ")+String(format:"%.0f%%",progress*100)
    }
    func taskDetails(_ task:TaskUsage) -> String {
        let u = task.usage
        var lines = [
            task.title + (task.isArchived ? t("（已归档）"," (Archived)") : ""),
            t("总计 ","Total ")+number(u.totalTokens),
            t("输入 ","Input ")+number(u.inputTokens)+" · "+t("缓存 ","Cached ")+number(u.cachedInputTokens)
        ]
        if isGemini {
            lines.append(t("输出 ","Output ")+number(u.outputTokens))
            if let models = geminiTaskModels[task.rootTaskId], !models.isEmpty {
                lines.append(t("模型 ","Models ")+models.joined(separator:", "))
            }
        } else {
            lines.append(t("输出 ","Output ")+number(u.outputTokens)+" · "+t("推理 ","Reasoning ")+number(u.reasoningOutputTokens))
        }
        return lines.joined(separator:"\n")
    }
    func syncLoginStatus() {
        let status = SMAppService.mainApp.status
        let registered = status == .enabled || status == .requiresApproval
        if loginEnabled != registered { loginEnabled = registered }
    }
    func setLoginEnabled(_ value:Bool) {
        do {
            if value { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
            loginError = SMAppService.mainApp.status == .requiresApproval ?
                t("请在系统设置 → 通用 → 登录项中允许。","Allow this app in System Settings → General → Login Items.") : nil
        } catch { loginError = error.localizedDescription }
        syncLoginStatus(); settingsChanged?(preferences)
    }
    func dateText(_ raw: String?, timeOnly: Bool = false) -> String {
        guard let raw, let d = parsedDate(raw) else { return "--" }
        let f = DateFormatter(); f.locale = Locale(identifier: chinese ? "zh_CN" : "en_US")
        f.dateFormat = timeOnly && Calendar.current.isDateInToday(d) ? "HH:mm" : "MM-dd HH:mm"
        return f.string(from:d)
    }
    func save() {
        guard persistSettings else { return }
        do {
            try FileManager.default.createDirectory(at: Self.dataDirectory, withIntermediateDirectories: true)
            try JSONEncoder().encode(preferences).write(to: Self.settingsURL, options: .atomic)
        } catch { self.error = error.localizedDescription }
    }
    func start() {
        stop(); paused = false; busy = true; ready = false; error = nil; progress = 0; progressStage = ""
        startGemini()
        let generation = UUID(); self.generation = generation
        let executable = Bundle.main.bundleURL.appendingPathComponent("Contents/Helpers/CodexUsageWidget.Bridge")
        let p = Process(); p.executableURL = executable
        let stdin = Pipe(), stdout = Pipe()
        p.standardInput = stdin; p.standardOutput = stdout
        let logURL = Self.dataDirectory.appendingPathComponent("diagnostics.log")
        if let size = (try? logURL.resourceValues(forKeys:[.fileSizeKey]))?.fileSize, size > 1_048_576 {
            let backup = Self.dataDirectory.appendingPathComponent("diagnostics.previous.log")
            try? FileManager.default.removeItem(at:backup)
            try? FileManager.default.moveItem(at:logURL,to:backup)
        }
        if !FileManager.default.fileExists(atPath:logURL.path) { FileManager.default.createFile(atPath:logURL.path,contents:nil) }
        let log = try? FileHandle(forWritingTo:logURL); _ = try? log?.seekToEnd()
        p.standardError = log ?? FileHandle.nullDevice
        input = stdin.fileHandleForWriting; output = stdout.fileHandleForReading
        let reader = BridgeLineReader()
        stdout.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let data = handle.availableData
            guard !data.isEmpty else { handle.readabilityHandler = nil; return }
            self?.readQueue.async { [weak self] in
                guard let self else { return }
                for line in reader.consume(data) {
                    DispatchQueue.main.async { [weak self] in
                        guard self?.generation == generation else { return }
                        self?.receive(line)
                    }
                }
            }
        }
        p.terminationHandler = { [weak self] p in
            DispatchQueue.main.async {
                guard let self, self.generation == generation else { return }
                self.ready = false; self.busy = false
                if !self.paused { self.error = self.t("统计服务已停止，请点刷新重试。", "Statistics service stopped. Refresh to retry.") }
            }
        }
        do {
            try p.run(); process = p
            var initCommand: [String:Any] = ["command":"init", "dataDirectory":Self.dataDirectory.path, "period":preferences.period]
            if !preferences.home.isEmpty { initCommand["home"] = (preferences.home as NSString).expandingTildeInPath }
            send(initCommand)
        } catch { self.error = error.localizedDescription; busy = false }
    }
    func startGemini() {
        guard !paused else { return }
        geminiBusy = true; geminiError = nil
        geminiMonitor.start(home: preferences.geminiHomePath, period: preferences.period)
        fetchGeminiQuota(force: true)
    }
    func fetchGeminiQuota(force: Bool) {
        guard !paused else { return }
        geminiQuotaFetcher.fetch(force: force) { [weak self] quota in
            guard let self, !self.paused else { return }
            if let quota {
                self.geminiQuota = quota
                self.quotaChanged?()
            }
        }
    }
    func stop() {
        generation = UUID(); pendingCommand = nil; ready = false
        output?.readabilityHandler = nil; output = nil
        try? input?.close(); input = nil
        if let p = process, p.isRunning { p.terminate() }
        process = nil
        geminiMonitor.stop()
        geminiQuotaFetcher.cancel()
    }
    func pause() { paused = true; stop(); busy = false; geminiBusy = false }
    func refresh(_ command: String = "refresh") {
        if paused { return }
        if isGemini {
            geminiBusy = true; geminiError = nil
            geminiMonitor.refresh(home: preferences.geminiHomePath, period: preferences.period, resetCache: command == "rebuild")
            fetchGeminiQuota(force: true)
            return
        }
        if process?.isRunning != true || (!ready && !busy) { start(); return }
        let request: [String:Any] = ["command":command, "period":preferences.period]
        if busy || !ready { pendingCommand = request; return }
        busy = true; progress = 0; progressStage = ""; error = nil; send(request)
    }
    func selectPeriod(_ p: Int) {
        preferences.period = p; showingWeekly = false; refresh()
    }
    func selectProvider(_ provider: UsageProvider) {
        guard preferences.activeProvider != provider else { return }
        preferences.activeProvider = provider
        showingWeekly = false
        quotaChanged?()
        if !paused { refresh() }
    }
    private func send(_ value: [String:Any]) {
        guard var data = try? JSONSerialization.data(withJSONObject:value) else { return }
        data.append(10)
        do { try input?.write(contentsOf:data) } catch { self.error = error.localizedDescription; busy = false }
    }
    private func receive(_ line: Data) {
        do {
            guard let envelope = try JSONSerialization.jsonObject(with:line) as? [String:Any], let type = envelope["type"] as? String else { return }
            let raw = envelope["data"] ?? NSNull()
            let data = try JSONSerialization.data(withJSONObject:raw, options:.fragmentsAllowed)
            let decoder = JSONDecoder()
            switch type {
            case "ready":
                sourceHome = (raw as? [String:Any])?["home"] as? String ?? ""
                ready = true
            case "quota":
                quota = try decoder.decode(Quota?.self, from:data); quotaChanged?()
            case "snapshot":
                let result = try decoder.decode(Snapshot.self, from:data)
                if result.period == preferences.period { snapshot = result; updatedAt = Date() }
                busy = false
                if let request = pendingCommand { pendingCommand = nil; busy = true; send(request) }
                else if result.period != preferences.period { refresh() }
            case "weekly": weekly = try decoder.decode([WeeklyDay].self, from:data)
            case "progress":
                progress = (raw as? [String:Any])?["progress"] as? Double ?? 0
                progressStage = (raw as? [String:Any])?["stage"] as? String ?? ""
            case "error":
                error = (raw as? [String:Any])?["message"] as? String ?? "Unknown error"; busy = false; pendingCommand = nil
            default: break
            }
        } catch { self.error = t("无法读取统计结果：", "Cannot read statistics: ") + error.localizedDescription; busy = false }
    }
}
