# Template for the cask in Manan-Santoki/homebrew-tap. The Release workflow
# fills in version and sha256 and pushes it there on every release.
cask "openshelf" do
  version "1.9.0"
  sha256 "0000000000000000000000000000000000000000000000000000000000000000"

  url "https://github.com/Manan-Santoki/OpenShelf/releases/download/v#{version}/OpenShelf-#{version}.dmg",
      verified: "github.com/Manan-Santoki/OpenShelf/"
  name "OpenShelf"
  desc "Menu bar shelf and drag-and-drop file converter"
  homepage "https://openshelf.space/"

  livecheck do
    url "https://raw.githubusercontent.com/Manan-Santoki/OpenShelf/appcast/appcast.xml"
    strategy :sparkle, &:short_version
  end

  auto_updates true
  depends_on macos: ">= :sonoma"

  app "OpenShelf.app"

  uninstall quit: "app.openshelf.OpenShelf"

  zap trash: [
    "~/Library/Application Support/OpenShelf",
    "~/Library/Caches/app.openshelf.OpenShelf",
    "~/Library/HTTPStorages/app.openshelf.OpenShelf",
    "~/Library/Preferences/app.openshelf.OpenShelf.plist",
  ]
end
