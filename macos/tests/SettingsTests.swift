import AppKit
import SwiftUI
@main struct SettingsTests {
    static func main() throws {
        let app = NSApplication.shared; app.setActivationPolicy(.prohibited)
        let delegate = AppDelegate()
        delegate.model = Model(persistSettings:false)
        delegate.model.paused = true
        delegate.anchor = CGPoint(x:200,y:200)
        delegate.panel = FloatingPanel(contentRect:CGRect(x:200,y:200,width:420,height:540),styleMask:.borderless,backing:.buffered,defer:false)
        delegate.panel.isReleasedWhenClosed = false
        let model = delegate.model
        let view = NSHostingView(rootView:WidgetView(model:model,settings:{ delegate.showSettings() },hover:{ delegate.hover($0) },startDrag:{ delegate.handleDragEvent($0) },collapse:{ delegate.setExpanded(false) }))
        view.sizingOptions = []; delegate.panel.contentView = view
        model.settingsChanged = { delegate.applySettings($0) }
        model.expanded = true; model.pinned = true; model.showingWeekly = true
        delegate.showSettings()
        assert(model.settingsOpen && !model.expanded && !model.pinned && !model.showingWeekly)
        assert(delegate.panel.frame.size == CGSize(width:80,height:80))
        func effects(_ root:NSView) -> [NSVisualEffectView] {
            (root as? NSVisualEffectView).map { [$0] } ?? root.subviews.flatMap { effects($0) }
        }
        for transparency in [0.0,0.5,1.0,0.25] {
            model.preferences.transparency = transparency
            RunLoop.current.run(until:Date().addingTimeInterval(0.1))
            let glass = effects(view).filter { $0.material == .hudWindow }
            assert(!glass.isEmpty)
            assert(glass.allSatisfy { abs($0.alphaValue-WidgetGeometry.glassOpacity(transparency)) < 0.001 })
        }
        for style in ["circle","glow","capsule"] {
            model.preferences.idleStyle = style
            for scale in [0.5,0.65,1.0,1.25] {
                model.preferences.idleScale = scale
                RunLoop.current.run(until:Date().addingTimeInterval(0.05))
                let size = WidgetGeometry.idleSize(style,scale:scale)
                // AppKit rounds fractional point sizes to the screen's pixel grid.
                assert(abs(delegate.panel.frame.width-size.width) <= 1 && abs(delegate.panel.frame.height-size.height) <= 1)
                assert(abs(view.frame.width-size.width) <= 1 && abs(view.frame.height-size.height) <= 1)
                assert(!model.expanded)
                delegate.hover(true)
                assert(model.expanded) // Settings must not disable the usual hover behavior.
                model.preferences.idleScale = scale == 0.5 ? 0.55 : 0.5
                assert(!model.expanded) // The next slider change restores the idle preview.
                model.preferences.idleScale = scale
            }
        }
        delegate.showSettings() // Opening an existing settings window also stays collapsed.
        assert(!model.expanded)
        delegate.settingsWindow?.close()
        assert(!model.settingsOpen)
        delegate.setExpanded(true)
        assert(model.expanded && delegate.panel.frame.size == CGSize(width:420,height:540))
        delegate.setExpanded(false)
        assert(delegate.panel.frame.size == WidgetGeometry.idleSize("capsule",scale:1.25))
        delegate.panel.orderOut(nil)
        print("PASS: native backdrop alpha updates at 0/25/50/100%; settings collapses pinned/weekly panel; hover still expands; slider restores collapsed preview; all 3 styles resize live at 4 scales; close restores expansion; in-memory model leaves user settings untouched")
    }
}
