import Foundation
struct WidgetGeometry {
    // Fade the native backdrop itself; a tint-only change leaves HUD glass opaque.
    // Keep a nonzero material layer at the transparent endpoint.
    static func glassOpacity(_ transparency:Double) -> Double {
        let value = transparency.isFinite ? min(1,max(0,transparency)) : 0.5
        return max(0.02,1-value)
    }
    static func idleSize(_ style:String, scale:Double = 1) -> CGSize {
        let factor = scale.isFinite ? min(1.25,max(0.5,scale)) : 1
        let base: CGSize
        switch style { case "glow": base = CGSize(width:32,height:32)
        case "capsule": base = CGSize(width:208,height:80)
        default: base = CGSize(width:80,height:80) }
        return CGSize(width:base.width*factor,height:base.height*factor)
    }
    static func frame(anchor:inout CGPoint, style:String, expanded:Bool, bounds:CGRect, scale:Double = 1) -> CGRect {
        let idle = idleSize(style,scale:scale)
        let size = expanded ? CGSize(width:420,height:540) : idle
        anchor.x = max(bounds.minX,min(anchor.x,bounds.maxX-idle.width))
        anchor.y = max(bounds.minY,min(anchor.y,bounds.maxY-idle.height))
        var origin = anchor
        if expanded {
            if anchor.x+size.width > bounds.maxX { origin.x = anchor.x+idle.width-size.width }
            origin.y = anchor.y+idle.height-size.height
        }
        origin.x = max(bounds.minX,min(origin.x,bounds.maxX-size.width))
        origin.y = max(bounds.minY,min(origin.y,bounds.maxY-size.height))
        return CGRect(origin:origin,size:size)
    }
    static func clamp(_ frame:CGRect,to bounds:CGRect) -> CGRect {
        CGRect(x:max(bounds.minX,min(frame.minX,bounds.maxX-frame.width)),
               y:max(bounds.minY,min(frame.minY,bounds.maxY-frame.height)),width:frame.width,height:frame.height)
    }
    static func movedAnchor(_ anchor:CGPoint,from oldFrame:CGRect,to newFrame:CGRect) -> CGPoint {
        CGPoint(x:anchor.x+newFrame.minX-oldFrame.minX,y:anchor.y+newFrame.minY-oldFrame.minY)
    }
    static func isDragPoint(_ point:CGPoint,expanded:Bool,controls:[CGRect]) -> Bool {
        !expanded || (point.y <= 112 && !controls.contains { $0.insetBy(dx:-3,dy:-3).contains(point) })
    }
    static func containsRounded(_ point:CGPoint,frame:CGRect,radius:CGFloat) -> Bool {
        guard frame.contains(point) else { return false }
        let r = min(radius,min(frame.width,frame.height)/2)
        let center = CGPoint(x:max(frame.minX+r,min(point.x,frame.maxX-r)),
                             y:max(frame.minY+r,min(point.y,frame.maxY-r)))
        return hypot(point.x-center.x,point.y-center.y) <= r
    }
    static func shouldPause(enabled:Bool,screenOff:Bool,locked:Bool,sleeping:Bool) -> Bool {
        enabled && (screenOff || locked || sleeping)
    }
}
