import Foundation
import XCTest

@testable import MeterCore

final class UnloadTests: XCTestCase {
    func testFirstPressOnlyArms() {
        let press = UnloadState.ready.pressed("qwen")
        XCTAssertEqual(press.next, .confirming(model: "qwen"))
        XCTAssertFalse(press.sendsUnload)
    }

    func testSecondPressSendsTheUnload() {
        let press = UnloadState.confirming(model: "qwen").pressed("qwen")
        XCTAssertEqual(press.next, .unloading(model: "qwen"))
        XCTAssertTrue(press.sendsUnload)
    }

    func testArmingOneModelDoesNotConfirmAnother() {
        let press = UnloadState.confirming(model: "qwen").pressed("llama")
        XCTAssertEqual(press.next, .confirming(model: "llama"))
        XCTAssertFalse(press.sendsUnload)
    }

    func testPressDuringAnUnloadDoesNothing() {
        let press = UnloadState.unloading(model: "qwen").pressed("qwen")
        XCTAssertEqual(press.next, .unloading(model: "qwen"))
        XCTAssertFalse(press.sendsUnload)
    }

    func testRetryAfterAFailureStillNeedsConfirming() {
        let press = UnloadState.failed(model: "qwen").pressed("qwen")
        XCTAssertEqual(press.next, .confirming(model: "qwen"))
        XCTAssertFalse(press.sendsUnload)
    }

    func testBodyAsksForAnImmediateUnloadAndNoGeneration() throws {
        let data = try XCTUnwrap(OllamaJSON.unloadBody(model: "qwen:latest"))
        let body = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        XCTAssertEqual(body["model"] as? String, "qwen:latest")
        XCTAssertEqual(body["keep_alive"] as? Int, 0)
        XCTAssertNil(body["prompt"])
    }

    func testWorkingCoversTheGapsBetweenRequests() {
        var model = MeterViewModel()
        model.snapshot.phase = .idle
        XCTAssertFalse(model.isWorking)
        model.display.displayPhase = .generating
        XCTAssertTrue(model.isWorking)
        model.display.displayPhase = .idle
        model.snapshot.phase = .prefill
        XCTAssertTrue(model.isWorking)
    }
}

/// Fixture lines are copied from a real Ollama 0.40.15 server.log.
final class LogParserTests: XCTestCase {
    func testNewPrompt() {
        XCTAssertEqual(
            LogParser.parse(
                "slot   operator(): id  0 | task 18484 | new prompt, n_ctx_slot = 262144, n_keep = 4, task.n_tokens = 57451"),
            .newPrompt(task: 18484, contextSize: 262144, promptTokens: 57451))
    }

    func testPromptProgress() {
        XCTAssertEqual(
            LogParser.parse(
                "slot print_timing: id  0 | task 447 | prompt processing, n_tokens =   2048, progress = 0.08, t =   4.80 s / 427.01 tokens per second"),
            .promptProgress(task: 447, tokens: 2048, progress: 0.08, tps: 427.01))
    }

    func testGenerationProgress() {
        XCTAssertEqual(
            LogParser.parse("slot print_timing: id  0 | task 12050 | n_gen =    100, tg =  27.70 t/s, tg_3s =  27.98 t/s"),
            .generationProgress(task: 12050, tokens: 100, tps: 27.70, recentTPS: 27.98))
    }

    func testFinalTimings() {
        XCTAssertEqual(
            LogParser.parse(
                "slot print_timing: id  0 | task 18484 | prompt eval time =     265.07 ms /    25 tokens (   10.60 ms per token,    94.32 tokens per second)"),
            .promptEval(task: 18484, milliseconds: 265.07, tokens: 25, tps: 94.32))
        XCTAssertEqual(
            LogParser.parse(
                "slot print_timing: id  0 | task 18484 |        eval time =   11745.47 ms /   321 tokens (   36.70 ms per token,    27.24 tokens per second)"),
            .eval(task: 18484, milliseconds: 11745.47, tokens: 321, tps: 27.24))
        XCTAssertEqual(
            LogParser.parse("slot print_timing: id  0 | task 18484 |       total time =   12010.54 ms /   346 tokens"),
            .total(task: 18484, milliseconds: 12010.54, tokens: 346))
    }

    func testLifecycleLines() {
        XCTAssertEqual(
            LogParser.parse("slot      release: id  0 | task 18484 | stop processing: n_tokens = 57771, truncated = 0"),
            .released(task: 18484))
        XCTAssertEqual(LogParser.parse("srv  update_slots: all slots are idle"), .allIdle)
        XCTAssertEqual(LogParser.parse("srv          stop: cancel task, id_task = 32026"), .cancelled(task: 32026))
        XCTAssertEqual(
            LogParser.parse("slot   operator(): id  0 | task 21 | slot context shift, n_keep = 4, n_left = 8187, n_discard = 4093"),
            .contextShift(task: 21))
        XCTAssertEqual(
            LogParser.parse(
                "slot   operator(): id  0 | task 5 | forcing full prompt re-processing due to lack of cache data (likely due to SWA or hybrid/recurrent memory)"),
            .fullReprocess(task: 5))
    }

    func testRuntimeLines() {
        XCTAssertEqual(
            LogParser.parse(
                #"time=2026-11-07T22:32:39.750+00:00 level=INFO source=llama_server.go:1351 msg="waiting for llama-server to start responding""#),
            .modelLoading)
        XCTAssertEqual(
            LogParser.parse(
                #"time=2026-11-07T22:32:40.255+00:00 level=INFO source=llama_server.go:1418 msg="llama-server started in 0.50 seconds""#),
            .modelLoaded)
        XCTAssertEqual(
            LogParser.parse(
                #"time=2026-11-07T22:49:31.505+00:00 level=WARN source=llama_server.go:344 msg="truncating input prompt" limit=4098 prompt=23010 keep=4 new=4098"#),
            .inputTruncated(limit: 4098, promptTokens: 23010))
    }

    func testPromptCacheLines() {
        XCTAssertEqual(
            LogParser.parse("srv    load_model: prompt cache is enabled, size limit: 24576 MiB"),
            .promptCacheEnabled(limitMiB: 24576))
        XCTAssertEqual(
            LogParser.parse("srv    load_model: prompt cache is enabled, size limit: no limit"),
            .promptCacheEnabled(limitMiB: nil))
        XCTAssertEqual(
            LogParser.parse("srv    load_model: prompt cache is disabled - use `--cache-ram N` to enable it"),
            .promptCacheDisabled)
        XCTAssertEqual(
            LogParser.parse("srv        update:  - cache state: 3 prompts, 4171.081 MiB (limits: 8192.000 MiB, 262144 tokens, 262144 est)"),
            .promptCacheState(savedPrompts: 3, usedMiB: 4171.081, limitMiB: 8192))
        XCTAssertEqual(
            LogParser.parse("srv        update:    - prompt 0x7644b45730:   10327 tokens, checkpoints: 18,  2441.874 MiB"),
            .promptCacheEntry(tokens: 10327))
        XCTAssertEqual(
            LogParser.parse("srv         alloc:  - making room for prompt cache entry, removing oldest entry (size = 7814.445 MiB)"),
            .promptCacheEvicted(sizeMiB: 7814.445))
        XCTAssertNil(LogParser.parse("srv  get_availabl: updating prompt cache"))
        XCTAssertNil(LogParser.parse("srv  get_availabl: prompt cache update took 223.15 ms"))
    }

    func testIgnoresUnrelatedLines() {
        XCTAssertNil(LogParser.parse(#"[GIN] 2026/11/08 - 13:36:30 | 200 |    1.61s |       127.0.0.1 | POST     "/v1/chat/completions""#))
        XCTAssertNil(LogParser.parse("slot get_availabl: id  0 | task -1 | selected slot by LRU, n_tokens = 10, t_last = 1"))
        XCTAssertNil(LogParser.parse("<|im_start|>user"))
    }
}

final class RequestTrackerTests: XCTestCase {
    private func finishedRequest(
        task: Int, prompt: Int, evaluated: Int, promptMs: Double, promptTPS: Double,
        generated: Int, generationMs: Double, generationTPS: Double
    ) -> [LogEvent] {
        [
            .newPrompt(task: task, contextSize: 262144, promptTokens: prompt),
            .promptEval(task: task, milliseconds: promptMs, tokens: evaluated, tps: promptTPS),
            .eval(task: task, milliseconds: generationMs, tokens: generated, tps: generationTPS),
            .total(task: task, milliseconds: promptMs + generationMs, tokens: evaluated + generated),
            .released(task: task),
            .allIdle,
        ]
    }

    func testFinishedRequestUpdatesStats() {
        var tracker = RequestTracker()
        let events = finishedRequest(
            task: 1, prompt: 1000, evaluated: 200, promptMs: 1000, promptTPS: 200,
            generated: 100, generationMs: 4000, generationTPS: 25)
        for event in events { tracker.apply(event, at: Date()) }

        XCTAssertTrue(tracker.active.isEmpty)
        XCTAssertEqual(tracker.recent.count, 1)
        XCTAssertEqual(tracker.recent[0].cachedTokens, 800)
        XCTAssertEqual(tracker.stats.requests, 1)
        XCTAssertEqual(tracker.stats.cacheFraction, 0.8)
        XCTAssertEqual(tracker.stats.promptTimeFraction, 0.2)
        XCTAssertEqual(tracker.stats.prompt.average, 200)
        XCTAssertEqual(tracker.stats.generation.peak, 25)
    }

    func testTinyRequestsDoNotSetSpeedRecords() {
        var tracker = RequestTracker()
        let events = finishedRequest(
            task: 1, prompt: 1000, evaluated: 25, promptMs: 265, promptTPS: 94,
            generated: 5, generationMs: 37, generationTPS: 108)
        for event in events { tracker.apply(event, at: Date()) }

        XCTAssertEqual(tracker.stats.requests, 1)
        XCTAssertNil(tracker.stats.prompt.peak)
        XCTAssertNil(tracker.stats.generation.peak)
    }

    func testCancelledRequestIsNotRecorded() {
        var tracker = RequestTracker()
        tracker.apply(.newPrompt(task: 9, contextSize: 100, promptTokens: 50), at: Date())
        tracker.apply(.cancelled(task: 9), at: Date())
        XCTAssertTrue(tracker.active.isEmpty)
        XCTAssertTrue(tracker.recent.isEmpty)
    }

    func testModelLoadResetsSession() {
        var tracker = RequestTracker()
        let events = finishedRequest(
            task: 1, prompt: 1000, evaluated: 200, promptMs: 1000, promptTPS: 200,
            generated: 100, generationMs: 4000, generationTPS: 25)
        for event in events { tracker.apply(event, at: Date()) }
        tracker.apply(.modelLoaded, at: Date())
        XCTAssertEqual(tracker.stats.requests, 0)
        XCTAssertTrue(tracker.recent.isEmpty)
    }

    func testPromptCacheFollowsTheLog() throws {
        var tracker = RequestTracker()
        tracker.apply(.promptCacheEnabled(limitMiB: 24576), at: Date())
        tracker.apply(.modelLoaded, at: Date())
        var cache = try XCTUnwrap(tracker.promptCache)
        XCTAssertEqual(cache.limitBytes, 25_769_803_776)
        XCTAssertEqual(cache.usedBytes, 0)
        XCTAssertEqual(cache.fraction, 0)

        tracker.apply(.promptCacheState(savedPrompts: 2, usedMiB: 6144, limitMiB: 24576), at: Date())
        tracker.apply(.promptCacheEntry(tokens: 10_327), at: Date())
        tracker.apply(.promptCacheEntry(tokens: 43_238), at: Date())
        cache = try XCTUnwrap(tracker.promptCache)
        XCTAssertEqual(cache.fraction, 0.25)
        XCTAssertEqual(cache.savedPrompts, 2)
        XCTAssertEqual(cache.savedTokens, [43_238, 10_327])

        tracker.apply(.promptCacheEvicted(sizeMiB: 7814.445), at: Date())
        tracker.apply(.promptCacheState(savedPrompts: 1, usedMiB: 3072, limitMiB: 24576), at: Date())
        tracker.apply(.promptCacheEntry(tokens: 43_238), at: Date())
        cache = try XCTUnwrap(tracker.promptCache)
        XCTAssertEqual(cache.savedTokens, [43_238])
        XCTAssertEqual(cache.evictions, 1)
        XCTAssertEqual(tracker.notices.map(\.id), ["cache-evicted"])
    }

    func testNewLoadStartsWithAnEmptyCache() throws {
        var tracker = RequestTracker()
        tracker.apply(.promptCacheEnabled(limitMiB: 8192), at: nil)
        tracker.apply(.promptCacheEvicted(sizeMiB: 100), at: nil)
        tracker.apply(.promptCacheState(savedPrompts: 1, usedMiB: 100, limitMiB: 8192), at: nil)
        XCTAssertTrue(tracker.notices.isEmpty)
        XCTAssertEqual(tracker.promptCache?.evictions, 1)

        tracker.apply(.promptCacheEnabled(limitMiB: 24576), at: Date())
        XCTAssertEqual(tracker.promptCache, PromptCacheState(limitBytes: 25_769_803_776))

        tracker.apply(.promptCacheDisabled, at: Date())
        XCTAssertNil(tracker.promptCache)
    }

    func testBacklogProducesNoNoticesOrLoadingState() {
        var tracker = RequestTracker()
        tracker.apply(.contextShift(task: 1), at: nil)
        tracker.apply(.modelLoading, at: nil)
        XCTAssertTrue(tracker.notices.isEmpty)
        XCTAssertFalse(tracker.isLoadingModel(at: Date()))
    }
}

final class SlidingRateTests: XCTestCase {
    func testRateOverWindow() {
        var rate = SlidingRate(window: 3)
        let start = Date()
        for index in 0...20 {
            rate.add(index * 7, at: start.addingTimeInterval(Double(index) * 0.25))
        }
        XCTAssertEqual(rate.rate ?? 0, 28, accuracy: 0.01)
    }

    func testNeedsMinimumSpan() {
        var rate = SlidingRate(window: 3)
        let start = Date()
        rate.add(0, at: start)
        rate.add(7, at: start.addingTimeInterval(0.25))
        XCTAssertNil(rate.rate)
    }

    func testCountGoingBackwardRestarts() {
        var rate = SlidingRate(window: 3)
        let start = Date()
        rate.add(500, at: start)
        rate.add(0, at: start.addingTimeInterval(1))
        XCTAssertNil(rate.rate)
    }
}

final class MeterEngineTests: XCTestCase {
    private func slot(processing: Bool = true, task: Int = 7, prompt: Int, processed: Int, cached: Int, decoded: Int)
        -> SlotReading
    {
        SlotReading(
            isProcessing: processing, task: task, contextSize: 262144, promptTokens: prompt,
            processedPromptTokens: processed, cachedPromptTokens: cached, decodedTokens: decoded)
    }

    func testOffline() {
        XCTAssertEqual(MeterEngine().snapshot(at: Date(), online: false).phase, .offline)
    }

    private func newPromptLine(total: Int) -> String {
        "slot   operator(): id  0 | task 7 | new prompt, n_ctx_slot = 262144, n_keep = 4, task.n_tokens = \(total)"
    }

    func testPrefillMatchesLogProgressWithACachedPrefix() {
        // Task 635 from a real log: 24,457 tokens, 8,472 of them cached.
        let engine = MeterEngine()
        let now = Date()
        engine.ingest(line: newPromptLine(total: 24_457), at: now)
        engine.ingest(line: "slot print_timing: id  0 | task 7 | prompt processing, n_tokens =   2049, progress = 0.43, t =   4.00 s / 512.25 tokens per second", at: now)
        engine.update(slots: [slot(prompt: 11_033, processed: 2_049, cached: 8_472, decoded: 0)], at: now)
        let snapshot = engine.snapshot(at: now, online: true)
        XCTAssertEqual(snapshot.phase, .prefill)
        XCTAssertEqual(snapshot.promptFraction ?? 0, 0.43, accuracy: 0.005)
        XCTAssertEqual(snapshot.promptTokens, 24_457)
        XCTAssertEqual(snapshot.promptDone, 2_049)
        XCTAssertEqual(snapshot.promptToProcess, 15_985)
        XCTAssertEqual(snapshot.etaSeconds ?? 0, Double(15_985 - 2_049) / 512.25, accuracy: 0.001)
    }

    func testPrefillDoesNotTreatTheGrowingSlotCountAsThePromptSize() {
        // A real cold prompt two batches in: the slot holds 1,024 of the 5,756 tokens.
        let engine = MeterEngine()
        let now = Date()
        engine.ingest(line: newPromptLine(total: 5_756), at: now)
        engine.update(slots: [slot(prompt: 1_024, processed: 512, cached: 0, decoded: 0)], at: now)
        let snapshot = engine.snapshot(at: now, online: true)
        XCTAssertEqual(snapshot.promptFraction ?? 0, 512.0 / 5_756, accuracy: 0.0001)
        XCTAssertEqual(snapshot.promptToProcess, 5_756)
    }

    func testPrefillUsesTheLogCountWhenItIsAhead() {
        let engine = MeterEngine()
        let now = Date()
        engine.ingest(line: newPromptLine(total: 50_000), at: now)
        engine.ingest(line: "slot print_timing: id  0 | task 7 | prompt processing, n_tokens =   5000, progress = 0.90, t =   10.00 s / 500.00 tokens per second", at: now)
        engine.update(slots: [slot(prompt: 45_512, processed: 1_000, cached: 40_000, decoded: 0)], at: now)
        let snapshot = engine.snapshot(at: now, online: true)
        XCTAssertEqual(snapshot.promptDone, 5_000)
        XCTAssertEqual(snapshot.promptFraction, 0.9)
        XCTAssertEqual(snapshot.promptTPS, 500)
    }

    func testPrefillWithoutThePromptSizeShowsNoPercentage() {
        let engine = MeterEngine()
        let now = Date()
        engine.update(slots: [slot(prompt: 1_024, processed: 512, cached: 0, decoded: 0)], at: now)
        let snapshot = engine.snapshot(at: now, online: true)
        XCTAssertEqual(snapshot.phase, .prefill)
        XCTAssertNil(snapshot.promptFraction)
        XCTAssertNil(snapshot.etaSeconds)
    }

    func testLogOnlyPrefillUsesTheLogsProgress() {
        let engine = MeterEngine()
        let now = Date()
        engine.update(slots: nil, at: now)
        engine.ingest(line: newPromptLine(total: 24_457), at: now)
        engine.ingest(line: "slot print_timing: id  0 | task 7 | prompt processing, n_tokens =   2049, progress = 0.43, t =   4.00 s / 512.25 tokens per second", at: now)
        let snapshot = engine.snapshot(at: now, online: true)
        XCTAssertEqual(snapshot.promptFraction, 0.43)
        XCTAssertEqual(snapshot.promptDone, 2_049)
        XCTAssertEqual(snapshot.promptToProcess, 2_049 + 13_940)
    }

    func testLiveRateFromSlots() {
        let engine = MeterEngine()
        let start = Date()
        for index in 0...12 {
            engine.update(
                slots: [slot(prompt: 1000 + index * 7, processed: 25, cached: 975, decoded: 1 + index * 7)],
                at: start.addingTimeInterval(Double(index) * 0.25))
        }
        let snapshot = engine.snapshot(at: start.addingTimeInterval(3), online: true)
        XCTAssertEqual(snapshot.phase, .generating)
        XCTAssertEqual(snapshot.liveTPS ?? 0, 28, accuracy: 0.01)
        XCTAssertEqual(snapshot.contextUsed, 1084)
    }

    func testNewTaskRestartsRate() {
        let engine = MeterEngine()
        let start = Date()
        engine.update(slots: [slot(task: 1, prompt: 1000, processed: 5, cached: 995, decoded: 100)], at: start)
        engine.update(slots: [slot(task: 2, prompt: 1100, processed: 5, cached: 1095, decoded: 2)], at: start.addingTimeInterval(1))
        XCTAssertNil(engine.snapshot(at: start.addingTimeInterval(1), online: true).liveTPS)
    }

    func testFallsBackToLogWithoutSlots() {
        let engine = MeterEngine()
        let now = Date()
        engine.update(slots: nil, at: now)
        engine.ingest(line: "slot   operator(): id  0 | task 7 | new prompt, n_ctx_slot = 1000, n_keep = 4, task.n_tokens = 400", at: now)
        engine.ingest(line: "slot print_timing: id  0 | task 7 | n_gen =    100, tg =  27.70 t/s, tg_3s =  27.98 t/s", at: now)
        let snapshot = engine.snapshot(at: now, online: true)
        XCTAssertEqual(snapshot.phase, .generating)
        XCTAssertEqual(snapshot.liveTPS, 27.98)
    }

    func testBacklogRequestIsNotTreatedAsLive() {
        let engine = MeterEngine()
        engine.update(slots: nil, at: Date())
        engine.ingest(line: "slot   operator(): id  0 | task 7 | new prompt, n_ctx_slot = 1000, n_keep = 4, task.n_tokens = 400", at: nil)
        XCTAssertEqual(engine.snapshot(at: Date(), online: true).phase, .idle)
    }

    func testIdleKeepsContextUsage() {
        let engine = MeterEngine()
        let now = Date()
        engine.update(slots: [slot(processing: false, prompt: 54_287, processed: 0, cached: 0, decoded: 0)], at: now)
        let snapshot = engine.snapshot(at: now, online: true)
        XCTAssertEqual(snapshot.phase, .idle)
        XCTAssertEqual(snapshot.contextUsed, 54_287)
    }
}

final class DisplayPresenterTests: XCTestCase {
    private func model(_ phase: Phase, at now: Date, configure: (inout LiveSnapshot) -> Void = { _ in }) -> MeterViewModel {
        var model = MeterViewModel()
        model.now = now
        model.snapshot.phase = phase
        model.hardware.gpuUtilization = 97
        configure(&model.snapshot)
        return model
    }

    func testGeneratingShowsSpeedAndGPU() {
        var presenter = DisplayPresenter()
        let result = presenter.present(model(.generating, at: Date()) { $0.liveTPS = 27.4 })
        XCTAssertEqual(result.text, "27 t/s")
        XCTAssertEqual(result.fraction, 0.97)
        XCTAssertEqual(result.tint, .gpu)
    }

    func testLargePrefillShowsProgressImmediately() {
        var presenter = DisplayPresenter()
        let result = presenter.present(
            model(.prefill, at: Date()) {
                $0.task = 3
                $0.promptToProcess = 10_000
                $0.promptFraction = 0.41
            })
        XCTAssertEqual(result.text, "41%")
        XCTAssertEqual(result.fraction, 0.41)
        XCTAssertEqual(result.tint, .prompt)
    }

    func testShortPrefillKeepsLastSpeedInsteadOfFlickering() {
        var presenter = DisplayPresenter()
        let start = Date()
        _ = presenter.present(model(.generating, at: start) { $0.liveTPS = 27.4 })
        let result = presenter.present(
            model(.prefill, at: start.addingTimeInterval(0.2)) {
                $0.task = 4
                $0.promptToProcess = 25
                $0.promptFraction = 0.5
            })
        XCTAssertEqual(result.text, "27 t/s")
        XCTAssertTrue(result.textDimmed)
    }

    func testSlowSmallPrefillEventuallyShowsProgress() {
        var presenter = DisplayPresenter()
        let start = Date()
        let configure: (inout LiveSnapshot) -> Void = {
            $0.task = 4
            $0.promptToProcess = 200
            $0.promptFraction = 0.5
        }
        _ = presenter.present(model(.prefill, at: start, configure: configure))
        let result = presenter.present(model(.prefill, at: start.addingTimeInterval(1), configure: configure))
        XCTAssertEqual(result.text, "50%")
    }

    func testIdleHoldsThenClearsSpeed() {
        var presenter = DisplayPresenter()
        let start = Date()
        _ = presenter.present(model(.generating, at: start) { $0.liveTPS = 27.4 })
        let held = presenter.present(model(.idle, at: start.addingTimeInterval(2)))
        XCTAssertEqual(held.text, "27 t/s")
        XCTAssertTrue(held.textDimmed)
        XCTAssertEqual(held.tint, .muted)
        XCTAssertEqual(held.displayPhase, .generating)
        XCTAssertEqual(held.heldTPS, 27.4)
        let expired = presenter.present(model(.idle, at: start.addingTimeInterval(6)))
        XCTAssertNil(expired.text)
        XCTAssertEqual(expired.displayPhase, .idle)
        XCTAssertNil(expired.heldTPS)
    }

    func testOfflineClearsGauge() {
        var presenter = DisplayPresenter()
        let result = presenter.present(model(.offline, at: Date()))
        XCTAssertEqual(result.fraction, 0)
        XCTAssertEqual(result.tint, .none)
        XCTAssertNil(result.text)
    }
}

final class FormatTests: XCTestCase {
    func testCompactTokens() {
        XCTAssertEqual(Format.compactTokens(950), "950")
        XCTAssertEqual(Format.compactTokens(262_144), "262k")
        XCTAssertEqual(Format.compactTokens(7_689_000), "7.7M")
    }

    func testPercentNeverOverstatesProgress() {
        XCTAssertEqual(Format.percent(0.999), "99%")
        XCTAssertEqual(Format.percent(1), "100%")
    }

    func testSpeeds() {
        XCTAssertEqual(Format.tps(27.44), "27.4")
        XCTAssertEqual(Format.tps(301.2), "301")
        XCTAssertEqual(Format.menuTPS(27.4), "27 t/s")
        XCTAssertEqual(Format.menuTPS(8.44), "8.4 t/s")
        XCTAssertEqual(Format.menuTPS(1234), "1.2k t/s")
    }

    func testDurations() {
        XCTAssertEqual(Format.milliseconds(265.07), "265 ms")
        XCTAssertEqual(Format.milliseconds(2_100), "2.1 s")
        XCTAssertEqual(Format.milliseconds(95_000), "1m 35s")
    }
}

final class OllamaJSONTests: XCTestCase {
    func testSlotsWithArrayNextToken() throws {
        let json = #"[{"id":0,"n_ctx":262144,"is_processing":true,"id_task":12015,"n_prompt_tokens":48185,"n_prompt_tokens_processed":30,"n_prompt_tokens_cache":48135,"next_token":[{"has_next_token":true,"n_decoded":99}]}]"#
        let slots = try XCTUnwrap(OllamaJSON.slots(from: Data(json.utf8)))
        XCTAssertEqual(
            slots,
            [
                SlotReading(
                    isProcessing: true, task: 12015, contextSize: 262144, promptTokens: 48185,
                    processedPromptTokens: 30, cachedPromptTokens: 48135, decodedTokens: 99)
            ])
    }

    func testSlotsWithObjectNextToken() throws {
        let json = #"[{"is_processing":false,"id_task":1,"next_token":{"n_decoded":4}}]"#
        XCTAssertEqual(try XCTUnwrap(OllamaJSON.slots(from: Data(json.utf8)))[0].decodedTokens, 4)
    }

    func testRejectsErrorBody() {
        XCTAssertNil(OllamaJSON.slots(from: Data(#"{"error":{"code":501}}"#.utf8)))
    }

    func testModels() throws {
        let json = #"{"models":[{"name":"qwen:latest","size":47023736749,"details":{"parameter_size":"176.9B","quantization_level":"IQ4_XS"},"expires_at":"2026-11-08T13:40:07.774411+02:00","size_vram":47023736749,"context_length":262144,"runner":"llamacpp"}]}"#
        let model = try XCTUnwrap(OllamaJSON.models(from: Data(json.utf8))?.first)
        XCTAssertEqual(model.name, "qwen:latest")
        XCTAssertEqual(model.sizeVRAM, 47_023_736_749)
        XCTAssertEqual(model.contextLength, 262144)
        XCTAssertEqual(model.quantization, "IQ4_XS")
        XCTAssertEqual(model.expiresAt, ISO8601DateFormatter().date(from: "2026-11-08T11:40:07Z"))
    }
}

final class LogTailerTests: XCTestCase {
    private var directory: URL!
    private var file: URL!

    override func setUpWithError() throws {
        directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        file = directory.appendingPathComponent("server.log")
    }

    private func append(_ text: String) throws {
        if !FileManager.default.fileExists(atPath: file.path) {
            FileManager.default.createFile(atPath: file.path, contents: nil)
        }
        let handle = try FileHandle(forWritingTo: file)
        try handle.seekToEnd()
        try handle.write(contentsOf: Data(text.utf8))
        try handle.close()
    }

    func testBacklogDropsCutOffFirstLine() throws {
        try append("first line here\nsecond\nthird\n")
        let tailer = LogTailer(path: file.path)
        XCTAssertEqual(tailer.openAtEnd(backlogBytes: 15), ["second", "third"])
    }

    func testHoldsBackUnfinishedLine() throws {
        try append("start\n")
        let tailer = LogTailer(path: file.path)
        _ = tailer.openAtEnd(backlogBytes: 0)
        try append("par")
        XCTAssertEqual(tailer.readNew(), [])
        try append("tial\nnext\n")
        XCTAssertEqual(tailer.readNew(), ["partial", "next"])
    }

    func testFollowsRotation() throws {
        try append("old\n")
        let tailer = LogTailer(path: file.path)
        _ = tailer.openAtEnd(backlogBytes: 0)
        try FileManager.default.moveItem(at: file, to: directory.appendingPathComponent("server-1.log"))
        try append("new\n")
        XCTAssertEqual(tailer.readNew(), ["new"])
    }

    func testMissingFileIsHarmless() {
        let tailer = LogTailer(path: directory.appendingPathComponent("absent.log").path)
        XCTAssertEqual(tailer.openAtEnd(backlogBytes: 100), [])
        XCTAssertEqual(tailer.readNew(), [])
    }
}

final class SharingTests: XCTestCase {
    private func sampleModel() -> MeterViewModel {
        var model = MeterViewModel()
        // Whole seconds, so dates survive the trip exactly.
        model.now = Date(timeIntervalSince1970: 1_800_000_000)
        model.snapshot.phase = .generating
        model.snapshot.liveTPS = 27.4
        model.snapshot.generatedTokens = 321
        model.version = "0.40.16"
        model.models = [
            LoadedModel(
                name: "llama3.2:3b", size: 2_000_000_000, sizeVRAM: 2_000_000_000, contextLength: 8192,
                expiresAt: Date(timeIntervalSince1970: 1_800_000_300), parameterSize: "3.2B", quantization: "Q4_K_M")
        ]
        var stats = SessionStats()
        for index in 0..<14 {
            let request = CompletedRequest(
                sequence: index, task: 100 + index, finishedAt: Date(timeIntervalSince1970: 1_799_999_000 + Double(index)),
                promptTokens: 4_000, evaluatedTokens: 300, promptMilliseconds: 900, promptTPS: 333,
                generatedTokens: 200, generationMilliseconds: 7_000, generationTPS: 28.5)
            stats.record(request)
            model.requests.insert(request, at: 0)
        }
        model.stats = stats
        model.notices = [Notice(id: "n", severity: .serious, text: "A notice.", at: model.now)]
        model.hardware.gpuUtilization = 97
        model.hardware.memoryPressure = .warning
        model.runner = RunnerStats(cpuPercent: 12, residentBytes: 3_000_000_000)
        model.promptCache = PromptCacheState(limitBytes: 1 << 30, usedBytes: 1 << 29, savedPrompts: 2, savedTokens: [9, 8])
        model.display = DisplayState(
            fraction: 0.97, tint: .gpu, modelLoaded: true, text: "27 t/s", textDimmed: false,
            summary: "Generating", displayPhase: .generating, heldTPS: 27)
        model.tpsHistory = [0, 26.5, 27.4]
        model.gpuHistory = [3, 97]
        return model
    }

    func testFrameRoundTripKeepsEverythingShown() throws {
        var model = sampleModel()
        model.unload = .confirming(model: "llama3.2:3b")
        model.remote = RemoteSource(host: "elsewhere", status: .connected)
        let data = try XCTUnwrap(SharedFrame(host: "studio", app: "0.2.0", model: model).encoded())
        guard case .success(let frame) = SharedFrame.decode(data) else { return XCTFail("didn't decode") }

        XCTAssertEqual(frame.host, "studio")
        XCTAssertEqual(frame.app, "0.2.0")
        var expected = model
        expected.requests = Array(model.requests.prefix(SharedFrame.requestLimit))
        // The receiving Mac's own button and source aren't the sender's to set.
        expected.unload = .ready
        expected.remote = nil
        XCTAssertEqual(frame.model, expected)
        XCTAssertEqual(frame.model.stats.generation.average, model.stats.generation.average)
        XCTAssertEqual(frame.model.stats.firstTokenAverage, model.stats.firstTokenAverage)
    }

    func testFrameIsOneLineForTheEventStream() throws {
        var model = sampleModel()
        model.notices = [Notice(id: "n", severity: .warning, text: "Two\nlines", at: model.now)]
        let data = try XCTUnwrap(SharedFrame(host: "studio", app: nil, model: model).encoded())
        XCTAssertFalse(data.contains(0x0A))
        XCTAssertFalse(data.contains(0x0D))
    }

    func testInfiniteSpeedStillEncodes() throws {
        var model = sampleModel()
        model.snapshot.promptTPS = .infinity
        let data = try XCTUnwrap(SharedFrame(host: "studio", app: nil, model: model).encoded())
        guard case .success(let frame) = SharedFrame.decode(data) else { return XCTFail("didn't decode") }
        XCTAssertEqual(frame.model.snapshot.promptTPS, .infinity)
    }

    func testOtherFormatIsReportedWithItsVersion() {
        let data = Data(#"{"format":2,"app":"0.9.0","host":"studio"}"#.utf8)
        XCTAssertEqual(SharedFrame.decode(data), .failure(.otherFormat(format: 2, app: "0.9.0")))
        XCTAssertEqual(SharedFrame.decode(Data("nonsense".utf8)), .failure(.malformed))
        XCTAssertEqual(SharedFrame.decode(Data(#"{"format":1,"host":"studio"}"#.utf8)), .failure(.malformed))
    }

    func testPlaceholderNamesTheProblem() {
        let model = MeterViewModel.placeholder(for: RemoteSource(host: "studio", status: .unreachable))
        XCTAssertEqual(model.display.displayPhase, .offline)
        XCTAssertEqual(model.display.summary, "Can't reach studio. Trying again…")
        XCTAssertEqual(model.remote?.host, "studio")
        XCTAssertNil(RemoteSource(host: "studio", status: .connected).problem)
    }

    func testAddresses() {
        XCTAssertEqual(MeterSource.address("studio.local"), .address(host: "studio.local", port: 11480))
        XCTAssertEqual(MeterSource.address(" 192.168.1.20:9000 "), .address(host: "192.168.1.20", port: 9000))
        XCTAssertEqual(MeterSource.address("http://studio.local:9000/v1/state"), .address(host: "studio.local", port: 9000))
        XCTAssertEqual(MeterSource.address("[fe80::1]:9000"), .address(host: "fe80::1", port: 9000))
        XCTAssertEqual(MeterSource.address("fe80::1"), .address(host: "fe80::1", port: 11480))
        XCTAssertNil(MeterSource.address(""))
        XCTAssertNil(MeterSource.address("studio local"))
        XCTAssertNil(MeterSource.address("studio:0"))
        XCTAssertNil(MeterSource.address("studio:99999"))
        XCTAssertNil(MeterSource.address(":9000"))
    }

    func testSourcesSurviveStorage() {
        let sources: [MeterSource] = [
            .local, .service(name: "Studio (2)"), .address(host: "studio.local", port: 11480),
            .address(host: "10.0.0.2", port: 9000), .address(host: "fe80::1", port: 9000),
        ]
        for source in sources {
            XCTAssertEqual(MeterSource(storageValue: source.storageValue), source, source.storageValue)
        }
        XCTAssertNil(MeterSource(storageValue: "service:"))
        XCTAssertNil(MeterSource(storageValue: "bogus"))
        XCTAssertEqual(MeterSource.address(host: "studio", port: 11480).displayName, "studio")
        XCTAssertEqual(MeterSource.address(host: "studio", port: 9000).displayName, "studio:9000")
    }

    func testRequestHead() {
        XCTAssertEqual(HTTPRequestHead.parse(Data("GET /v1/state HTTP/1.1\r\nHost: x\r\n".utf8)), .incomplete)
        XCTAssertEqual(
            HTTPRequestHead.parse(Data("GET /v1/events?x=1 HTTP/1.1\r\nHost: x\r\n\r\n".utf8)),
            .complete(HTTPRequestHead(method: "GET", path: "/v1/events")))
        XCTAssertEqual(HTTPRequestHead.parse(Data("hello there\r\n\r\n".utf8)), .malformed)
        XCTAssertEqual(HTTPRequestHead.parse(Data(repeating: 0x41, count: HTTPRequestHead.sizeLimit + 1)), .malformed)
    }

    func testEventStreamAcrossChunks() throws {
        let response = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\n\r\n"
            + "data: {\"a\":1}\n\n: a comment\n\ndata: {\"b\":2}\r\n\r\ndata: first\ndata: second\n\ndata: {\"c\""
        var parser = EventStreamParser()
        var events: [String] = []
        // One byte at a time, so every boundary lands mid-chunk somewhere.
        for byte in Data(response.utf8) {
            events += try parser.append(Data([byte]))
        }
        XCTAssertEqual(events, [#"{"a":1}"#, #"{"b":2}"#, "first\nsecond"])
        XCTAssertEqual(try parser.append(Data(":3}\n\n".utf8)), [#"{"c":3}"#])
    }

    func testEventStreamRejectsErrors() {
        var parser = EventStreamParser()
        XCTAssertThrowsError(try parser.append(Data("HTTP/1.1 404 Not Found\r\n\r\n".utf8))) { error in
            XCTAssertEqual(error as? EventStreamParser.Failure, .status(404))
        }
        var garbage = EventStreamParser()
        XCTAssertThrowsError(try garbage.append(Data("SSH-2.0-OpenSSH\r\n\r\n".utf8)))
    }
}
