/// What one wedge of a wheel shows on the current page.
enum WheelSlot: Hashable {
    case item(WheelItem)
    /// Turns to the next page. Pages are numbered from 0.
    case more(toPage: Int, of: Int)
    /// Turns from the last page back to the first.
    case back(toPage: Int, of: Int)
    /// A gap on a last page with fewer items, so Back stays where More was.
    case empty

    var item: WheelItem? {
        if case .item(let item) = self { return item }
        return nil
    }

    /// The page More or Back turns to; nil for other slots.
    var targetPage: Int? {
        switch self {
        case .more(let page, _), .back(let page, _): page
        case .item, .empty: nil
        }
    }

    var isEmpty: Bool { self == .empty }

    var systemImage: String? {
        switch self {
        case .item(let item): item.systemImage
        case .more: "ellipsis"
        case .back: "arrow.uturn.backward"
        case .empty: nil
        }
    }

    /// The text in the label above the wheel while this slot is highlighted,
    /// e.g. "Crop" or "More · 2 of 2".
    var label: String? {
        switch self {
        case .item(let item): item.title
        case .more(let page, let count): "More · \(page + 1) of \(count)"
        case .back(let page, let count): "Back · \(page + 1) of \(count)"
        case .empty: nil
        }
    }

    var accessibilityLabel: String {
        switch self {
        case .item(let item): item.title
        case .more(let page, let count): "More options, page \(page + 1) of \(count)"
        case .back(let page, let count): "Back to page \(page + 1) of \(count)"
        case .empty: ""
        }
    }
}

/// Splits long wheels into pages so their wedges stay big enough to hit
/// mid-drag. A wheel of up to seven items shows them all. A longer one shows
/// seven wedges: six items, then More in the last wedge (clockwise from
/// 12 o'clock); the last page has Back in that same wedge, with empty wedges
/// filling any gap so nothing moves when the page turns.
enum WheelPaging {
    /// Wedges on a paged wheel, and the most items a wheel shows unpaged.
    static let slotCount = 7
    static var itemsPerPage: Int { slotCount - 1 }
    /// Where More and Back sit on a paged wheel.
    static var pageTurnSlot: Int { slotCount - 1 }

    static func pageCount(forItemCount count: Int) -> Int {
        guard count > slotCount else { return 1 }
        return (count + itemsPerPage - 1) / itemsPerPage
    }

    /// The wedges of `page` (from 0) of a wheel holding `items`.
    static func slots(for items: [WheelItem], page: Int) -> [WheelSlot] {
        let pageCount = pageCount(forItemCount: items.count)
        guard pageCount > 1 else { return items.map(WheelSlot.item) }
        let page = min(max(page, 0), pageCount - 1)
        let start = page * itemsPerPage
        let pageItems = items[start..<min(start + itemsPerPage, items.count)]

        var slots: [WheelSlot] = pageItems.map(WheelSlot.item)
        slots += Array(repeating: WheelSlot.empty, count: itemsPerPage - pageItems.count)
        let isLastPage = page == pageCount - 1
        slots.append(isLastPage ? .back(toPage: 0, of: pageCount) : .more(toPage: page + 1, of: pageCount))
        return slots
    }
}

/// Decides when resting on More or Back turns the page. Fed with readings of
/// whether the pointer is on that wedge and when, it turns the page once the
/// pointer has stayed for `delay`, then waits for the pointer to leave and
/// come back before turning again, so resting never flips back and forth.
struct PageTurnDwell {
    static let delaySeconds: Double = 0.35
    static var delay: Duration { .milliseconds(Int(delaySeconds * 1000)) }

    private var enteredAt: ContinuousClock.Instant?
    private var hasTurned = false

    /// Whether the pointer is resting on More or Back and will turn the page.
    var isPending: Bool { enteredAt != nil && !hasTurned }

    /// Returns true when the page should turn now.
    mutating func update(isOnPageTurn: Bool, at now: ContinuousClock.Instant) -> Bool {
        guard isOnPageTurn else {
            enteredAt = nil
            hasTurned = false
            return false
        }
        guard let enteredAt else {
            self.enteredAt = now
            return false
        }
        guard !hasTurned, enteredAt.duration(to: now) >= Self.delay else { return false }
        hasTurned = true
        return true
    }

    /// The page turned some other way (Return, a click or a drop). A pointer
    /// on More or Back then has to leave before resting turns the page again.
    mutating func pageDidTurn(isOnPageTurn: Bool, at now: ContinuousClock.Instant) {
        enteredAt = isOnPageTurn ? (enteredAt ?? now) : nil
        hasTurned = isOnPageTurn
    }
}
