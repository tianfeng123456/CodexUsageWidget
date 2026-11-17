import Foundation
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
        print("PASS: Chinese/English units, .NET timestamps, most constrained window, weekly selection, percentage clamping, settings roundtrip, 3-style edge geometry, anchor stability, native drag hit regions and offsets, negative-screen clamp, rounded hover, settings validation, status rounding, progress, sleep gate")
    }
}
