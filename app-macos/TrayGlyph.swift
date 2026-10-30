import AppKit

// тот же значок, что в трее Windows: контур микрофона, цвет волны показывает состояние; рисунок задан в сетке 16x16
enum TrayGlyph {
    case on, off, problem

    private var wave: NSColor {
        switch self {
        case .on: return NSColor(name: nil) { $0.bestMatch(from: [.aqua, .darkAqua]) == .aqua ? NSColor(srgbRed: 0.04, green: 0.55, blue: 0.5, alpha: 1) : NSColor(srgbRed: 0.09, green: 0.82, blue: 0.75, alpha: 1) }
        case .off: return NSColor(srgbRed: 0.55, green: 0.55, blue: 0.55, alpha: 1)
        case .problem: return NSColor(name: nil) { $0.bestMatch(from: [.aqua, .darkAqua]) == .aqua ? NSColor(srgbRed: 0.77, green: 0.17, blue: 0.11, alpha: 1) : NSColor(srgbRed: 1, green: 0.43, blue: 0.43, alpha: 1) }
        }
    }

    // цвет контура зависит от строки меню, поэтому рисуем при каждом показе
    var image: NSImage {
        let image = NSImage(size: NSSize(width: 18, height: 18), flipped: true) { _ in
            let scale = 18.0 / 16.0
            func stroke(_ path: NSBezierPath, _ width: CGFloat, _ color: NSColor) {
                path.transform(using: AffineTransform(scale: scale))
                path.lineWidth = width * scale
                path.lineCapStyle = .round
                path.lineJoinStyle = .round
                color.setStroke()
                path.stroke()
            }
            let capsule = NSBezierPath(roundedRect: NSRect(x: 4.5, y: 0.75, width: 7, height: 10), xRadius: 3.5, yRadius: 3.5)
            stroke(capsule, 1.15, .labelColor)
            let stand = NSBezierPath()
            stand.move(to: NSPoint(x: 2, y: 6.5))
            stand.line(to: NSPoint(x: 2, y: 7.875))
            stand.curve(to: NSPoint(x: 8, y: 13.5), controlPoint1: NSPoint(x: 2, y: 10.98), controlPoint2: NSPoint(x: 4.69, y: 13.5))
            stand.curve(to: NSPoint(x: 14, y: 7.875), controlPoint1: NSPoint(x: 11.31, y: 13.5), controlPoint2: NSPoint(x: 14, y: 10.98))
            stand.line(to: NSPoint(x: 14, y: 6.5))
            stand.move(to: NSPoint(x: 8, y: 13.6))
            stand.line(to: NSPoint(x: 8, y: 15.25))
            stand.move(to: NSPoint(x: 5.5, y: 15.25))
            stand.line(to: NSPoint(x: 10.5, y: 15.25))
            stroke(stand, 1.15, .labelColor)
            let line = NSBezierPath()
            for index in 0...16 {
                let t = CGFloat(index) / 16
                let point = NSPoint(x: 5.9 + 4.2 * t, y: 6.2 - sin(t * .pi * 2) * 1.9)
                if index == 0 { line.move(to: point) } else { line.line(to: point) }
            }
            stroke(line, 1.35, wave)
            return true
        }
        image.cacheMode = .never
        image.accessibilityDescription = "Noise Reduction"
        return image
    }
}
