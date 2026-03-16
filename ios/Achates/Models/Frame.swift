import Foundation

// MARK: - JSON Value

enum JSONValue: Sendable, Codable, Equatable {
    case string(String)
    case int(Int)
    case double(Double)
    case bool(Bool)
    case array([JSONValue])
    case object([String: JSONValue])
    case null

    init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        if container.decodeNil() {
            self = .null
        } else if let value = try? container.decode(Bool.self) {
            self = .bool(value)
        } else if let value = try? container.decode(Int.self) {
            self = .int(value)
        } else if let value = try? container.decode(Double.self) {
            self = .double(value)
        } else if let value = try? container.decode(String.self) {
            self = .string(value)
        } else if let value = try? container.decode([JSONValue].self) {
            self = .array(value)
        } else if let value = try? container.decode([String: JSONValue].self) {
            self = .object(value)
        } else {
            throw DecodingError.dataCorruptedError(in: container, debugDescription: "Cannot decode JSONValue")
        }
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        switch self {
        case .string(let v): try container.encode(v)
        case .int(let v): try container.encode(v)
        case .double(let v): try container.encode(v)
        case .bool(let v): try container.encode(v)
        case .array(let v): try container.encode(v)
        case .object(let v): try container.encode(v)
        case .null: try container.encodeNil()
        }
    }

    var stringValue: String? {
        if case .string(let v) = self { return v }
        return nil
    }

    var intValue: Int? {
        if case .int(let v) = self { return v }
        return nil
    }

    var doubleValue: Double? {
        switch self {
        case .double(let v): return v
        case .int(let v): return Double(v)
        default: return nil
        }
    }

    var boolValue: Bool? {
        if case .bool(let v) = self { return v }
        return nil
    }

    var arrayValue: [JSONValue]? {
        if case .array(let v) = self { return v }
        return nil
    }

    var objectValue: [String: JSONValue]? {
        if case .object(let v) = self { return v }
        return nil
    }

    subscript(key: String) -> JSONValue? {
        if case .object(let dict) = self { return dict[key] }
        return nil
    }

    subscript(index: Int) -> JSONValue? {
        if case .array(let arr) = self, index >= 0 && index < arr.count { return arr[index] }
        return nil
    }
}

// MARK: - Frame Types

enum FrameType: String, Codable, Sendable {
    case req
    case res
    case evt
}

struct RequestFrame: Codable, Sendable {
    let type: FrameType
    let id: String
    let method: String
    let params: [String: JSONValue]?

    init(method: String, params: [String: JSONValue]? = nil) {
        self.type = .req
        self.id = UUID().uuidString.lowercased()
        self.method = method
        self.params = params
    }

    init(id: String, method: String, params: [String: JSONValue]? = nil) {
        self.type = .req
        self.id = id
        self.method = method
        self.params = params
    }
}

struct ResponseFrame: Codable, Sendable {
    let type: FrameType
    let id: String
    let ok: Bool
    let payload: [String: JSONValue]?
    let error: ResponseError?

    init(id: String, ok: Bool, payload: [String: JSONValue]? = nil, error: ResponseError? = nil) {
        self.type = .res
        self.id = id
        self.ok = ok
        self.payload = payload
        self.error = error
    }
}

struct ResponseError: Codable, Sendable {
    let code: String
    let message: String
}

struct EventFrame: Codable, Sendable {
    let type: FrameType
    let event: String
    let payload: [String: JSONValue]?
    let seq: Int?
}

// MARK: - Frame Parsing

enum Frame: Sendable {
    case request(RequestFrame)
    case response(ResponseFrame)
    case event(EventFrame)

    static func parse(_ data: Data) throws -> Frame {
        guard let dict = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let typeStr = dict["type"] as? String else {
            throw FrameError.invalidFrame
        }

        let decoder = JSONDecoder()

        switch typeStr {
        case "req":
            let frame = try decoder.decode(RequestFrame.self, from: data)
            return .request(frame)
        case "res":
            let frame = try decoder.decode(ResponseFrame.self, from: data)
            return .response(frame)
        case "evt":
            let frame = try decoder.decode(EventFrame.self, from: data)
            return .event(frame)
        default:
            throw FrameError.unknownType(typeStr)
        }
    }
}

enum FrameError: Error, LocalizedError {
    case invalidFrame
    case unknownType(String)
    case timeout
    case notConnected
    case serverError(String)

    var errorDescription: String? {
        switch self {
        case .invalidFrame: return "Invalid frame data"
        case .unknownType(let t): return "Unknown frame type: \(t)"
        case .timeout: return "Request timed out"
        case .notConnected: return "Not connected to server"
        case .serverError(let msg): return msg
        }
    }
}
