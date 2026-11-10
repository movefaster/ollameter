import Foundation

/// Follows a log file by polling, which handles Ollama renaming `server.log` when it restarts.
public final class LogTailer {
    private let path: String
    private var descriptor: Int32 = -1
    private var inode: ino_t = 0
    private var offset: off_t = 0
    private var partialLine = Data()

    private static let chunkSize = 1 << 18
    private static let longestLine = 1 << 20

    public init(path: String) {
        self.path = path
    }

    deinit {
        if descriptor >= 0 { close(descriptor) }
    }

    /// Positions at the end of the file and returns up to `backlogBytes` of complete lines before it.
    public func openAtEnd(backlogBytes: Int) -> [String] {
        guard reopen(), let size = currentSize() else { return [] }
        offset = max(size - off_t(backlogBytes), 0)
        let startedMidFile = offset > 0
        var lines = readNew()
        if startedMidFile, !lines.isEmpty { lines.removeFirst() }
        return lines
    }

    /// Returns complete lines appended since the last call. An unfinished last line is held back.
    public func readNew() -> [String] {
        var status = stat()
        guard stat(path, &status) == 0 else { return [] }
        if descriptor < 0 || status.st_ino != inode || status.st_size < offset {
            guard reopen() else { return [] }
            offset = 0
            partialLine.removeAll()
        }

        var bytes = Data()
        var buffer = [UInt8](repeating: 0, count: Self.chunkSize)
        while true {
            let count = pread(descriptor, &buffer, buffer.count, offset)
            guard count > 0 else { break }
            bytes.append(buffer, count: count)
            offset += off_t(count)
            if count < buffer.count { break }
        }
        guard !bytes.isEmpty else { return [] }

        var data = partialLine
        data.append(bytes)
        var lines: [String] = []
        var start = data.startIndex
        while let newline = data[start...].firstIndex(of: 0x0A) {
            lines.append(String(decoding: data[start..<newline], as: UTF8.self))
            start = data.index(after: newline)
        }
        partialLine = Data(data[start...])
        if partialLine.count > Self.longestLine { partialLine.removeAll() }
        return lines
    }

    private func reopen() -> Bool {
        if descriptor >= 0 { close(descriptor) }
        descriptor = open(path, O_RDONLY)
        guard descriptor >= 0 else { return false }
        var status = stat()
        guard fstat(descriptor, &status) == 0 else { return false }
        inode = status.st_ino
        return true
    }

    private func currentSize() -> off_t? {
        var status = stat()
        return fstat(descriptor, &status) == 0 ? status.st_size : nil
    }
}
