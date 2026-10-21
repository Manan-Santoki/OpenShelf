import Foundation

/// Why a file couldn't be saved, in words a person can act on.
enum SaveError: LocalizedError, Equatable {
    case noPermission(folder: String)
    case folderMissing(folder: String)
    case readOnlyVolume(folder: String)
    case diskFull
    case nameTooLong(file: String)
    case failed(file: String)

    var errorDescription: String? {
        switch self {
        case .noPermission(let folder): "You don’t have permission to save in “\(folder)”."
        case .folderMissing(let folder): "The folder “\(folder)” no longer exists."
        case .readOnlyVolume(let folder): "“\(folder)” is on a disk that can’t be written to."
        case .diskFull: "There isn’t enough free space on the disk."
        case .nameTooLong(let file): "The name “\(file)” is too long. Rename the original to something shorter."
        case .failed(let file): "“\(file)” couldn’t be saved."
        }
    }

    /// Names close to the 255-byte limit fail inside ImageIO, which adds its
    /// own suffix for a temporary file while writing.
    static let safeNameLength = 240

    /// Throws a specific error when `url` can't be written: its folder is
    /// missing or read-only, or its name is too long.
    static func checkWritable(_ url: URL, fileManager: FileManager = .default) throws {
        let folder = url.deletingLastPathComponent()
        var isDirectory: ObjCBool = false
        guard fileManager.fileExists(atPath: folder.path, isDirectory: &isDirectory), isDirectory.boolValue else {
            throw SaveError.folderMissing(folder: folder.lastPathComponent)
        }
        if (try? folder.resourceValues(forKeys: [.volumeIsReadOnlyKey]))?.volumeIsReadOnly == true {
            throw SaveError.readOnlyVolume(folder: folder.lastPathComponent)
        }
        guard fileManager.isWritableFile(atPath: folder.path) else {
            throw SaveError.noPermission(folder: folder.lastPathComponent)
        }
        if url.lastPathComponent.utf8.count > safeNameLength {
            throw SaveError.nameTooLong(file: url.lastPathComponent)
        }
    }

    /// Explains a failed write that didn't come with an error (ImageIO, PDFKit).
    static func diagnose(_ url: URL, fileManager: FileManager = .default) -> SaveError {
        do {
            try checkWritable(url, fileManager: fileManager)
        } catch let error as SaveError {
            return error
        } catch {}
        let folder = url.deletingLastPathComponent()
        if let free = (try? folder.resourceValues(forKeys: [.volumeAvailableCapacityKey]))?.volumeAvailableCapacity,
           free < 1_000_000 {
            return .diskFull
        }
        return .failed(file: url.lastPathComponent)
    }

    /// Maps a Cocoa or POSIX write error to a `SaveError`.
    static func from(_ error: Error, writing url: URL) -> SaveError {
        let folder = url.deletingLastPathComponent().lastPathComponent
        let nsError = error as NSError
        let underlying = nsError.userInfo[NSUnderlyingErrorKey] as? NSError
        let posix = nsError.domain == NSPOSIXErrorDomain ? nsError.code : underlying.flatMap { $0.domain == NSPOSIXErrorDomain ? $0.code : nil }

        switch (nsError.domain, nsError.code, posix) {
        case (NSCocoaErrorDomain, NSFileWriteNoPermissionError, _), (_, _, Int(EACCES)?), (_, _, Int(EPERM)?):
            return .noPermission(folder: folder)
        case (NSCocoaErrorDomain, NSFileWriteOutOfSpaceError, _), (_, _, Int(ENOSPC)?), (_, _, Int(EDQUOT)?):
            return .diskFull
        case (NSCocoaErrorDomain, NSFileWriteVolumeReadOnlyError, _), (_, _, Int(EROFS)?):
            return .readOnlyVolume(folder: folder)
        case (NSCocoaErrorDomain, NSFileWriteInvalidFileNameError, _), (_, _, Int(ENAMETOOLONG)?):
            return .nameTooLong(file: url.lastPathComponent)
        default:
            return diagnose(url)
        }
    }
}

extension Data {
    /// Writes next to the original without overwriting, with a clear error.
    func writeNew(to url: URL) throws {
        try SaveError.checkWritable(url)
        do {
            try write(to: url, options: .withoutOverwriting)
        } catch {
            throw SaveError.from(error, writing: url)
        }
    }
}
