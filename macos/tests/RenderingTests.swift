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
            for kind in ["circle","glow","capsule","panel","weekly"] {
                model.preferences.language = theme == "light" ? "zh" : "en"
                model.preferences.idleStyle = ["panel","weekly"].contains(kind) ? "circle" : kind
                model.expanded = ["panel","weekly"].contains(kind); model.showingWeekly = kind == "weekly"
                let size = model.expanded ? NSSize(width:420,height:540) : WidgetGeometry.idleSize(kind)
                let window = NSWindow(contentRect:NSRect(origin:.zero,size:size),styleMask:.borderless,backing:.buffered,defer:false)
                window.appearance = NSAppearance(named:theme == "dark" ? .darkAqua : .aqua)
                let view = NSHostingView(rootView:WidgetView(model:model,settings:{},hover:{_ in},startDrag:{_ in},collapse:{}))
                view.sizingOptions = []; window.contentView = view; view.frame = NSRect(origin:.zero,size:size)
                view.layoutSubtreeIfNeeded(); RunLoop.current.run(until:Date().addingTimeInterval(0.08))
                assert(window.frame.size == size)
                guard let bitmap = view.bitmapImageRepForCachingDisplay(in:view.bounds) else { fatalError("No bitmap") }
                view.cacheDisplay(in:view.bounds,to:bitmap)
                guard let png = bitmap.representation(using:.png,properties:[:]) else { fatalError("No PNG") }
                try png.write(to:output.appendingPathComponent("\(theme)-\(kind).png"))
                window.contentView = nil
            }
        }
        print("PASS: 10 native renders, 3 idle styles, fixed 420x540 panel and weekly chart, light/dark, Chinese/English; user settings untouched")
    }
}
