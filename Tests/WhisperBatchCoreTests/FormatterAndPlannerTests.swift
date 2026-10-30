import Foundation
import Testing
@testable import WhisperBatchCore

private let sample = Transcript(segments: [
    .init(start: 0, end: 2.5, text: " 本日は晴天なり。"),
    .init(start: 2.5, end: 3661.25, text: "二行目 --> です\n改行あり"),
    .init(start: 4000, end: 4000, text: "   "),
], language: "ja")

@Suite("出力フォーマット")
struct FormatterTests {
    init() { TestLocalization.setUp() }

    @Test func plainText() {
        #expect(TranscriptFormatter.plainText(sample) == "本日は晴天なり。\n二行目 --> です 改行あり\n")
    }

    @Test func plainTextWithTimestamps() {
        #expect(TranscriptFormatter.plainText(sample, includeTimestamps: true)
            == "[00:00] 本日は晴天なり。\n[00:02] 二行目 --> です 改行あり\n")
    }

    @Test func emptyTranscript() {
        let t = Transcript(segments: [.init(start: 0, end: 1, text: " ")], language: nil)
        #expect(t.isEmpty)
        #expect(TranscriptFormatter.plainText(t) == TranscriptFormatter.noSpeechText + "\n")
        #expect(TranscriptFormatter.srt(t) == "")
        #expect(TranscriptFormatter.vtt(t) == "WEBVTT\n\n")
    }

    @Test func srt() {
        let expected = """
        1
        00:00:00,000 --> 00:00:02,500
        本日は晴天なり。

        2
        00:00:02,500 --> 01:01:01,250
        二行目 --> です 改行あり


        """
        #expect(TranscriptFormatter.srt(sample) == expected)
    }

    @Test func vttEscapesArrowAndUsesDot() {
        let out = TranscriptFormatter.vtt(sample)
        #expect(out.hasPrefix("WEBVTT\n\n00:00:00.000 --> 00:00:02.500\n本日は晴天なり。\n\n"))
        #expect(out.contains("二行目 → です 改行あり"))
    }

    @Test func zeroLengthCueGetsMinimumDuration() {
        let t = Transcript(segments: [.init(start: 5, end: 5, text: "a")], language: nil)
        #expect(TranscriptFormatter.srt(t).contains("00:00:05,000 --> 00:00:05,500"))
    }

    @Test func combinedKeepsOrderAndHeadings() {
        let a = Transcript(segments: [.init(start: 0, end: 1, text: "A の内容")], language: "ja")
        let b = Transcript(segments: [], language: "ja")
        let out = TranscriptFormatter.combinedPlainText([
            CombinedEntry(title: "2_後.m4a", transcript: a),
            CombinedEntry(title: "1_先.wav", transcript: b),
        ])
        let rule = String(repeating: "=", count: 40)
        #expect(out == """
        \(rule)
        [1/2] 2_後.m4a
        \(rule)

        A の内容

        \(rule)
        [2/2] 1_先.wav
        \(rule)

        \(TranscriptFormatter.noSpeechText)

        """)
    }

    @Test func clock() {
        #expect(TranscriptFormatter.clock(65.9) == "01:05")
        #expect(TranscriptFormatter.clock(3725) == "1:02:05")
    }
}

@Suite("チャンク境界の決定")
struct ChunkPlannerTests {
    init() { TestLocalization.setUp() }

    let planner = ChunkPlanner(chunkDuration: 60, tailMargin: 5) // minimumAdvance = 30

    @Test func finalChunkAcceptsEverything() {
        let segs = [TranscriptSegment(start: 0, end: 10, text: "a"), .init(start: 10, end: 20, text: "b")]
        let d = planner.decide(segments: segs, bufferDuration: 20, isFinal: true)
        #expect(d.accepted == segs)
        #expect(d.resumeAt == 20)
    }

    @Test func dropsSegmentNearEndAndResumesAtItsStart() {
        let segs = [
            TranscriptSegment(start: 0, end: 20, text: "a"),
            .init(start: 20, end: 52, text: "b"),
            .init(start: 52, end: 59.8, text: "途中で切れた文"),
        ]
        let d = planner.decide(segments: segs, bufferDuration: 60, isFinal: false)
        #expect(d.accepted.map(\.text) == ["a", "b"])
        #expect(d.resumeAt == 52)
    }

    @Test func longSegmentCrossingBoundaryIsKeptToGuaranteeProgress() {
        let segs = [TranscriptSegment(start: 1, end: 58, text: "とても長い区間")]
        let d = planner.decide(segments: segs, bufferDuration: 60, isFinal: false)
        #expect(d.accepted.count == 1)
        #expect(d.resumeAt == 58)
    }

    @Test func trailingSilenceIsReread() {
        let segs = [TranscriptSegment(start: 0, end: 20, text: "a"), .init(start: 20, end: 40, text: "b")]
        let d = planner.decide(segments: segs, bufferDuration: 60, isFinal: false)
        #expect(d.accepted.count == 2)
        #expect(d.resumeAt == 40)
    }

    @Test func noSpeechAdvancesAlmostWholeChunk() {
        let d = planner.decide(segments: [], bufferDuration: 60, isFinal: false)
        #expect(d.accepted.isEmpty)
        #expect(d.resumeAt == 55)
    }

    /// 擬似的な「認識器」で、チャンクをつなげた結果に欠落・重複がないことを確かめる
    @Test func simulatedStreamHasNoGapsOrDuplicates() {
        // 7 秒ごとに 1 文（6 秒の発話 + 1 秒の間）が 100 文ある音声を想定
        let sentences = (0 ..< 100).map { i in TranscriptSegment(start: Double(i) * 7, end: Double(i) * 7 + 6, text: "文\(i)") }
        let total = 700.0
        var start = 0.0
        var output: [TranscriptSegment] = []
        while true {
            let length = min(planner.chunkDuration, total - start)
            let isFinal = start + length >= total
            // チャンク内に含まれる文。末尾で切れた文は切れた位置で終わる
            let visible = sentences.compactMap { s -> TranscriptSegment? in
                guard s.end > start, s.start < start + length else { return nil }
                let a = max(s.start, start) - start
                let b = min(s.end, start + length) - start
                // 先頭で切れた文の後半は認識されない想定（resumeAt で前に戻るので発生しない）
                guard s.start >= start else { return nil }
                return .init(start: a, end: b, text: s.text)
            }
            let d = planner.decide(segments: visible, bufferDuration: length, isFinal: isFinal)
            output += d.accepted.map { .init(start: $0.start + start, end: $0.end + start, text: $0.text) }
            if isFinal { break }
            start += d.resumeAt
        }
        #expect(output.map(\.text) == sentences.map(\.text))
    }

    @Test func cutsAtQuietestPointNearEnd() {
        // 100 サンプル/秒として 60 秒。53〜53.5 秒だけ無音にする
        var samples = (0 ..< 6000).map { i in Float(sin(Double(i) * 0.7)) * 0.5 }
        for i in 5300 ..< 5350 { samples[i] = 0 }
        let p = ChunkPlanner(chunkDuration: 60, tailMargin: 3, boundarySearch: 15)
        let cut = p.cutLength(samples: samples, sampleRate: 100, isFinal: false)
        #expect((5300 ... 5350).contains(cut))
        #expect(p.cutLength(samples: samples, sampleRate: 100, isFinal: true) == 6000)
    }

    @Test func quietestPointHandlesSmallRanges() {
        let samples = [Float](repeating: 0.1, count: 10)
        #expect(SilenceFinder.quietestPoint(in: samples, range: 0 ..< 5, window: 20) == 5)
        #expect(SilenceFinder.quietestPoint(in: samples, range: 0 ..< 0, window: 2) == 10)
    }
}

@Suite("書き出しファイル名")
struct ExportNamingTests {
    init() { TestLocalization.setUp() }

    @Test func basicNames() {
        let p = ExportNaming.plan(audioFileNames: ["会議.m4a", "memo.wav"], format: .plainText, existing: [], policy: .replace)
        #expect(p.fileNames == ["会議.txt", "memo.txt"])
        #expect(p.replacing.isEmpty)
    }

    @Test func duplicatesInSameExportAreNumbered() {
        let p = ExportNaming.plan(audioFileNames: ["a.wav", "a.mp3", "A.m4a"], format: .srt, existing: [], policy: .replace)
        #expect(p.fileNames == ["a.srt", "a (2).srt", "A (3).srt"])
    }

    @Test func replacePolicyReportsConflicts() {
        let p = ExportNaming.plan(audioFileNames: ["a.wav", "b.wav"], format: .plainText, existing: ["a.txt"], policy: .replace)
        #expect(p.fileNames == ["a.txt", "b.txt"])
        #expect(p.replacing == ["a.txt"])
    }

    @Test func keepBothPolicyAvoidsExisting() {
        let p = ExportNaming.plan(audioFileNames: ["a.wav"], format: .plainText, existing: ["a.txt", "a (2).txt"], policy: .keepBoth)
        #expect(p.fileNames == ["a (3).txt"])
        #expect(p.replacing.isEmpty)
    }
}

@Suite("モデル一覧")
struct ModelCatalogTests {
    init() { TestLocalization.setUp() }

    @Test func defaultModelExistsAndCatalogIsConsistent() {
        #expect(ModelCatalog.model(id: ModelCatalog.defaultModelID) != nil)
        #expect(Set(ModelCatalog.all.map(\.id)).count == ModelCatalog.all.count)
        for m in ModelCatalog.all where m.isDownloadable {
            #expect(m.requiredFreeSpace > m.sizeBytes)
            for file in m.files {
                #expect(file.sha256.count == 64)
                #expect(file.url.scheme == "https")
                #expect(file.url.absoluteString.hasSuffix("/" + file.name))
            }
            // 同じフォルダに同じ名前のファイルを置かない
            #expect(Set(m.files.map(\.name)).count == m.files.count)
        }
    }

    /// 方式ごとに既定のモデルがあり、Apple のモデルはダウンロードしない
    @Test func everyEngineHasModels() {
        for engine in SpeechEngine.allCases {
            let models = ModelCatalog.models(for: engine)
            #expect(!models.isEmpty)
            #expect(ModelCatalog.defaultModel(for: engine).engine == engine)
            #expect(models.allSatisfy { $0.isDownloadable == engine.usesDownloadedFiles })
        }
        // Whisper は以前の版と同じ場所（保存先の直下）、ほかは専用のフォルダ
        #expect(ModelCatalog.models(for: .whisper).allSatisfy { $0.folder == nil })
        let folders = ModelCatalog.models(for: .reazonSpeech).compactMap(\.folder)
        #expect(folders.count == 2 && Set(folders).count == 2)
        // どちらもコミットを固定して取得する（main の更新で取得するファイルが変わらないように）
        for m in ModelCatalog.models(for: .whisper) {
            #expect(m.files.allSatisfy { $0.url.absoluteString.hasPrefix("https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c") })
        }
        // ReazonSpeech はコミットを固定した公式リポジトリから取得する
        for m in ModelCatalog.models(for: .reazonSpeech) {
            #expect(m.files.allSatisfy { $0.url.absoluteString.hasPrefix("https://huggingface.co/reazon-research/reazonspeech-k2-v2/resolve/291488c") })
        }
    }

    @Test func engineLanguageSupport() {
        #expect(SpeechEngine.whisper.languageOptions == LanguageOption.allCases)
        #expect(SpeechEngine.reazonSpeech.languageOptions == [.japanese])
        #expect(!SpeechEngine.apple.languageOptions.contains(.auto))
        for option in LanguageOption.allCases where option != .auto {
            #expect(option.localeIdentifier != nil)
        }
    }

    @Test func languageStorageRoundTrip() {
        for option in LanguageOption.allCases {
            #expect(LanguageOption(storageValue: option.id) == option)
        }
        #expect(LanguageOption(storageValue: nil) == .japanese)
    }
}

@Suite("表示文字列（日本語・英語）")
struct LocalizationTests {
    init() { TestLocalization.setUp() }

    private var ja: [String: String] { Localizer.shared.table(for: .japanese) }
    private var en: [String: String] { Localizer.shared.table(for: .english) }

    @Test func bothLanguagesHaveTheSameKeys() {
        #expect(!ja.isEmpty && !en.isEmpty)
        #expect(Set(ja.keys).subtracting(en.keys).sorted() == [])
        #expect(Set(en.keys).subtracting(ja.keys).sorted() == [])
    }

    /// 書式指定（%@, %ld など）の種類と数が両言語で一致している
    @Test func formatSpecifiersMatch() {
        func specs(_ s: String) -> [String] {
            s.replacingOccurrences(of: "%%", with: "").matches(of: /%(?:\d+\$)?(?:l?d|@)/).map { m in
                String(m.output).replacingOccurrences(of: #"\d+\$"#, with: "", options: .regularExpression)
            }.sorted()
        }
        for (key, value) in ja {
            #expect(specs(value) == specs(en[key] ?? ""), "\(key)")
        }
    }

    /// ソースで使っているキーがすべて定義されている（動的に作るキーも含む）
    @Test func everyUsedKeyExists() throws {
        let sources = TestLocalization.directory.deletingLastPathComponent().appendingPathComponent("Sources")
        var used = Set<String>()
        let files = FileManager.default.enumerator(at: sources, includingPropertiesForKeys: nil)!
        while let url = files.nextObject() as? URL {
            guard url.pathExtension == "swift" else { continue }
            let text = try String(contentsOf: url, encoding: .utf8)
            for m in text.matches(of: /L\("([A-Za-z0-9_.]+)"/) { used.insert(String(m.1)) }
        }
        #expect(used.count > 150)
        for option in LanguageOption.allCases { used.insert("whisperLanguage.\(option.id)") }
        for format in ExportFormat.allCases { used.insert("exportFormat.\(format.rawValue)") }
        for model in ModelCatalog.all { used.insert("model.\(model.id).name"); used.insert("model.\(model.id).summary") }
        for engine in SpeechEngine.allCases {
            for part in ["name", "summary", "source"] { used.insert("engine.\(engine.rawValue).\(part)") }
            used.insert("models.message.\(engine.rawValue)")
        }
        #expect(used.subtracting(ja.keys).sorted() == [])
        #expect(used.subtracting(en.keys).sorted() == [])
        // 使われていないキーがない
        #expect(Set(ja.keys).subtracting(used).sorted() == [])
    }

    /// 英語の文字列に日本語が混ざっていない
    @Test func englishHasNoJapanese() {
        let japanese = /[\u{3040}-\u{30FF}\u{4E00}-\u{9FFF}\u{FF01}-\u{FF60}]/
        for (key, value) in en where value.contains(japanese) {
            Issue.record("\(key): \(value)")
        }
    }

    @Test func preferredLanguageFromSystemList() {
        #expect(AppLanguage.preferred(from: ["ja-JP", "en-US"]) == .japanese)
        #expect(AppLanguage.preferred(from: ["en-GB", "ja"]) == .english)
        #expect(AppLanguage.preferred(from: ["fr-FR", "en-US"]) == .english)
        #expect(AppLanguage.preferred(from: ["de"]) == .english)
    }

    /// 文字起こしの言語は表示言語と独立していて、英語も選べる
    @Test func transcriptionLanguageIncludesEnglish() {
        #expect(LanguageOption.english.code == "en")
        #expect(LanguageOption(storageValue: "en") == .english)
    }
}

@Suite("認識結果の区間のまとめ方")
struct SegmentMergerTests {
    private func seg(_ s: Double, _ e: Double, _ t: String) -> TranscriptSegment {
        TranscriptSegment(start: s, end: e, text: t)
    }

    /// 英語の語句は空白を保ったまま、文末で区切る
    @Test func mergesPhrasesIntoSentences() {
        let merged = SegmentMerger.merge([
            seg(0, 0.5, "This is"), seg(0.5, 1.2, " a test."), seg(1.3, 2.0, " The meeting"), seg(2.0, 2.6, " starts at 3."),
        ])
        #expect(merged == [seg(0, 1.2, "This is a test."), seg(1.3, 2.6, "The meeting starts at 3.")])
    }

    /// 間が空いたところと、長くなりすぎたところでも区切る
    @Test func splitsOnGapsAndLength() {
        let gap = SegmentMerger.merge([seg(0, 1, "あ"), seg(3, 4, "い")], maxGap: 1)
        #expect(gap.map(\.text) == ["あ", "い"])
        let long = SegmentMerger.merge([seg(0, 15, "あ"), seg(15, 30, "い")], maxDuration: 20)
        #expect(long.map(\.text) == ["あ", "い"])
    }

    /// 空白だけの断片は区間にしない
    @Test func dropsEmptyPieces() {
        #expect(SegmentMerger.merge([seg(0, 1, " "), seg(1, 2, "")]).isEmpty)
    }
}

@Suite("発話ごとの区切り")
struct UtteranceSplitterTests {
    private let rate = 1000.0 // 計算を簡単にするため 1kHz とする

    /// 音（振幅 0.5）と無音を秒数で並べた音声
    private func audio(_ parts: [(Bool, Double)]) -> [Float] {
        parts.flatMap { voiced, seconds in
            (0 ..< Int(seconds * rate)).map { i in voiced ? Float(i % 2 == 0 ? 0.5 : -0.5) : 0 }
        }
    }

    @Test func splitsAtPauses() {
        let samples = audio([(false, 0.5), (true, 2), (false, 0.5), (true, 1), (false, 0.5)])
        let r = UtteranceSplitter().split(samples, sampleRate: rate, isFinal: true)
        #expect(r.consumed == samples.count)
        #expect(r.utterances.count == 2)
        // 区切りは無音の中（中央付近）
        #expect((2700 ..< 2800).contains(r.utterances[0].upperBound))
        #expect(r.utterances[1].lowerBound == r.utterances[0].upperBound)
    }

    /// 短い無音（語の途中の詰まる音など）では区切らない
    @Test func ignoresShortGaps() {
        let samples = audio([(true, 1), (false, 0.1), (true, 1)])
        let r = UtteranceSplitter().split(samples, sampleRate: rate, isFinal: true)
        #expect(r.utterances == [0 ..< samples.count])
    }

    /// 続きがあるときは、最後の発話を次回に残す
    @Test func keepsLastUtteranceWhenNotFinal() {
        let samples = audio([(true, 2), (false, 0.5), (true, 1)])
        let r = UtteranceSplitter().split(samples, sampleRate: rate, isFinal: false)
        #expect(r.utterances.count == 1)
        #expect((2200 ..< 2300).contains(r.consumed))
    }

    /// 長すぎる発話は最大の長さ以内に分ける
    @Test func limitsUtteranceLength() {
        let samples = audio([(true, 25)])
        let r = UtteranceSplitter(maxUtterance: 10).split(samples, sampleRate: rate, isFinal: true)
        #expect(r.utterances.count >= 3)
        #expect(r.utterances.allSatisfy { $0.count <= 10_000 })
        #expect(r.utterances.first?.lowerBound == 0 && r.utterances.last?.upperBound == samples.count)
    }

    /// 無音だけなら何も返さない
    @Test func skipsSilence() {
        let r = UtteranceSplitter().split(audio([(false, 3)]), sampleRate: rate, isFinal: true)
        #expect(r.utterances.isEmpty)
    }
}
