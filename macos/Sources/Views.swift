import SwiftUI
import AppKit
import Charts
import ServiceManagement
import Darwin

struct Glass: NSViewRepresentable {
    var opacity: Double = 1
    func makeNSView(context: Context) -> NSVisualEffectView {
        let view = NSVisualEffectView(); view.material = .hudWindow
        view.blendingMode = .behindWindow; view.state = .active
        view.alphaValue = opacity
        return view
    }
    func updateNSView(_ view: NSVisualEffectView, context: Context) {
        view.alphaValue = opacity
    }
}
extension Model {
    var tint: Color {
        if isGemini && geminiQuota == nil {
            return Color(red:0.46,green:0.68,blue:0.99)
        }
        guard let n = displayedRemaining else { return .secondary }
        return n >= 30 ? Color(red:0.31,green:0.82,blue:0.66) : n >= 10 ? Color(red:1,green:0.80,blue:0.45) : n > 0 ? .orange : .red
    }
    var ringProgress: Double {
        if isGemini && geminiQuota == nil {
            return geminiTodayTokens > 0 ? 1.0 : 0.0
        }
        return (activeQuota?.remaining ?? 0) / 100
    }
}
// One continuous path: the heartbeat is part of the progress track, as on Windows.
struct HeartbeatRing: Shape {
    func path(in rect:CGRect) -> Path {
        let center = CGPoint(x:rect.midX,y:rect.midY), radius = min(rect.width,rect.height)/2
        var p = Path()
        func point(_ degrees:Double) -> CGPoint {
            let angle: Double = degrees * .pi / 180
            return CGPoint(x:center.x+radius*CGFloat(Darwin.cos(angle)),y:center.y+radius*CGFloat(Darwin.sin(angle)))
        }
        p.move(to:point(-90))
        for angle in stride(from:-89.0,through:76.0,by:1) { p.addLine(to:point(angle)) }
        let baseline = point(76).y
        if radius >= 12 {
            for (x,y) in [(5.0,0.0),(3,-2),(0.5,4),(-2,-3),(-4,0)] { p.addLine(to:CGPoint(x:center.x+x,y:baseline+y)) }
        }
        p.addLine(to:point(104))
        for angle in stride(from:105.0,through:270.0,by:1) { p.addLine(to:point(angle)) }
        return p
    }
}
struct Wave: Shape {
    func path(in r:CGRect) -> Path {
        var p = Path(); p.move(to:CGPoint(x:0,y:r.midY))
        p.addCurve(to:CGPoint(x:r.width*0.25,y:r.midY),control1:CGPoint(x:r.width*0.12,y:-r.height*0.25),control2:CGPoint(x:r.width*0.14,y:r.height*1.25))
        p.addCurve(to:CGPoint(x:r.width*0.6,y:r.midY),control1:CGPoint(x:r.width*0.4,y:-r.height*0.25),control2:CGPoint(x:r.width*0.42,y:r.height*1.25))
        p.addCurve(to:CGPoint(x:r.width,y:r.midY),control1:CGPoint(x:r.width*0.8,y:-r.height*0.15),control2:CGPoint(x:r.width*0.74,y:r.height*1.3))
        return p
    }
}
struct Ring: View {
    @ObservedObject var model: Model
    var small = false
    @Environment(\.colorScheme) var scheme
    var body: some View {
        ZStack {
            if small {
                Circle().stroke(model.tint.opacity(0.16),lineWidth:5)
                Circle().trim(from:0,to:model.ringProgress).stroke(model.tint,style:StrokeStyle(lineWidth:2,lineCap:.round)).rotationEffect(.degrees(-90))
            } else {
                HeartbeatRing().stroke(Color.primary.opacity(0.17),style:StrokeStyle(lineWidth:2.4,lineCap:.round,lineJoin:.round))
                HeartbeatRing().trim(from:0,to:model.ringProgress).stroke(model.tint,style:StrokeStyle(lineWidth:2.5,lineCap:.round,lineJoin:.round))
                Text(model.remainingText).font(.system(size:model.isGemini && model.geminiQuota == nil ? 17 : 21,weight:.semibold)).monospacedDigit().lineLimit(1).minimumScaleFactor(0.55).padding(.horizontal,8).offset(y:-1)
            }
        }.padding(small ? 5 : 7)
    }
}
// A real AppKit mouse surface gives borderless panels reliable native dragging.
// Interactive SwiftUI controls are holes in this surface, not competing gestures.
struct WindowDragSurface: NSViewRepresentable {
    var expanded: Bool
    var controls: [CGRect]
    var start: (NSEvent) -> Void
    final class Surface: NSView {
        var expanded = false
        var controls: [CGRect] = []
        var start: ((NSEvent) -> Void)?
        override var isFlipped: Bool { true }
        override func acceptsFirstMouse(for event:NSEvent?) -> Bool { true }
        override func hitTest(_ point:NSPoint) -> NSView? {
            let local = convert(point,from:superview)
            return bounds.contains(local) && WidgetGeometry.isDragPoint(local,expanded:expanded,controls:controls) ? self : nil
        }
        override func mouseDown(with event:NSEvent) { start?(event) }
        override func mouseDragged(with event:NSEvent) { start?(event) }
        override func mouseUp(with event:NSEvent) { start?(event) }
    }
    func makeNSView(context:Context) -> Surface { Surface() }
    func updateNSView(_ view:Surface,context:Context) { view.expanded = expanded; view.controls = controls; view.start = start }
}
struct HeaderControlsKey: PreferenceKey {
    static var defaultValue: [CGRect] = []
    static func reduce(value:inout [CGRect],nextValue:() -> [CGRect]) { value += nextValue() }
}
extension View {
    func headerControl() -> some View {
        background(GeometryReader { proxy in
            Color.clear.preference(key:HeaderControlsKey.self,value:[proxy.frame(in:.named("widget"))])
        })
    }
}
struct WidgetView: View {
    @ObservedObject var model: Model
    var settings: () -> Void
    var hover: (Bool) -> Void
    var startDrag: (NSEvent) -> Void
    var collapse: () -> Void
    @Environment(\.colorScheme) var scheme
    var dark: Bool { scheme == .dark }
    var accent: Color {
        if model.isGemini && model.geminiQuota == nil {
            return dark ? model.tint : Color(red:0.22,green:0.47,blue:0.88)
        }
        return (model.displayedRemaining ?? 0) >= 30 && !dark ? Color(red:0.08,green:0.65,blue:0.45) : model.tint
    }
    var surface: Color { dark ? Color(red:0.13,green:0.17,blue:0.16) : Color(red:0.85,green:0.88,blue:0.85) }
    var body: some View {
        ZStack {
            if model.expanded || model.preferences.idleStyle != "glow" {
                Glass(opacity:WidgetGeometry.glassOpacity(model.preferences.transparency))
                surface.opacity(1-model.preferences.transparency*0.88)
                LinearGradient(colors:[Color.white.opacity(dark ? 0.04 : 0.12),.clear,Color.black.opacity(0.035)],startPoint:.topLeading,endPoint:.bottomTrailing)
                    .opacity(WidgetGeometry.glassOpacity(model.preferences.transparency))
            }
            if model.expanded { panel }
            else if model.preferences.idleStyle == "capsule" {
                let compactGemini = model.isGemini && model.geminiQuota == nil
                HStack(spacing:compactGemini ? 7 : 10) {
                    Text(model.remainingText).font(.system(size:compactGemini ? 20 : 26,weight:.light)).monospacedDigit().lineLimit(1).minimumScaleFactor(0.65)
                    Wave().stroke(accent,style:StrokeStyle(lineWidth:1.4,lineCap:.round)).frame(width:compactGemini ? 24 : 34,height:15)
                    VStack(alignment:.leading,spacing:3) {
                        Text(compactGemini ? "Gemini" : model.t("剩余额度","Remaining")).foregroundStyle(.secondary)
                        Text(model.statusText).foregroundStyle(accent)
                    }.font(.system(size:10.5)).lineLimit(1).minimumScaleFactor(0.8)
                    Spacer(minLength:0)
                }.padding(.horizontal,compactGemini ? 13 : 15).contentShape(Rectangle()).onTapGesture { hover(true) }
            } else { Ring(model:model,small:model.preferences.idleStyle == "glow").accessibilityElement(children:.ignore).accessibilityLabel(model.isGemini ? model.t("Gemini 今日用量","Gemini today usage") : model.t("剩余额度","Remaining quota")).accessibilityValue(model.remainingText).accessibilityAddTraits(.isButton).accessibilityAction { hover(true) }.contentShape(Circle()).onTapGesture { hover(true) } }
        }
        .clipShape(RoundedRectangle(cornerRadius:model.expanded ? 32 : 100))
        .frame(width:model.expanded ? 420 : model.preferences.idleStyle == "capsule" ? 208 : model.preferences.idleStyle == "glow" ? 32 : 80,
               height:model.expanded ? 540 : model.preferences.idleStyle == "glow" ? 32 : 80)
        .scaleEffect(model.expanded ? 1 : model.preferences.idleScale)
        .frame(width:model.expanded ? 420 : WidgetGeometry.idleSize(model.preferences.idleStyle,scale:model.preferences.idleScale).width,
               height:model.expanded ? 540 : WidgetGeometry.idleSize(model.preferences.idleStyle,scale:model.preferences.idleScale).height)
        .coordinateSpace(name:"widget").onPreferenceChange(HeaderControlsKey.self) { if model.headerControls != $0 { model.headerControls = $0 } }
        .overlay(WindowDragSurface(expanded:model.expanded,controls:model.headerControls,start:startDrag))
        .onHover(perform:hover)
        .contextMenu {
            if model.expanded { Button(model.t("收起","Collapse"),action:collapse) }
            Button(model.isGemini ? model.t("切换到 Codex","Switch to Codex") : model.t("切换到 Gemini","Switch to Gemini")) {
                model.selectProvider(model.isGemini ? .codex : .gemini)
            }
            Button(model.t("立即刷新","Refresh now")) { model.refresh() }
            Button(model.t("设置…","Settings…"),action:settings)
            Button(model.t("退出","Quit")) { NSApp.terminate(nil) }
        }
    }
    var panel: some View {
        VStack(spacing:0) {
            header.frame(height:96,alignment:.top)
            if model.showingWeekly { weeklyPanel }
            else { tokensPanel }
            footer.padding(.top,6)
        }.padding(20).frame(width:420,height:540,alignment:.top)
    }
    var header: some View {
        HStack(alignment:.top,spacing:16) {
            Text(model.remainingText).font(.system(size:55,weight:.ultraLight)).monospacedDigit().tracking(-2).lineLimit(1).minimumScaleFactor(0.45).frame(width:112,alignment:.leading).padding(.top,4).contentShape(Rectangle())
            VStack(alignment:.leading,spacing:5) {
                Text(model.isGemini && model.geminiQuota == nil ? model.t("Gemini 今日","Gemini today") : model.t("剩余额度","Remaining")).font(.system(size:14,weight:.regular)).contentShape(Rectangle())
                Text(resetText).font(.system(size:10.5)).foregroundStyle(.secondary).lineLimit(1).contentShape(Rectangle())
                Button {
                    model.showingWeekly.toggle()
                    if model.showingWeekly { model.refresh("weekly") }
                } label: {
                    HStack(spacing:5) {
                        Circle().fill(accent).frame(width:7,height:7)
                        Text(weeklyTriggerLabel)
                            .font(.system(size:10.5)).foregroundStyle(.secondary).lineLimit(1)
                        sparkline.frame(width:48,height:14).allowsHitTesting(false).accessibilityHidden(true)
                    }.frame(minHeight:24).contentShape(Rectangle())
                }.buttonStyle(.plain).headerControl().accessibilityIdentifier("WeeklyQuotaTrendButton").help(model.isGemini && model.geminiQuota == nil ? model.t("查看七日 Token 消耗","View 7-day token usage") : model.t("查看七日周额度消耗","View daily weekly-quota usage"))
            }.padding(.top,2)
            Spacer(minLength:0)
        }.overlay(alignment:.topTrailing) {
            VStack(alignment:.trailing,spacing:10) {
                HStack(spacing:12) {
                    Button { model.pinned.toggle() } label: { Image(systemName:model.pinned ? "pin.fill" : "pin") }.foregroundStyle(model.pinned ? accent : .secondary).help(model.t("固定展开","Pin panel"))
                    Button { model.refresh(model.showingWeekly ? "weekly" : "refresh") } label: { Image(systemName:"arrow.clockwise") }.help(model.t("立即刷新","Refresh now")).disabled(model.activeBusy)
                    Button(action:settings) { Image(systemName:"gearshape") }.help(model.t("设置","Settings"))
                }.buttonStyle(.plain).font(.system(size:15,weight:.light)).foregroundStyle(.secondary).offset(y:1)
                providerSwitcher
            }.headerControl()
        }
    }
    var providerSwitcher: some View {
        HStack(spacing:2) {
            providerChip(.codex, label: "Codex")
            providerChip(.gemini, label: "Gemini")
        }
        .padding(2)
        .background(Capsule().fill(Color.primary.opacity(dark ? 0.08 : 0.06)))
    }
    func providerChip(_ provider: UsageProvider, label: String) -> some View {
        let selected = model.activeProvider == provider
        return Button { model.selectProvider(provider) } label: {
            Text(label)
                .font(.system(size:10,weight:selected ? .medium : .regular))
                .foregroundStyle(selected ? accent : .secondary)
                .padding(.horizontal,7).padding(.vertical,3)
                .background(Capsule().fill(selected ? accent.opacity(dark ? 0.2 : 0.16) : .clear))
                .contentShape(Capsule())
        }.buttonStyle(.plain).accessibilityIdentifier("ProviderSwitch_\(provider.rawValue)")
    }
    var weeklyTriggerLabel: String {
        if model.isGemini && model.geminiQuota == nil {
            return model.t("7日 ", "7d ") + model.compactNumber(model.geminiWeeklyTotal)
        }
        return model.t("周已用 ","Week ") + (model.activeQuota?.weekly.map { String(format:"%.0f%%",$0.usedPercent) } ?? "--")
    }
    var resetText: String {
        if model.paused { return model.statusText }
        if model.isGemini && model.geminiQuota == nil {
            return model.t("近5h ","5h ") + model.compactNumber(model.geminiRecent5hTokens) + model.t(" · 本地日志"," · Local")
        }
        let window = model.activeQuota?.windows.min { $0.remainingPercent < $1.remainingPercent }
        guard let raw = window?.resetsAt, let date = parsedDate(raw) else { return model.t("等待额度记录","Waiting for quota") }
        let hours = Int(max(0,date.timeIntervalSinceNow)/3600)
        if hours >= 24 { return model.chinese ? "\(hours/24)天\(hours%24)小时后重置" : "Resets in \(hours/24)d \(hours%24)h" }
        return model.chinese ? "\(hours)小时后重置" : "Resets in \(hours)h"
    }
    var sparkline: some View {
        GeometryReader { g in
            let values: [Double?] = (model.isGemini && model.geminiQuota == nil)
                ? model.geminiWeeklyTokens.map { $0.totalTokens.map(Double.init) }
                : model.weekly.map { $0.consumedPercentagePoints }
            let maximum: Double = max(1,values.compactMap { $0 }.max() ?? 1)
            Path { path in
                var connected = false
                for i in 0..<7 {
                    if values.count == 7, let value = values[i] {
                        let point = CGPoint(x:CGFloat(i)*g.size.width/CGFloat(6),y:g.size.height-2-CGFloat(value/maximum)*(g.size.height-4))
                        if connected { path.addLine(to:point) } else { path.move(to:point) }; connected = true
                    } else { connected = false }
                }
                if values.isEmpty { path.move(to:CGPoint(x:0,y:g.size.height/2));path.addLine(to:CGPoint(x:g.size.width,y:g.size.height/2)) }
            }.stroke(Color.secondary.opacity(0.5),lineWidth:0.8)
            ForEach(0..<7,id:\.self) { i in
                Circle().fill(values.count == 7 && values[i] != nil ? accent.opacity(0.65) : Color.secondary.opacity(0.25)).frame(width:3,height:3)
                    .position(x:CGFloat(i)*g.size.width/CGFloat(6),y:values.count == 7 ? g.size.height-2-CGFloat((values[i] ?? 0)/maximum)*(g.size.height-4) : g.size.height/2)
            }
        }
    }
    var tokensPanel: some View {
        VStack(spacing:0) {
            HStack(spacing:0) {
                ForEach(0..<4,id:\.self) { period in
                    Button { model.selectPeriod(period) } label: {
                        VStack(spacing:8) {
                            Text(model.chinese ? ["今日","近7日","本月","累计"][period] : ["Today","7 days","Month","All"][period])
                                .font(.system(size:14,weight:.regular)).foregroundStyle(model.preferences.period == period ? Color.primary : .secondary)
                            Capsule().fill(model.preferences.period == period ? accent : .clear).frame(width:18,height:2)
                        }.frame(maxWidth:.infinity)
                    }.buttonStyle(.plain)
                }
            }.frame(height:36)
            HStack {
                Text(model.chinese ? ["今日任务消耗","近7日任务消耗","本月任务消耗","累计任务消耗"][model.preferences.period] : "Task consumption")
                    .font(.system(size:12.5)).foregroundStyle(.secondary)
                Spacer()
            }.frame(height:26,alignment:.top)
            if let s = model.activeSnapshot, s.period == model.preferences.period {
                ScrollView {
                    VStack(spacing:0) {
                        ForEach(Array(s.topTasks.enumerated()),id:\.element.id) { i, task in
                            taskRow(rank:i+1,title:task.title,usage:task.usage,percent:task.percentOfPeriod,archived:task.isArchived)
                                .help(model.taskDetails(task))
                        }
                        if s.summary.taskCount > s.topTasks.count {
                            taskRow(rank:10,title:model.t("其他","Other"),usage:s.summary.otherTasksTotal,percent:s.summary.total.totalTokens>0 ? Double(s.summary.otherTasksTotal.totalTokens)/Double(s.summary.total.totalTokens)*100 : 0)
                        }
                    }
                }.scrollIndicators(.hidden).frame(maxHeight:.infinity,alignment:.top)
                if s.topTasks.isEmpty { Text(model.t("此时间段暂无本地用量","No local usage in this period")).font(.caption).foregroundStyle(.secondary) }
                summary(s.summary)
            } else {
                Text(model.activeBusy ? model.t("正在读取本地日志…","Reading local logs…") : model.t("点击刷新读取用量","Refresh to load usage"))
                    .font(.system(size:12)).foregroundStyle(.secondary).frame(maxWidth:.infinity,maxHeight:.infinity)
            }
        }.frame(maxHeight:.infinity)
    }
    func taskRow(rank:Int,title:String,usage:Tokens,percent:Double,archived:Bool = false) -> some View {
        HStack(alignment:.top,spacing:10) {
            Text(String(format:"%02d",rank)).font(.system(size:10,weight:.light)).foregroundStyle(.secondary.opacity(0.8)).frame(width:14,alignment:.leading)
            VStack(spacing:4) {
                HStack(spacing:6) {
                    if archived { Image(systemName:"archivebox").font(.system(size:9)).foregroundStyle(.secondary) }
                    Text(title).font(.system(size:11.5)).lineLimit(1).truncationMode(.middle)
                    Spacer(minLength:0)
                    Text(model.number(usage.totalTokens)).font(.system(size:11.5)).monospacedDigit()
                    Text(String(format:"%.1f%%",percent)).font(.system(size:9,weight:.light)).foregroundStyle(.secondary).monospacedDigit().frame(width:38,alignment:.trailing)
                }
                GeometryReader { g in
                    let width = g.size.width*min(1,max(0,percent/100))
                    Capsule().fill(Color.primary.opacity(dark ? 0.18 : 0.10))
                    Capsule().fill(dark ? Color.white.opacity(0.6) : Color.white.opacity(0.65)).frame(width:max(2,width))
                    if width>2 { Capsule().fill(accent).frame(width:min(4,width)).offset(x:max(0,width-4)) }
                }.frame(height:1.7)
            }
        }.frame(height:24,alignment:.top)
    }
    func summary(_ s:Summary) -> some View {
        VStack(spacing:10) {
            Rectangle().fill(Color.primary.opacity(0.15)).frame(height:0.5)
            HStack(alignment:.top) {
                summaryCell(model.t("总计","Total"),model.number(s.total.totalTokens),.leading)
                Spacer(minLength:3)
                summaryCell(model.t("输入","Input"),model.number(s.total.inputTokens),.center)
                Spacer(minLength:3)
                summaryCell(model.t("输出","Output"),model.number(s.total.outputTokens),.center)
                Spacer(minLength:3)
                summaryCell(model.t("任务","Tasks"),String(s.taskCount),.trailing)
            }
            HStack(spacing:18) {
                Text(model.t("缓存输入  ","Cached  ") + model.number(s.total.cachedInputTokens))
                if model.isGemini {
                    Text(model.t("本地只读 · 含子代理归并","Local logs · Subagents merged"))
                } else {
                    Text(model.t("推理输出  ","Reasoning  ") + model.number(s.total.reasoningOutputTokens))
                }
            }.font(.system(size:9.5,weight:.light)).foregroundStyle(.secondary)
        }.frame(height:74,alignment:.top)
    }
    func summaryCell(_ title:String,_ value:String,_ alignment:HorizontalAlignment) -> some View {
        VStack(alignment:alignment,spacing:4) {
            Text(title).font(.system(size:10,weight:.light)).foregroundStyle(.secondary)
            Text(value).font(.system(size:17,weight:.light)).monospacedDigit().lineLimit(1).minimumScaleFactor(0.8)
        }
    }
    var weeklyPanel: some View {
        VStack(alignment:.leading,spacing:14) {
            HStack {
                Button { model.showingWeekly = false } label: { Image(systemName:"chevron.left").frame(width:24,height:24).contentShape(Rectangle()) }.buttonStyle(.plain).accessibilityIdentifier("WeeklyQuotaCloseButton")
                Text(model.isGemini && model.geminiQuota == nil ? model.t("七日 Token 消耗","7-day token usage") : model.t("七日周额度消耗","Daily weekly-quota usage")).font(.system(size:14,weight:.regular))
                Spacer()
            }
            if model.isGemini && model.geminiQuota == nil {
                let maxTokens: Double = max(1000, Double(model.geminiWeeklyTokens.compactMap(\.totalTokens).max() ?? 0) * 1.18)
                Chart(Array(model.geminiWeeklyTokens.enumerated()),id:\.element.id) { i,day in
                    if let tokens = day.totalTokens {
                        let value = Double(tokens)
                        BarMark(x:.value("Date",String(day.localDate.suffix(5))),y:.value("Tokens",value)).foregroundStyle(accent.opacity(0.18)).cornerRadius(3)
                        LineMark(x:.value("Date",String(day.localDate.suffix(5))),y:.value("Tokens",value),series:.value("Segment",String(model.geminiWeeklyTokens.prefix(i+1).filter { $0.totalTokens == nil }.count))).foregroundStyle(accent).lineStyle(StrokeStyle(lineWidth:1.4))
                        PointMark(x:.value("Date",String(day.localDate.suffix(5))),y:.value("Tokens",value)).foregroundStyle(accent).symbolSize(14)
                            .annotation(position:.top,spacing:6) {
                                Text(model.compactNumber(tokens))
                                    .font(.system(size:9.5,weight:.medium)).monospacedDigit().foregroundStyle(Color.primary.opacity(0.85)).fixedSize()
                            }
                            .accessibilityLabel(day.localDate)
                            .accessibilityValue(model.compactNumber(tokens))
                    }
                }.chartXScale(domain:model.geminiWeeklyTokens.map { String($0.localDate.suffix(5)) },range:.plotDimension(padding:24))
                    .chartYScale(domain:0...maxTokens)
                    .chartYAxis {
                        AxisMarks { value in
                            AxisGridLine()
                            AxisValueLabel {
                                if let n = value.as(Double.self) {
                                    Text(model.compactNumber(Int64(n))).font(.system(size:9))
                                }
                            }
                        }
                    }
                    .frame(height:290)
                Text(model.t("仅统计本地 Antigravity 日志，无记录的日期留空；官方剩余百分比暂未开放外部接口。","Counts local Antigravity logs only; missing dates stay blank. Official remaining quota API is not publicly exposed."))
                    .font(.system(size:9)).foregroundStyle(.secondary).fixedSize(horizontal:false,vertical:true)
            } else {
                Chart(Array(model.weekly.enumerated()),id:\.element.id) { i,day in
                    if let value = day.consumedPercentagePoints {
                        BarMark(x:.value("Date",String(day.localDate.suffix(5))),y:.value("Usage",value)).foregroundStyle(accent.opacity(0.18)).cornerRadius(3)
                        LineMark(x:.value("Date",String(day.localDate.suffix(5))),y:.value("Usage",value),series:.value("Segment",String(model.weekly.prefix(i+1).filter { $0.consumedPercentagePoints == nil }.count))).foregroundStyle(accent).lineStyle(StrokeStyle(lineWidth:1.4))
                        PointMark(x:.value("Date",String(day.localDate.suffix(5))),y:.value("Usage",value)).foregroundStyle(accent).symbolSize(14)
                            .annotation(position:.top,spacing:6) {
                                Text((day.isPartial ? "≥ " : "") + String(format:"%.1f%%",value))
                                    .font(.system(size:9.5,weight:.medium)).monospacedDigit().foregroundStyle(Color.primary.opacity(0.85)).fixedSize()
                            }
                            .accessibilityLabel(day.localDate)
                            .accessibilityValue((day.isPartial ? "≥ " : "") + String(format:"%.1f%%",value))
                    }
                }.chartXScale(domain:model.weekly.map { String($0.localDate.suffix(5)) },range:.plotDimension(padding:24))
                    .chartYScale(domain:0...max(100,(model.weekly.compactMap(\.consumedPercentagePoints).max() ?? 0)*1.15)).frame(height:290)
                Text(model.t("≥ 为可观测部分，无记录的日期留空。","≥ indicates partial observations; missing dates stay blank."))
                    .font(.system(size:9)).foregroundStyle(.secondary).fixedSize(horizontal:false,vertical:true)
            }
            Spacer(minLength:0)
        }.frame(maxHeight:.infinity)
    }
    var footer: some View {
        HStack {
            if let error = model.activeError { Text(error).foregroundStyle(.red).lineLimit(1).help(error) }
            else if model.activeBusy { Text(model.progressText).foregroundStyle(.secondary) }
            else if model.paused { Text(model.statusText).foregroundStyle(.secondary) }
            else if let s = model.activeSnapshot {
                Text(model.t("前9 ","Top 9 ")+String(format:"%.1f%%",s.topTasks.reduce(0) { $0+$1.percentOfPeriod })+model.t(" · 其他 "," · Other ")+model.number(s.summary.otherTasksTotal.totalTokens)).foregroundStyle(.secondary)
            }
            Spacer(minLength:4)
            Text(model.t("最后刷新 ","Updated ")+model.dateText(model.activeSnapshot?.lastUpdated ?? model.activeQuota?.timestamp,timeOnly:true)).foregroundStyle(.secondary)
        }.font(.system(size:8.5,weight:.light)).frame(height:14)
    }
}
struct SettingsView: View {
    @ObservedObject var model: Model
    var body: some View {
        Form {
            Section(model.t("外观","Appearance")) {
                Picker(model.t("主题","Theme"),selection:$model.preferences.theme) {
                    Text(model.t("跟随系统","System")).tag("system"); Text(model.t("浅色","Light")).tag("light"); Text(model.t("深色","Dark")).tag("dark")
                }
                Picker(model.t("语言","Language"),selection:$model.preferences.language) {
                    Text(model.t("跟随系统","System")).tag("system"); Text("简体中文").tag("zh"); Text("English").tag("en")
                }
                Picker(model.t("待机样式","Idle style"),selection:$model.preferences.idleStyle) {
                    Text(model.t("微光","Glow")).tag("glow"); Text(model.t("圆环","Circle")).tag("circle"); Text(model.t("胶囊","Capsule")).tag("capsule")
                }
                Slider(value:$model.preferences.idleScale,in:0.5...1.25,step:0.05) {
                    Text(model.t("图标大小","Icon size") + " \(Int((model.preferences.idleScale*100).rounded()))%")
                }.accessibilityIdentifier("IdleIconScaleSlider")
                Text(model.t("桌面图标实时预览大小；100% 为原始大小。","The desktop icon previews changes live; 100% is the original size."))
                    .font(.caption).foregroundStyle(.secondary)
                Slider(value:$model.preferences.transparency,in:0...1,step:0.01) {
                    Text(model.t("玻璃透明度","Transparency") + " \(Int((model.preferences.transparency*100).rounded()))%")
                }.accessibilityIdentifier("GlassTransparencySlider")
            }
            Section(model.t("常驻行为","Behavior")) {
                Toggle(model.t("保持置顶","Always on top"),isOn:$model.preferences.alwaysOnTop)
                Toggle(model.t("鼠标移开自动收起","Collapse when pointer leaves"),isOn:$model.preferences.autoCollapse)
                Toggle(model.t("息屏、锁屏和休眠时暂停监控","Pause while screen is off, locked or asleep"),isOn:$model.preferences.pauseOnSleep)
                Toggle(model.t("登录时启动","Launch at login"),isOn:Binding(get:{ model.loginEnabled },set:{ model.setLoginEnabled($0) }))
                if let loginError = model.loginError { Text(loginError).font(.caption).foregroundStyle(.secondary) }
            }
            Section(model.t("数据来源","Data source")) {
                Picker(model.t("当前显示","Active source"),selection:Binding(get:{ model.activeProvider },set:{ model.selectProvider($0) })) {
                    Text("Codex").tag(UsageProvider.codex)
                    Text("Gemini (Antigravity)").tag(UsageProvider.gemini)
                }
                VStack(alignment:.leading,spacing:4) {
                    Text("Codex Home").font(.caption).foregroundStyle(.secondary)
                    Text(model.sourceHome.isEmpty ? model.t("自动检测 ~/.codex","Auto-detect ~/.codex") : model.sourceHome)
                        .font(.system(size:11,design:.monospaced)).textSelection(.enabled)
                    HStack {
                        Button(model.t("选择 Codex Home…","Choose Codex Home…")) { chooseHome() }
                        Button(model.t("自动检测","Auto-detect")) { model.preferences.home = "" }
                    }
                }
                VStack(alignment:.leading,spacing:4) {
                    Text("Gemini (Antigravity) Home").font(.caption).foregroundStyle(.secondary)
                    Text(model.geminiSourceHome.isEmpty ? model.t("自动检测 ~/.gemini/antigravity","Auto-detect ~/.gemini/antigravity") : model.geminiSourceHome)
                        .font(.system(size:11,design:.monospaced)).textSelection(.enabled)
                    HStack {
                        Button(model.t("选择 Gemini Home…","Choose Gemini Home…")) { chooseGeminiHome() }
                        Button(model.t("自动检测","Auto-detect")) { model.preferences.geminiHomePath = "" }
                    }
                }
                Text(model.t("只读本地用量日志，不读取登录凭据、不联网。","Reads local usage logs only; no credentials or network access."))
                    .font(.caption).foregroundStyle(.secondary)
            }
            Section(model.t("维护","Maintenance")) {
                if let error = model.activeError { Text(error).font(.caption).foregroundStyle(.red).textSelection(.enabled) }
                HStack {
                    Button(model.t("重建统计索引","Rebuild index")) { model.refresh("rebuild") }.disabled(model.activeBusy)
                    Button(model.t("打开数据目录","Open data folder")) { NSWorkspace.shared.open(Model.dataDirectory) }
                }
                Text("v1.3.0-mac.7 · Apple Silicon · macOS 13+").font(.caption).foregroundStyle(.secondary)
            }
        }.formStyle(.grouped).frame(width:480,height:720)
    }
    func chooseHome() {
        let p = NSOpenPanel(); p.canChooseDirectories = true; p.canChooseFiles = false; p.allowsMultipleSelection = false; p.showsHiddenFiles = true
        p.message = model.t("选择包含 sessions 或 archived_sessions 的 Codex Home 目录。","Choose the Codex Home folder containing sessions or archived_sessions.")
        if p.runModal() == .OK, let url = p.url { model.preferences.home = url.path }
    }
    func chooseGeminiHome() {
        let p = NSOpenPanel(); p.canChooseDirectories = true; p.canChooseFiles = false; p.allowsMultipleSelection = false; p.showsHiddenFiles = true
        p.message = model.t("选择包含 brain 的 Antigravity 数据目录（默认 ~/.gemini/antigravity）。","Choose the Antigravity data folder containing brain (default ~/.gemini/antigravity).")
        if p.runModal() == .OK, let url = p.url { model.preferences.geminiHomePath = url.path }
    }
}
