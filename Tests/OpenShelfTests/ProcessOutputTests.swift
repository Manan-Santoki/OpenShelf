import Foundation
import Testing
@testable import OpenShelf

/// A tool's last words are never lost: what it writes just before exiting is
/// read before its exit is acted on.
struct ProcessOutputTests {
    /// Under load, as in a full test run, where afconvert's message was lost.
    @Test func errorTextIsAlwaysCaught() async {
        let missed = await withTaskGroup(of: Int.self) { group in
            for _ in 0..<8 {
                group.addTask {
                    var missed = 0
                    for _ in 0..<250 {
                        do {
                            try await ProcessRunner.run(URL(fileURLWithPath: "/bin/sh"), ["-c", "printf 'Couldn’t open input file (\\047typ?\\047)' >&2; exit 3"])
                        } catch let failure as ProcessFailure {
                            if !failure.message.contains("('typ?')") { missed += 1 }
                        } catch {
                            missed += 1
                        }
                    }
                    return missed
                }
            }
            return await group.reduce(0, +)
        }
        #expect(missed == 0, "lost the error text \(missed) times in 2000")
    }

    @Test func lastOutputLineIsDelivered() async throws {
        for _ in 0..<200 {
            let lines = Collected()
            try await ProcessRunner.run(URL(fileURLWithPath: "/bin/sh"), ["-c", "printf 'one\\ntwo'"]) { lines.add($0) }
            #expect(lines.values == ["one", "two"])
        }
    }
}

private final class Collected: @unchecked Sendable {
    private let lock = NSLock()
    private var items: [String] = []
    func add(_ line: String) { lock.withLock { items.append(line) } }
    var values: [String] { lock.withLock { items } }
}
