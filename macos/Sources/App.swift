import AppKit
import SwiftUI
import ServiceManagement
import Darwin
import Combine

final class FloatingPanel: NSPanel {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }
    override func sendEvent(_ event:NSEvent) {
        if CommandLine.arguments.contains("--interaction-audit"), [.leftMouseDown,.leftMouseUp,.leftMouseDragged].contains(event.type) {
            fputs("input \(event.type.rawValue) point \(event.locationInWindow) global \(String(describing:event.cgEvent?.location)) mouse \(NSEvent.mouseLocation) frame \(frame)\n",stderr)
        }
        super.sendEvent(event)
    }
}
@main
final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate {
    var model = Model()
    var panel: FloatingPanel!
    var settingsWindow: NSWindow?
    var status: NSStatusItem!
    var anchor = NSPoint(x:120,y:120)
    var isDragging = false
    var dragStartFrame: CGRect?
    var dragStartAnchor = CGPoint.zero
    var dragStartPointer = CGPoint.zero
    var dragWasExpanded = false
    var dragEndTimer: Timer?
    var hoverTrigger: NSRect?
    var inputMonitor: Any?
    var pointerTimer: Timer?
    var subscriptions = Set<AnyCancellable>()
    var collapseWork: DispatchWorkItem?
    var observers: [NSObjectProtocol] = []
    var screensOff = false, locked = false, sleeping = false
    var lockFD: Int32 = -1
    static func main() {
        let app = NSApplication.shared
        let delegate = AppDelegate(); app.delegate = delegate
        app.setActivationPolicy(.accessory)
        withExtendedLifetime(delegate) { app.run() }
    }
    func applicationDidFinishLaunching(_ notification: Notification) {
        try? FileManager.default.createDirectory(at:Model.dataDirectory,withIntermediateDirectories:true)
        lockFD = open(Model.dataDirectory.appendingPathComponent("instance.lock").path,O_CREAT|O_RDWR,0o600)
        guard lockFD >= 0, flock(lockFD,LOCK_EX|LOCK_NB) == 0 else {
            DistributedNotificationCenter.default().postNotificationName(.init("com.tianfeng.codexusagewidget.show"),object:nil)
            NSApp.terminate(nil); return
        }
        if let x = model.preferences.x, let y = model.preferences.y { anchor = NSPoint(x:x,y:y) }
        else if let screen = NSScreen.main { anchor = NSPoint(x:screen.visibleFrame.maxX-110,y:screen.visibleFrame.maxY-130) }
        panel = FloatingPanel(contentRect:NSRect(origin:anchor,size:idleSize),styleMask:[.borderless,.nonactivatingPanel],backing:.buffered,defer:false)
        panel.title = "Codex Usage Widget"; panel.identifier = NSUserInterfaceItemIdentifier("CodexUsageWidget")
        panel.isOpaque = false; panel.backgroundColor = .clear; panel.hasShadow = true
        panel.delegate = self; panel.isMovable = true
        panel.hidesOnDeactivate = false; panel.isReleasedWhenClosed = false
        panel.collectionBehavior = [.canJoinAllSpaces,.fullScreenAuxiliary,.stationary]
        let hosting = NSHostingView(rootView:WidgetView(model:model,settings:{ [weak self] in self?.showSettings() },hover:{ [weak self] in self?.hover($0) },startDrag:{ [weak self] in self?.handleDragEvent($0) },collapse:{ [weak self] in self?.setExpanded(false) }))
        hosting.sizingOptions = []
        panel.contentView = hosting
        model.settingsChanged = { [weak self] old in self?.applySettings(old) }
        model.quotaChanged = { [weak self] in self?.updateStatus() }
        status = NSStatusBar.system.statusItem(withLength:NSStatusItem.variableLength)
        updateStatus(); applySettings(model.preferences); position()
        subscribe()
        installInputMonitor()
        model.$pinned.removeDuplicates().sink { [weak self] _ in
            DispatchQueue.main.async { self?.syncPointerMonitor() }
        }.store(in:&subscriptions)
        model.start(); panel.orderFrontRegardless()
        if CommandLine.arguments.contains("--expanded") { setExpanded(true); model.pinned = true }
        if let i = CommandLine.arguments.firstIndex(of:"--capture"), CommandLine.arguments.count > i+1 {
            let path = CommandLine.arguments[i+1]
            DispatchQueue.main.asyncAfter(deadline:.now()+12) { [weak self] in self?.capture(path) }
        }
    }
    var idleSize: NSSize { WidgetGeometry.idleSize(model.preferences.idleStyle) }
    func position() {
        let screen = NSScreen.screens.first { $0.visibleFrame.contains(NSPoint(x:anchor.x+idleSize.width/2,y:anchor.y+idleSize.height/2)) } ?? NSScreen.main
        guard let screen else { return }
        panel.setFrame(WidgetGeometry.frame(anchor:&anchor,style:model.preferences.idleStyle,expanded:model.expanded,bounds:screen.visibleFrame),display:true)
    }
    func setExpanded(_ value: Bool) {
        guard !isDragging, model.expanded != value else { return }
        collapseWork?.cancel()
        hoverTrigger = value && model.preferences.idleStyle == "glow" ? panel.frame : nil
        model.expanded = value
        if value { model.showingWeekly = false; model.selectPeriod(0) }
        else { model.pinned = false; model.showingWeekly = false }
        position(); syncPointerMonitor()
    }
    func hover(_ entered: Bool) {
        guard !isDragging else { return }
        collapseWork?.cancel()
        if entered { setExpanded(true) }
        else {
            let work = DispatchWorkItem { [weak self] in self?.checkPointer() }
            collapseWork = work
            // One input turn, rather than an intentional collapse delay.
            DispatchQueue.main.async(execute:work)
        }
    }
    var shouldCheckPointer: Bool {
        model.expanded && !model.pinned && model.preferences.autoCollapse &&
        !model.paused && panel.isVisible && settingsWindow?.isVisible != true
    }
    func checkPointer() {
        guard shouldCheckPointer, !isDragging, NSEvent.pressedMouseButtons == 0 else { return }
        let point = NSEvent.mouseLocation
        if let trigger = hoverTrigger {
            if trigger.contains(point) { return }
            hoverTrigger = nil
        }
        if !WidgetGeometry.containsRounded(point,frame:panel.frame,radius:32) { setExpanded(false) }
    }
    func syncPointerMonitor() {
        guard shouldCheckPointer else { pointerTimer?.invalidate(); pointerTimer = nil; return }
        guard pointerTimer == nil else { return }
        let timer = Timer(timeInterval:0.125,repeats:true) { [weak self] _ in self?.checkPointer() }
        timer.tolerance = 0.02
        RunLoop.main.add(timer,forMode:.common); pointerTimer = timer
    }
    func installInputMonitor() {
        inputMonitor = NSEvent.addLocalMonitorForEvents(matching:.keyDown) { [weak self] event in
            guard let self, event.window === self.panel else { return event }
            if event.keyCode == 53, self.model.showingWeekly { self.model.showingWeekly = false; return nil }
            return event
        }
    }
    func screenPoint(_ event:NSEvent) -> CGPoint {
        if let location = event.cgEvent?.location, let primary = NSScreen.screens.first {
            return CGPoint(x:location.x,y:primary.frame.maxY-location.y)
        }
        return NSEvent.mouseLocation
    }
    func handleDragEvent(_ event:NSEvent) {
        if event.type == .leftMouseDown {
            collapseWork?.cancel(); hoverTrigger = nil; isDragging = true
            dragStartFrame = panel.frame; dragStartAnchor = anchor; dragWasExpanded = model.expanded
            dragStartPointer = screenPoint(event)
            dragEndTimer?.invalidate()
            let timer = Timer(timeInterval:0.125,repeats:true) { [weak self] _ in
                if NSEvent.pressedMouseButtons & 1 == 0 { self?.finishWindowDrag() }
            }
            RunLoop.main.add(timer,forMode:.common); dragEndTimer = timer
        } else if isDragging, let oldFrame = dragStartFrame {
            // Keep the pointer and original window origin in screen points;
            // moving the view must not change the coordinate space of the delta.
            let pointer = screenPoint(event)
            panel.setFrameOrigin(CGPoint(x:oldFrame.minX+pointer.x-dragStartPointer.x,
                                         y:oldFrame.minY+pointer.y-dragStartPointer.y))
            if event.type == .leftMouseUp { finishWindowDrag() }
        }
    }
    func windowDidMove(_ notification:Notification) {
        guard (notification.object as? NSWindow) === panel, isDragging, let oldFrame = dragStartFrame else { return }
        anchor = WidgetGeometry.movedAnchor(dragStartAnchor,from:oldFrame,to:panel.frame)
    }
    func finishWindowDrag() {
        guard isDragging, let oldFrame = dragStartFrame else { return }
        dragEndTimer?.invalidate(); dragEndTimer = nil
        var movedFrame = panel.frame
        if let screen = panel.screen ?? NSScreen.main { movedFrame = WidgetGeometry.clamp(movedFrame,to:screen.visibleFrame) }
        panel.setFrame(movedFrame,display:true)
        anchor = WidgetGeometry.movedAnchor(dragStartAnchor,from:oldFrame,to:movedFrame)
        let clickedIdle = !dragWasExpanded && hypot(movedFrame.minX-oldFrame.minX,movedFrame.minY-oldFrame.minY) < 3
        isDragging = false; dragStartFrame = nil; savePosition()
        if clickedIdle { setExpanded(true) }
        syncPointerMonitor(); hover(false)
    }
    func savePosition() {
        var prefs = model.preferences; prefs.x = anchor.x; prefs.y = anchor.y; model.preferences = prefs
    }
    func applySettings(_ old: Preferences) {
        guard panel != nil else { return }
        panel.level = model.preferences.alwaysOnTop ? .floating : .normal
        let appearance: NSAppearance? = model.preferences.theme == "dark" ? NSAppearance(named:.darkAqua) : model.preferences.theme == "light" ? NSAppearance(named:.aqua) : nil
        panel.appearance = appearance; settingsWindow?.appearance = appearance
        if old.idleStyle != model.preferences.idleStyle { hoverTrigger = nil; position() }
        if old.home != model.preferences.home { model.snapshot = nil; model.quota = nil; model.weekly = []; if !model.paused { model.start() } }
        if old.pauseOnSleep != model.preferences.pauseOnSleep { updateSleep() }
        syncPointerMonitor(); updateStatus()
    }
    func updateStatus() {
        guard status != nil else { return }
        status.button?.image = NSImage(systemSymbolName:"circle.hexagongrid",accessibilityDescription:"Codex Usage")
        status.button?.title = " " + model.remainingText
        status.button?.toolTip = model.t("Codex 剩余额度 · ","Codex remaining · ")+model.statusText
        let menu = NSMenu()
        add(menu,model.t("显示 / 隐藏","Show / Hide"),#selector(toggleVisible))
        add(menu,model.t("立即刷新","Refresh now"),#selector(refresh))
        add(menu,model.t("设置…","Settings…"),#selector(showSettings))
        model.syncLoginStatus()
        let login = add(menu,model.t("登录时启动","Launch at login"),#selector(toggleLogin)); login.state = model.loginEnabled ? .on : .off
        menu.addItem(.separator())
        add(menu,model.t("退出","Quit"),#selector(quit)); status.menu = menu
    }
    @discardableResult func add(_ menu:NSMenu,_ title:String,_ action:Selector) -> NSMenuItem {
        let item = NSMenuItem(title:title,action:action,keyEquivalent:""); item.target = self; menu.addItem(item); return item
    }
    @objc func toggleVisible() { if panel.isVisible { panel.orderOut(nil) } else { panel.orderFrontRegardless(); setExpanded(true) }; syncPointerMonitor() }
    @objc func refresh() { model.refresh(model.showingWeekly ? "weekly" : "refresh") }
    @objc func quit() { NSApp.terminate(nil) }
    @objc func toggleLogin() {
        model.syncLoginStatus(); model.setLoginEnabled(!model.loginEnabled)
        updateStatus()
        if model.loginError != nil { showSettings() }
    }
    @objc func showSettings() {
        collapseWork?.cancel()
        if settingsWindow == nil {
            let w = NSWindow(contentRect:NSRect(x:0,y:0,width:480,height:650),styleMask:[.titled,.closable],backing:.buffered,defer:false)
            w.delegate = self; w.isReleasedWhenClosed = false; w.contentView = NSHostingView(rootView:SettingsView(model:model)); w.center(); settingsWindow = w
        }
        settingsWindow?.title = model.t("Codex 用量 · 设置","Codex Usage · Settings")
        settingsWindow?.appearance = panel.appearance
        model.syncLoginStatus()
        NSApp.activate(ignoringOtherApps:true); settingsWindow?.makeKeyAndOrderFront(nil); syncPointerMonitor()
    }
    func windowWillClose(_ notification:Notification) {
        DispatchQueue.main.async { [weak self] in self?.syncPointerMonitor(); self?.hover(false) }
    }
    func subscribe() {
        let ws = NSWorkspace.shared.notificationCenter
        func workspace(_ name:Notification.Name,_ action:@escaping () -> Void) {
            observers.append(ws.addObserver(forName:name,object:nil,queue:.main) { _ in action() })
        }
        workspace(NSWorkspace.screensDidSleepNotification) { [weak self] in self?.screensOff = true; self?.updateSleep() }
        workspace(NSWorkspace.screensDidWakeNotification) { [weak self] in self?.screensOff = false; self?.updateSleep() }
        workspace(NSWorkspace.willSleepNotification) { [weak self] in self?.sleeping = true; self?.updateSleep() }
        workspace(NSWorkspace.didWakeNotification) { [weak self] in self?.sleeping = false; self?.updateSleep() }
        workspace(NSWorkspace.sessionDidResignActiveNotification) { [weak self] in self?.locked = true; self?.updateSleep() }
        workspace(NSWorkspace.sessionDidBecomeActiveNotification) { [weak self] in self?.locked = false; self?.updateSleep() }
        let dc = DistributedNotificationCenter.default()
        observers.append(dc.addObserver(forName:.init("com.apple.screenIsLocked"),object:nil,queue:.main) { [weak self] _ in self?.locked = true; self?.updateSleep() })
        observers.append(dc.addObserver(forName:.init("com.apple.screenIsUnlocked"),object:nil,queue:.main) { [weak self] _ in self?.locked = false; self?.updateSleep() })
        observers.append(dc.addObserver(forName:.init("com.tianfeng.codexusagewidget.show"),object:nil,queue:.main) { [weak self] _ in self?.panel.orderFrontRegardless(); self?.setExpanded(true) })
        observers.append(NotificationCenter.default.addObserver(forName:NSApplication.didChangeScreenParametersNotification,object:nil,queue:.main) { [weak self] _ in self?.position() })
    }
    func updateSleep() {
        let shouldPause = WidgetGeometry.shouldPause(enabled:model.preferences.pauseOnSleep,screenOff:screensOff,locked:locked,sleeping:sleeping)
        if shouldPause && !model.paused { model.pause() }
        else if !shouldPause && model.paused { model.start() }
        syncPointerMonitor(); updateStatus()
    }
    func applicationShouldHandleReopen(_ sender:NSApplication,hasVisibleWindows:Bool) -> Bool { panel.orderFrontRegardless(); setExpanded(true); return true }
    func applicationWillTerminate(_ notification:Notification) { pointerTimer?.invalidate(); dragEndTimer?.invalidate(); if let inputMonitor { NSEvent.removeMonitor(inputMonitor) }; model.stop(); if panel != nil { savePosition() }; if lockFD >= 0 { close(lockFD) } }
    func capture(_ path:String) {
        guard let v = panel.contentView, let bitmap = v.bitmapImageRepForCachingDisplay(in:v.bounds) else { return }
        v.cacheDisplay(in:v.bounds,to:bitmap)
        if let png = bitmap.representation(using:.png,properties:[:]) { try? png.write(to:URL(fileURLWithPath:path)) }
    }
}
