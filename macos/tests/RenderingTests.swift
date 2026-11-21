import AppKit
import SwiftUI
@main struct RenderingTests {
    static func main() throws {
        let app = NSApplication.shared; app.setActivationPolicy(.prohibited)
        let output = URL(fileURLWithPath:CommandLine.arguments[1]); try FileManager.default.createDirectory(at:output,withIntermediateDirectories:true)
        let model = Model(persistSettings:false)
        model.quota = Quota(timestamp:"2026-10-29T14:00:00Z",limitId:"codex",planType:"pro",primary:QuotaWindow(usedPercent:53,windowMinutes:10080,resetsAt:"2026-11-04T06:37:44Z"))
        let token = Tokens(inputTokens:4200000,cachedInputTokens:3500000,outputTokens:12000,reasoningOutputTokens:4000,totalTokens:4212000)
        let tasks = (1...9).map { TaskUsage(rootTaskId:String($0),title:"示例任务 \($0) · Migration verification",usage:token,percentOfPeriod:10,isArchived:false) }
        let totals = Tokens(inputTokens:42000000,cachedInputTokens:35000000,outputTokens:120000,reasoningOutputTokens:40000,totalTokens:42120000)
        model.snapshot = Snapshot(period:0,topTasks:tasks,summary:Summary(total:totals,taskCount:10,otherTasksTotal:token),lastUpdated:"2026-10-29T14:00:00Z")
        var days: [WeeklyDay] = []
        for day in 23...29 {
            let consumption: Double? = day == 25 ? nil : Double(day-20)*4
            days.append(WeeklyDay(localDate:"2026-10-\(day)",consumedPercentagePoints:consumption,changeFromPreviousDayPercentagePoints:4,lastObservedUsedPercent:53,observationCount:5,isPartial:day==29))
        }
        model.weekly = days
        for theme in ["light","dark"] {
            for kind in ["circle","glow","capsule","panel","weekly","weekly-high"] {
                for scale in ((kind == "panel" || kind.hasPrefix("weekly")) ? [1.0] : [0.5,0.75,1.0]) {
                model.preferences.idleScale = scale
                model.preferences.language = theme == "light" ? "zh" : "en"
                model.preferences.idleStyle = (kind == "panel" || kind.hasPrefix("weekly")) ? "circle" : kind
                model.expanded = (kind == "panel" || kind.hasPrefix("weekly")); model.showingWeekly = kind.hasPrefix("weekly")
                model.weekly = days
                if kind == "weekly-high" {
                    model.weekly[0].consumedPercentagePoints = 0
                    model.weekly[3].consumedPercentagePoints = 160
                    model.weekly[4].consumedPercentagePoints = 158
                    model.weekly[5].consumedPercentagePoints = 165
                    model.weekly[6].consumedPercentagePoints = 162
                }
                let size = model.expanded ? NSSize(width:420,height:540) : WidgetGeometry.idleSize(kind,scale:scale)
                let window = NSWindow(contentRect:NSRect(origin:.zero,size:size),styleMask:.borderless,backing:.buffered,defer:false)
                window.appearance = NSAppearance(named:theme == "dark" ? .darkAqua : .aqua)
                let view = NSHostingView(rootView:WidgetView(model:model,settings:{},hover:{_ in},startDrag:{_ in},collapse:{}))
                view.sizingOptions = []; window.contentView = view; view.frame = NSRect(origin:.zero,size:size)
                view.layoutSubtreeIfNeeded(); RunLoop.current.run(until:Date().addingTimeInterval(0.08))
                assert(window.frame.size == size)
                guard let bitmap = view.bitmapImageRepForCachingDisplay(in:view.bounds) else { fatalError("No bitmap") }
                view.cacheDisplay(in:view.bounds,to:bitmap)
                guard let png = bitmap.representation(using:.png,properties:[:]) else { fatalError("No PNG") }
                try png.write(to:output.appendingPathComponent("\(theme)-\(kind)-\(Int(scale*100)).png"))
                window.contentView = nil
                }
            }
        }
        print("PASS: 24 native renders at 50%, 75% and 100%, 3 idle styles, fixed 420x540 panel and weekly chart, light/dark, Chinese/English; user settings untouched")

        // Additional Gemini mode renders (dark Chinese + light English) with official agy percentage quota.
        model.preferences.activeProvider = .gemini
        model.geminiQuota = Quota(
            timestamp:"2026-11-21T09:47:35Z",
            limitId:"gemini",
            planType:"antigravity",
            primary:QuotaWindow(usedPercent:3.5,windowMinutes:300,resetsAt:"2026-11-21T13:52:34Z"),
            secondary:QuotaWindow(usedPercent:1.0,windowMinutes:10080,resetsAt:"2026-11-28T08:52:34Z")
        )
        model.geminiTodayTokens = 13_420_000
        model.geminiRecent5hTokens = 4_850_000
        model.geminiSnapshot = Snapshot(period:0,topTasks:tasks,summary:Summary(total:totals,taskCount:10,otherTasksTotal:token),lastUpdated:"2026-11-20T14:00:00Z")
        model.geminiWeeklyTokens = [
            WeeklyTokenDay(localDate:"2026-11-14",totalTokens:nil,eventCount:0),
            WeeklyTokenDay(localDate:"2026-11-15",totalTokens:3_200_000,eventCount:4),
            WeeklyTokenDay(localDate:"2026-11-16",totalTokens:8_900_000,eventCount:9),
            WeeklyTokenDay(localDate:"2026-11-17",totalTokens:5_400_000,eventCount:6),
            WeeklyTokenDay(localDate:"2026-11-18",totalTokens:nil,eventCount:0),
            WeeklyTokenDay(localDate:"2026-11-19",totalTokens:11_200_000,eventCount:12),
            WeeklyTokenDay(localDate:"2026-11-20",totalTokens:13_420_000,eventCount:15)
        ]
        for (theme, lang) in [("dark","zh"),("light","en")] {
            for kind in ["circle","capsule","panel","weekly"] {
                model.preferences.idleScale = 1.0
                model.preferences.language = lang
                model.preferences.idleStyle = (kind == "panel" || kind == "weekly") ? "circle" : kind
                model.expanded = (kind == "panel" || kind == "weekly")
                model.showingWeekly = kind == "weekly"
                let size = model.expanded ? NSSize(width:420,height:540) : WidgetGeometry.idleSize(kind,scale:1.0)
                let window = NSWindow(contentRect:NSRect(origin:.zero,size:size),styleMask:.borderless,backing:.buffered,defer:false)
                window.appearance = NSAppearance(named:theme == "dark" ? .darkAqua : .aqua)
                let view = NSHostingView(rootView:WidgetView(model:model,settings:{},hover:{_ in},startDrag:{_ in},collapse:{}))
                view.sizingOptions = []; window.contentView = view; view.frame = NSRect(origin:.zero,size:size)
                view.layoutSubtreeIfNeeded(); RunLoop.current.run(until:Date().addingTimeInterval(0.08))
                guard let bitmap = view.bitmapImageRepForCachingDisplay(in:view.bounds) else { fatalError("No bitmap") }
                view.cacheDisplay(in:view.bounds,to:bitmap)
                guard let png = bitmap.representation(using:.png,properties:[:]) else { fatalError("No PNG") }
                try png.write(to:output.appendingPathComponent("gemini-\(theme)-\(kind).png"))
                window.contentView = nil
            }
        }
        print("PASS: 8 Gemini native renders (dark-zh & light-en, circle/capsule/panel/weekly)")

        // Dual AI overview renders + overview drill-down render.
        model.quota = Quota(
            timestamp:"2026-11-21T09:47:35Z",
            limitId:"codex",
            planType:"pro",
            primary:QuotaWindow(usedPercent:53,windowMinutes:300,resetsAt:"2026-11-21T12:10:00Z"),
            secondary:QuotaWindow(usedPercent:28,windowMinutes:10080,resetsAt:"2026-11-26T06:00:00Z")
        )
        model.preferences.layoutMode = .overview
        model.expanded = true
        model.showingWeekly = false
        for (theme, lang, showingOverview, name) in [
            ("dark","zh",true,"overview-dark"),
            ("light","en",true,"overview-light"),
            ("dark","zh",false,"overview-detail-dark")
        ] {
            model.preferences.language = lang
            model.showingOverview = showingOverview
            let size = NSSize(width:420,height:540)
            let window = NSWindow(contentRect:NSRect(origin:.zero,size:size),styleMask:.borderless,backing:.buffered,defer:false)
            window.appearance = NSAppearance(named:theme == "dark" ? .darkAqua : .aqua)
            let view = NSHostingView(rootView:WidgetView(model:model,settings:{},hover:{_ in},startDrag:{_ in},collapse:{}))
            view.sizingOptions = []; window.contentView = view; view.frame = NSRect(origin:.zero,size:size)
            view.layoutSubtreeIfNeeded(); RunLoop.current.run(until:Date().addingTimeInterval(0.08))
            guard let bitmap = view.bitmapImageRepForCachingDisplay(in:view.bounds) else { fatalError("No bitmap") }
            view.cacheDisplay(in:view.bounds,to:bitmap)
            guard let png = bitmap.representation(using:.png,properties:[:]) else { fatalError("No PNG") }
            try png.write(to:output.appendingPathComponent("\(name).png"))
            window.contentView = nil
        }
        print("PASS: 3 dual-AI overview & drill-down renders")
    }
}
