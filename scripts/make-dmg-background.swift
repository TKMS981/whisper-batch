// DMG ウィンドウの背景画像を生成する（scripts/make-dmg.sh から呼ばれる）。
//   swift scripts/make-dmg-background.swift <出力フォルダ>
// 600×380 pt の 1x / 2x PNG を書き出す。アイコンは dmg-settings.py で
// (160, 170) と (440, 170) に置くので、その間に矢印を描く。
import AppKit

let width = 600, height = 380
let outDir = URL(fileURLWithPath: CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "build")

func render(scale: Int) -> Data {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: width * scale, pixelsHigh: height * scale,
                               bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                               colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    rep.size = NSSize(width: width, height: height)  // 描画はポイント単位
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)

    let bounds = NSRect(x: 0, y: 0, width: width, height: height)
    NSGradient(starting: NSColor(calibratedWhite: 0.98, alpha: 1),
               ending: NSColor(calibratedWhite: 0.92, alpha: 1))!.draw(in: bounds, angle: -90)

    // 矢印（Finder の座標は上から、AppKit は下からなので y を反転）
    let arrowY = CGFloat(height - 170)
    let color = NSColor(calibratedRed: 0.20, green: 0.36, blue: 0.75, alpha: 0.85)
    color.setStroke()
    color.setFill()
    let shaft = NSBezierPath()
    shaft.move(to: NSPoint(x: 245, y: arrowY))
    shaft.line(to: NSPoint(x: 340, y: arrowY))
    shaft.lineWidth = 6
    shaft.lineCapStyle = .round
    shaft.stroke()
    let head = NSBezierPath()
    head.move(to: NSPoint(x: 360, y: arrowY))
    head.line(to: NSPoint(x: 336, y: arrowY + 16))
    head.line(to: NSPoint(x: 336, y: arrowY - 16))
    head.close()
    head.fill()

    // 説明文（日本語・英語）
    let style = NSMutableParagraphStyle()
    style.alignment = .center
    func draw(_ text: String, y: CGFloat, size: CGFloat, alpha: CGFloat) {
        let attrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: size, weight: .medium),
            .foregroundColor: NSColor(calibratedWhite: 0.25, alpha: alpha),
            .paragraphStyle: style,
        ]
        NSString(string: text).draw(in: NSRect(x: 0, y: y, width: CGFloat(width), height: size * 1.6),
                                    withAttributes: attrs)
    }
    draw("Whisper Batch を Applications フォルダへドラッグしてください", y: 72, size: 14, alpha: 0.9)
    draw("Drag Whisper Batch to the Applications folder to install", y: 48, size: 12, alpha: 0.6)

    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])!
}

try! FileManager.default.createDirectory(at: outDir, withIntermediateDirectories: true)
try! render(scale: 1).write(to: outDir.appendingPathComponent("background.png"))
try! render(scale: 2).write(to: outDir.appendingPathComponent("background@2x.png"))
