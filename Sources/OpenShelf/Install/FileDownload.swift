import Foundation

/// Why a download didn't arrive.
enum DownloadError: LocalizedError, Equatable {
    case unreachable(host: String)
    case httpStatus(Int, host: String)
    case untrustedLocation(String)

    var errorDescription: String? {
        switch self {
        case .unreachable(let host): "Couldn’t reach \(host). Check the connection and try again."
        case .httpStatus(let status, let host): "\(host) answered with an error (\(status)). Try again later."
        case .untrustedLocation(let host): "The download was redirected to \(host), so it was stopped."
        }
    }
}

/// Downloads with URLSession, without cookies or a cache. Only ever started
/// by a person clicking Install.
enum FileDownload {
    /// Bytes received so far and the expected total, when the server says.
    typealias Progress = @Sendable (_ received: Int64, _ expected: Int64?) -> Void

    private static let session: URLSession = {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 60
        configuration.waitsForConnectivity = false
        return URLSession(configuration: configuration)
    }()

    /// Downloads `url` to `destination`, returning where the request ended up
    /// after redirects. Cancelling the task cancels the download.
    @discardableResult
    static func download(_ url: URL, to destination: URL, progress: @escaping Progress) async throws -> URL {
        let delegate = DownloadDelegate(destination: destination, progress: progress)
        let task = session.downloadTask(with: url)
        task.delegate = delegate
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<URL, Error>) in
                delegate.start(task, continuation: continuation)
            }
        } onCancel: {
            task.cancel()
        }
    }

    /// A small text file, such as a checksum or a directory listing. Nil when
    /// the server says it doesn't exist.
    static func text(at url: URL) async throws -> String? {
        let (data, response) = try await translatingErrors(for: url) { try await session.data(from: url) }
        if let http = response as? HTTPURLResponse {
            if http.statusCode == 404 { return nil }
            guard (200..<300).contains(http.statusCode) else {
                throw DownloadError.httpStatus(http.statusCode, host: url.host ?? "The server")
            }
        }
        return String(decoding: data, as: UTF8.self)
    }

    private static func translatingErrors<T>(for url: URL, _ body: () async throws -> T) async throws -> T {
        do {
            return try await body()
        } catch let error as URLError {
            if error.code == .cancelled { throw CancellationError() }
            throw DownloadError.unreachable(host: url.host ?? "the server")
        }
    }
}

/// Receives one download task's callbacks.
private final class DownloadDelegate: NSObject, URLSessionDownloadDelegate, @unchecked Sendable {
    private let destination: URL
    private let progress: FileDownload.Progress
    private let lock = NSLock()
    private var continuation: CheckedContinuation<URL, Error>?
    private var result: Result<URL, Error>?
    private var lastReported: Int64 = 0

    init(destination: URL, progress: @escaping FileDownload.Progress) {
        self.destination = destination
        self.progress = progress
    }

    func start(_ task: URLSessionDownloadTask, continuation: CheckedContinuation<URL, Error>) {
        lock.withLock { self.continuation = continuation }
        task.resume()
    }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didWriteData bytesWritten: Int64, totalBytesWritten: Int64, totalBytesExpectedToWrite: Int64) {
        let expected: Int64? = totalBytesExpectedToWrite > 0 ? totalBytesExpectedToWrite : nil
        // About every 256 KB, so a 300 MB download doesn't flood the UI.
        let shouldReport: Bool = lock.withLock {
            guard totalBytesWritten - lastReported >= 256 << 10 || totalBytesWritten == totalBytesExpectedToWrite else { return false }
            lastReported = totalBytesWritten
            return true
        }
        if shouldReport { progress(totalBytesWritten, expected) }
    }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didFinishDownloadingTo location: URL) {
        // The temporary file is deleted when this returns, so move it now.
        let finalURL = downloadTask.currentRequest?.url ?? downloadTask.originalRequest?.url
        let host = finalURL?.host ?? "The server"
        if let http = downloadTask.response as? HTTPURLResponse, !(200..<300).contains(http.statusCode) {
            setResult(.failure(DownloadError.httpStatus(http.statusCode, host: host)))
            return
        }
        do {
            try? FileManager.default.removeItem(at: destination)
            try FileManager.default.moveItem(at: location, to: destination)
            setResult(.success(downloadTask.response?.url ?? finalURL ?? destination))
        } catch {
            setResult(.failure(error))
        }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        let host = task.originalRequest?.url?.host ?? "the server"
        if let error = error as? URLError {
            setResult(.failure(error.code == .cancelled ? CancellationError() : DownloadError.unreachable(host: host)))
        } else if let error {
            setResult(.failure(error))
        }
        let outcome: (CheckedContinuation<URL, Error>, Result<URL, Error>)? = lock.withLock {
            guard let continuation else { return nil }
            self.continuation = nil
            return (continuation, result ?? .failure(DownloadError.unreachable(host: host)))
        }
        if let (continuation, result) = outcome {
            continuation.resume(with: result)
        }
    }

    private func setResult(_ result: Result<URL, Error>) {
        lock.withLock {
            if self.result == nil { self.result = result }
        }
    }
}
