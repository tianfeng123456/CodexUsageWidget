import Foundation
import SQLite3
@main struct ModelTests {
    static func main() throws {
        assert(formatTokens(9876,chinese:true) == "9,876")
        assert(formatTokens(9876,chinese:false) == "9.88K")
        assert(formatTokens(602820000,chinese:true) == "6.03亿")
        assert(formatTokens(602820000,chinese:false) == "602.82M")
        assert(formatTokens(1000000000000,chinese:true) == "1万亿")
        assert(parsedDate("2026-10-29T14:12:30.1234567+00:00") != nil)
        assert(parsedDate("2026-10-29T14:12:30Z") != nil)
        let data = """
        {"timestamp":"2026-10-29T14:12:30Z","limitId":"codex","primary":{"usedPercent":25,"windowMinutes":10080,"resetsAt":null},"secondary":{"usedPercent":80,"windowMinutes":300,"resetsAt":null}}
        """.data(using:.utf8)!
        let quota = try JSONDecoder().decode(Quota.self,from:data)
        assert(quota.remaining == 20)
        assert(quota.weekly?.usedPercent == 25)
        assert(QuotaWindow(usedPercent:120).remainingPercent == 0)
        assert(QuotaWindow(usedPercent:-20).remainingPercent == 100)
        assert(WidgetGeometry.glassOpacity(0) == 1)
        assert(WidgetGeometry.glassOpacity(0.5) == 0.5)
        assert(WidgetGeometry.glassOpacity(1) == 0.02)
        assert(WidgetGeometry.glassOpacity(-1) == 1)
        assert(WidgetGeometry.glassOpacity(.nan) == 0.5)
        let prefs = Preferences()
        let roundtrip = try JSONDecoder().decode(Preferences.self,from:JSONEncoder().encode(prefs))
        assert(roundtrip == prefs)
        var legacyObject = try JSONSerialization.jsonObject(with:JSONEncoder().encode(prefs)) as! [String:Any]
        legacyObject.removeValue(forKey:"iconScale")
        legacyObject["x"] = 21.4375; legacyObject["y"] = 670.70703125
        let legacy = try JSONDecoder().decode(Preferences.self,from:JSONSerialization.data(withJSONObject:legacyObject))
        assert(legacy.idleScale == 1 && legacy.x == 21.4375 && legacy.y == 670.70703125)
        var scaled = prefs; scaled.idleScale = 0.65
        let restoredScale = try JSONDecoder().decode(Preferences.self,from:JSONEncoder().encode(scaled))
        assert(restoredScale.idleScale == 0.65)
        scaled.idleScale = -10; assert(scaled.idleScale == 0.5)
        scaled.idleScale = 10; assert(scaled.idleScale == 1.25)
        for style in ["circle","glow","capsule"] {
            for scale in [0.5,0.65,1.0,1.25] {
                let base = WidgetGeometry.idleSize(style)
                let size = WidgetGeometry.idleSize(style,scale:scale)
                assert(size == CGSize(width:base.width*scale,height:base.height*scale))
                var anchor = CGPoint(x:1430,y:890)
                let bounds = CGRect(x:0,y:0,width:1440,height:900)
                let idle = WidgetGeometry.frame(anchor:&anchor,style:style,expanded:false,bounds:bounds,scale:scale)
                let expanded = WidgetGeometry.frame(anchor:&anchor,style:style,expanded:true,bounds:bounds,scale:scale)
                assert(bounds.contains(idle) && bounds.contains(expanded))
                assert(expanded.size == CGSize(width:420,height:540))
                assert(WidgetGeometry.frame(anchor:&anchor,style:style,expanded:false,bounds:bounds,scale:scale) == idle)
            }
            for point in [CGPoint(x:0,y:0),CGPoint(x:1400,y:850),CGPoint(x:-2000,y:40)] {
                var anchor = point
                let bounds = CGRect(x:0,y:0,width:1440,height:900)
                let idle = WidgetGeometry.frame(anchor:&anchor,style:style,expanded:false,bounds:bounds)
                let stableAnchor = anchor
                let expanded = WidgetGeometry.frame(anchor:&anchor,style:style,expanded:true,bounds:bounds)
                assert(bounds.contains(idle) && bounds.contains(expanded))
                assert(expanded.size == CGSize(width:420,height:540))
                assert(anchor == stableAnchor)
                assert(WidgetGeometry.frame(anchor:&anchor,style:style,expanded:false,bounds:bounds) == idle)
            }
        }
        assert(!WidgetGeometry.shouldPause(enabled:false,screenOff:true,locked:true,sleeping:true))
        assert(WidgetGeometry.shouldPause(enabled:true,screenOff:false,locked:true,sleeping:false))
        assert(WidgetGeometry.shouldPause(enabled:true,screenOff:true,locked:false,sleeping:false))
        assert(WidgetGeometry.shouldPause(enabled:true,screenOff:false,locked:false,sleeping:true))
        assert(!WidgetGeometry.shouldPause(enabled:true,screenOff:false,locked:false,sleeping:false))
        // Drag the actual panel without recomputing its expansion direction.
        // The right-edge collapsed anchor retains its offset while moving left.
        let originalFrame = CGRect(x:1000,y:300,width:420,height:540)
        let originalAnchor = CGPoint(x:1340,y:760)
        let moved = originalFrame.offsetBy(dx:-400,dy:-100)
        assert(WidgetGeometry.movedAnchor(originalAnchor,from:originalFrame,to:moved) == CGPoint(x:940,y:660))
        let monitor = CGRect(x:-1920,y:-200,width:1920,height:1080)
        let negative = WidgetGeometry.clamp(CGRect(x:-2100,y:-300,width:420,height:540),to:monitor)
        assert(monitor.contains(negative))
        assert(WidgetGeometry.movedAnchor(originalAnchor,from:originalFrame,to:originalFrame) == originalAnchor)
        let buttons = [CGRect(x:145,y:60,width:140,height:24),CGRect(x:320,y:20,width:80,height:24)]
        assert(WidgetGeometry.isDragPoint(CGPoint(x:300,y:90),expanded:true,controls:buttons))
        assert(!WidgetGeometry.isDragPoint(CGPoint(x:250,y:74),expanded:true,controls:buttons))
        assert(!WidgetGeometry.isDragPoint(CGPoint(x:335,y:32),expanded:true,controls:buttons))
        assert(!WidgetGeometry.isDragPoint(CGPoint(x:100,y:200),expanded:true,controls:buttons))
        assert(WidgetGeometry.isDragPoint(CGPoint(x:16,y:16),expanded:false,controls:buttons))
        let roundFrame = CGRect(x:0,y:0,width:420,height:540)
        assert(!WidgetGeometry.containsRounded(CGPoint(x:1,y:1),frame:roundFrame,radius:32))
        assert(WidgetGeometry.containsRounded(CGPoint(x:20,y:20),frame:roundFrame,radius:32))
        assert(!WidgetGeometry.containsRounded(CGPoint(x:421,y:200),frame:roundFrame,radius:32))
        var damaged = Preferences(); damaged.period = 99; damaged.transparency = -1; damaged.idleStyle = "bad"
        let fixed = damaged.normalized()
        assert(fixed.period == 3 && fixed.transparency == 0 && fixed.idleStyle == "circle")
        let model = Model(persistSettings:false); model.preferences.language = "en"
        for (remaining,label) in [(0.4,"Exhausted"),(0.5,"Almost exhausted"),(9.5,"Running low"),(29.5,"Usage steady"),(69.5,"Plenty remaining")] {
            model.quota = Quota(timestamp:"",primary:QuotaWindow(usedPercent:100-remaining))
            assert(model.statusText == label)
        }
        model.quota = nil; model.busy = true; assert(model.statusText == "Syncing")
        assert(model.progressText == "Refreshing…")
        model.progressStage = "Reading"; model.progress = 0.25; assert(model.progressText == "Indexing 25%")
        model.progress = 1; assert(model.progressText == "Summarizing…")

        // Gemini local usage scanner tests (isolated temporary directory).
        assert(formatCompactTokens(0, chinese: false) == "0")
        assert(formatCompactTokens(13_400_000, chinese: false) == "13.4M")
        assert(formatCompactTokens(13_400_000, chinese: true) == "1340万")
        assert(formatCompactTokens(134_000_000, chinese: true) == "1.34亿")
        let tempRoot = FileManager.default.temporaryDirectory.appendingPathComponent("gemini-usage-test-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: tempRoot) }
        let brain = tempRoot.appendingPathComponent("brain")
        let rootConv = brain.appendingPathComponent("conv-root/.system_generated/logs")
        let childConv = brain.appendingPathComponent("conv-child/.system_generated/logs")
        try FileManager.default.createDirectory(at: rootConv, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: childConv, withIntermediateDirectories: true)
        let dbPath = tempRoot.appendingPathComponent("conversation_summaries.db").path
        var db: OpaquePointer?
        assert(sqlite3_open(dbPath, &db) == SQLITE_OK)
        let schema = """
        CREATE TABLE conversation_summaries (conversation_id text PRIMARY KEY, title text NOT NULL DEFAULT '', parent_conversation_id text NOT NULL DEFAULT '');
        INSERT INTO conversation_summaries VALUES ('conv-root', '主任务标题', '');
        INSERT INTO conversation_summaries VALUES ('conv-child', '', 'conv-root');
        """
        assert(sqlite3_exec(db, schema, nil, nil, nil) == SQLITE_OK)
        sqlite3_close(db)
        let now = parsedDate("2026-11-20T14:00:00Z")!
        let rootJsonl = """
        {"type":"USER_INPUT","created_at":"2026-11-20T10:00:00Z","content":"<USER_REQUEST>\\nHello\\n</USER_REQUEST>"}
        {"type":"PLANNER_RESPONSE","created_at":"2026-11-20T12:00:00Z","model":"gemini-4-neon-medium","input_tokens":1000,"cache_read_tokens":5000,"output_tokens":200}
        {"type":"PLANNER_RESPONSE","created_at":"2026-11-18T12:00:00Z","model":"gemini-4-neon-medium","input_tokens":3000,"cache_read_tokens":0,"output_tokens":500}
        """
        let childJsonl = """
        {"type":"PLANNER_RESPONSE","created_at":"2026-11-20T13:00:00Z","model":"gemini-4-carbon-high","input_tokens":500,"cache_read_tokens":1500,"output_tokens":300}
        """
        try rootJsonl.write(to: rootConv.appendingPathComponent("transcript.jsonl"), atomically: true, encoding: .utf8)
        try childJsonl.write(to: childConv.appendingPathComponent("transcript.jsonl"), atomically: true, encoding: .utf8)
        var utcCal = Calendar(identifier: .gregorian); utcCal.timeZone = TimeZone(secondsFromGMT: 0)!
        let scanner = GeminiUsageScanner(calendar: utcCal)
        let todayRes = scanner.scan(home: tempRoot.path, period: 0, now: now)
        assert(todayRes.todayTokens == 8500) // (1000+5000+200) + (500+1500+300)
        assert(todayRes.recent5hTokens == 8500)
        assert(todayRes.snapshot.summary.taskCount == 1) // child merged into root
        assert(todayRes.snapshot.topTasks.first?.title == "主任务标题")
        assert(todayRes.snapshot.summary.total.cachedInputTokens == 6500)
        assert(todayRes.snapshot.summary.total.outputTokens == 500)
        assert(todayRes.taskModels["conv-root"] == ["gemini-4-carbon-high", "gemini-4-neon-medium"])
        assert(todayRes.weeklyTokens.count == 7)
        assert(todayRes.weeklyTokens.last?.totalTokens == 8500)
        let weekRes = scanner.scan(home: tempRoot.path, period: 1, now: now)
        assert(weekRes.snapshot.summary.total.totalTokens == 12000) // 8500 + 3500
        model.preferences.activeProvider = .gemini
        model.geminiSnapshot = todayRes.snapshot
        model.geminiTodayTokens = todayRes.todayTokens
        model.geminiRecent5hTokens = todayRes.recent5hTokens
        assert(model.isGemini && model.remainingText == "8.50K" && model.menuBarTitle == "G 8.50K")

        // Official agy CLI JSON quota parsing tests.
        let agyJSON = """
        {"status":"SUCCESS","command":{"name":"usage","data":{"groups":[
          {"name":"Gemini Models","buckets":[
            {"id":"gemini-weekly","window":"weekly","remaining_fraction":0.99,"reset_time":"2026-11-28T08:52:34Z"},
            {"id":"gemini-5h","window":"5h","remaining_fraction":0.9648,"reset_time":"2026-11-21T13:52:34Z"}
          ]},
          {"name":"Claude and GPT models","buckets":[
            {"id":"3p-5h","window":"5h","remaining_fraction":0.5,"reset_time":"2026-11-21T14:00:00Z"}
          ]}
        ]}}}
        """.data(using: .utf8)!
        let parsedGeminiQuota = GeminiQuotaParser.parse(agyJSON, now: now)
        assert(parsedGeminiQuota != nil)
        assert(parsedGeminiQuota?.primary?.windowMinutes == 300)
        assert(parsedGeminiQuota?.fiveHour?.windowMinutes == 300)
        assert(parsedGeminiQuota?.weekly?.windowMinutes == 10080)
        assert(abs((parsedGeminiQuota?.remaining ?? 0) - 96.48) < 0.01)
        assert(abs((parsedGeminiQuota?.weekly?.usedPercent ?? 0) - 1.0) < 0.01)
        model.geminiQuota = parsedGeminiQuota
        assert(model.remainingText == "96%" && model.menuBarTitle == "G 96%" && model.statusText == "Plenty remaining")
        assert(GeminiQuotaParser.parse(Data("{\"status\":\"ERROR\"}".utf8)) == nil)

        // PanelLayoutMode roundtrip and backward compatibility.
        assert(Preferences().layoutMode == .focused)
        var overviewPrefs = Preferences(); overviewPrefs.layoutMode = .overview
        let decodedOverview = try JSONDecoder().decode(Preferences.self, from: JSONEncoder().encode(overviewPrefs))
        assert(decodedOverview.layoutMode == .overview)
        var badLayout = Preferences(); badLayout.panelLayout = "invalid"
        assert(badLayout.normalized().layoutMode == .focused)

        print("PASS: Chinese/English units, .NET timestamps, most constrained window, weekly selection, percentage clamping, settings roundtrip, 3-style edge geometry, anchor stability, native drag hit regions and offsets, negative-screen clamp, rounded hover, settings validation, status rounding, progress, sleep gate, Gemini scanner, subagent merge, official agy CLI quota parser & dual-layout preferences")
    }
}
