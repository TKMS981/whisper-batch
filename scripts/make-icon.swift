// アプリアイコン（App/AppIcon.icns）を生成する。
//   swift scripts/make-icon.swift && iconutil -c icns build/AppIcon.iconset -o App/AppIcon.icns
import AppKit

let sizes = [16, 32, 64, 128, 256, 512, 1024]
let dir = URL(fileURLWithPath: "build/AppIcon.iconset")
try? FileManager.default.removeItem(at: dir)
try! FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)

func render(_ px: Int) -> Data {
    let s = CGFloat(px)
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: px, pixelsHigh: px, bitsPerSample: 8,
                               samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                               colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    let inset = s * 0.1
    let rect = NSRect(x: inset, y: inset, width: s - inset * 2, height: s - inset * 2)
    let path = NSBezierPath(roundedRect: rect, xRadius: rect.width * 0.225, yRadius: rect.width * 0.225)
    NSGradient(starting: NSColor(calibratedRed: 0.20, green: 0.36, blue: 0.75, alpha: 1),
               ending: NSColor(calibratedRed: 0.10, green: 0.18, blue: 0.42, alpha: 1))!.draw(in: path, angle: -90)
    // 波形の棒
    let heights: [CGFloat] = [0.22, 0.42, 0.64, 0.46, 0.30, 0.52, 0.36]
    let barW = rect.width * 0.07
    let gap = rect.width * 0.045
    let total = CGFloat(heights.count) * barW + CGFloat(heights.count - 1) * gap
    var x = rect.midX - total / 2
    NSColor.white.withAlphaComponent(0.95).setFill()
    for h in heights {
        let bh = rect.height * h
        NSBezierPath(roundedRect: NSRect(x: x, y: rect.midY - bh / 2 + rect.height * 0.06, width: barW, height: bh),
                     xRadius: barW / 2, yRadius: barW / 2).fill()
        x += barW + gap
    }
    // テキスト行
    NSColor.white.withAlphaComponent(0.55).setFill()
    for (i, w) in [0.56, 0.40].enumerated() {
        let lineH = rect.height * 0.045
        let y = rect.minY + rect.height * (0.17 - CGFloat(i) * 0.08)
        NSBezierPath(roundedRect: NSRect(x: rect.midX - rect.width * w / 2, y: y, width: rect.width * w, height: lineH),
                     xRadius: lineH / 2, yRadius: lineH / 2).fill()
    }
    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])!
}

for size in sizes where size <= 512 {
    try! render(size).write(to: dir.appendingPathComponent("icon_\(size)x\(size).png"))
    try! render(size * 2).write(to: dir.appendingPathComponent("icon_\(size)x\(size)@2x.png"))
}
print("wrote \(dir.path)")
