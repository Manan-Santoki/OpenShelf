import Foundation

/// Names a watched folder never reacts to: hidden files (including
/// .DS_Store and the dot-prefixed files Screenshot writes before the real
/// one), unfinished downloads and other temporary files.
enum ArrivalFilter {
    /// Extensions of files still being written by a browser, Screenshot or
    /// a download tool; the finished file is renamed without them.
    static let partialExtensions: Set<String> = [
        "download", "crdownload", "part", "partial", "opdownload", "tmp", "temp", "screenshot",
    ]

    static func isIgnored(_ name: String) -> Bool {
        // "~$Report.docx" is Office's lock file; "Icon\r" holds a folder's custom icon.
        if name.hasPrefix(".") || name.hasPrefix("~$") || name == "Icon\r" { return true }
        let ext = (name as NSString).pathExtension.lowercased()
        return partialExtensions.contains(ext)
    }
}

/// Decides which files in a watched folder are new and finished, from
/// repeated listings of it. Pure state, so the rules can be tested without
/// touching the disk:
///
/// - Files present at the first listing are never reported.
/// - A file renamed inside the folder (same file number, new name) and a
///   file replaced in place under the same name (as apps do when saving)
///   aren't new.
/// - A new file is reported once its size has stayed the same for
///   `settleInterval` (an empty file waits `emptySettleInterval`, in case
///   it's about to be filled), and never while Finder marks it as still
///   being copied.
struct ArrivalTracker {
    /// One file in a listing of the folder.
    struct Entry: Equatable, Sendable {
        var name: String
        /// Identifies the file across renames (its inode); nil when unknown.
        var fileNumber: UInt64?
        var size: Int64
        var isRegularFile: Bool
        /// Finder is still copying it.
        var isBusy = false
    }

    private struct Candidate {
        var fileNumber: UInt64?
        var size: Int64
        var isBusy: Bool
        var unchangedSince: Date
    }

    static let settleInterval: TimeInterval = 1
    static let emptySettleInterval: TimeInterval = 5

    /// Files already accounted for, by name. (Updated with `updateValue`,
    /// since assigning a nil file number would remove the name.)
    private var known: [String: UInt64?] = [:]
    private var pending: [String: Candidate] = [:]

    /// - Parameter existing: The folder's contents when watching starts.
    init(existing: [Entry]) {
        for entry in existing where Self.isEligible(entry) {
            known.updateValue(entry.fileNumber, forKey: entry.name)
        }
    }

    /// Whether some new file is still being written, so the folder needs
    /// listing again shortly even if nothing else changes in it.
    var hasPending: Bool { !pending.isEmpty }

    /// Takes a fresh listing and returns the names of new files that have
    /// finished arriving since the last one, sorted.
    mutating func observe(_ entries: [Entry], now: Date) -> [String] {
        let eligible = entries.filter(Self.isEligible)
        let present = Set(eligible.map(\.name))
        // Taken before forgetting names that went away, which renamed files' old names did.
        let knownNumbers = Set(known.values.compactMap { $0 })
        // Forget files that went away, so a later file with the same name counts.
        known = known.filter { present.contains($0.key) }
        pending = pending.filter { present.contains($0.key) }

        var ready: [String] = []
        for entry in eligible {
            if let number = known[entry.name] {
                // Same file, or one saved over it in place: not new either way.
                if number != entry.fileNumber { known.updateValue(entry.fileNumber, forKey: entry.name) }
                continue
            }
            if let number = entry.fileNumber, knownNumbers.contains(number) {
                // Renamed inside the folder.
                known.updateValue(number, forKey: entry.name)
                pending[entry.name] = nil
                continue
            }
            guard let candidate = pending[entry.name],
                  candidate.size == entry.size, candidate.isBusy == entry.isBusy, candidate.fileNumber == entry.fileNumber
            else {
                // Just appeared, or changed since the last listing: start waiting again.
                pending[entry.name] = Candidate(fileNumber: entry.fileNumber, size: entry.size, isBusy: entry.isBusy, unchangedSince: now)
                continue
            }
            let wait = entry.size > 0 ? Self.settleInterval : Self.emptySettleInterval
            guard !entry.isBusy, now.timeIntervalSince(candidate.unchangedSince) >= wait else { continue }
            pending[entry.name] = nil
            known.updateValue(entry.fileNumber, forKey: entry.name)
            ready.append(entry.name)
        }
        return ready.sorted { $0.localizedStandardCompare($1) == .orderedAscending }
    }

    private static func isEligible(_ entry: Entry) -> Bool {
        entry.isRegularFile && !ArrivalFilter.isIgnored(entry.name)
    }
}
