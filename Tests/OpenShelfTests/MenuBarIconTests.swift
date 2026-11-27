import Foundation
import Testing
@testable import OpenShelf

struct MenuBarIconPreferenceTests {
    @MainActor
    @Test func defaultsOnAndPersists() throws {
        let suite = "OpenShelfTests.showsMenuBarIcon.\(UUID().uuidString)"
        let defaults = try #require(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        #expect(Preferences(defaults: defaults).showsMenuBarIcon)
        Preferences(defaults: defaults).showsMenuBarIcon = false
        #expect(!Preferences(defaults: defaults).showsMenuBarIcon)
        Preferences(defaults: defaults).showsMenuBarIcon = true
        #expect(Preferences(defaults: defaults).showsMenuBarIcon)
    }
}
