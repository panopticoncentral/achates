import Foundation

struct CronJobInfo: Identifiable, Sendable, Hashable {
    var id: String { "\(agent):\(jobId)" }
    let agent: String
    let jobId: String
    let name: String
    let kind: Kind
    let schedule: Schedule
    let enabled: Bool
    let message: String
    let state: State

    enum Kind: String, Sendable {
        case user
        case dreamtime
    }

    enum Schedule: Sendable, Hashable {
        case at(Date)
        case every(minutes: Double)
        case cron(expression: String, timezone: String?)
        case unknown
    }

    struct State: Sendable, Hashable {
        let lastStatus: String?
        let lastRunAt: Date?
        let nextRunAt: Date?
        let lastError: String?
        let consecutiveErrors: Int
    }

    static func fromList(_ payload: [String: JSONValue]) -> [CronJobInfo] {
        (payload["jobs"]?.arrayValue ?? []).compactMap { item in
            guard let obj = item.objectValue,
                  let agent = obj["agent"]?.stringValue,
                  let jobId = obj["id"]?.stringValue,
                  let name = obj["name"]?.stringValue else { return nil }

            let kind = Kind(rawValue: obj["kind"]?.stringValue ?? "user") ?? .user
            let enabled = obj["enabled"]?.boolValue ?? true
            let message = obj["message"]?.stringValue ?? ""

            return CronJobInfo(
                agent: agent,
                jobId: jobId,
                name: name,
                kind: kind,
                schedule: parseSchedule(obj["schedule"]),
                enabled: enabled,
                message: message,
                state: parseState(obj["state"])
            )
        }
    }

    private static func parseSchedule(_ value: JSONValue?) -> Schedule {
        guard let obj = value?.objectValue,
              let type = obj["type"]?.stringValue else { return .unknown }

        switch type {
        case "at":
            let ms = obj["time"]?.intValue ?? 0
            return .at(Date(timeIntervalSince1970: Double(ms) / 1000))
        case "every":
            let minutes = obj["minutes"]?.doubleValue ?? 0
            return .every(minutes: minutes)
        case "cron":
            let expression = obj["expression"]?.stringValue ?? ""
            let tz = obj["timezone"]?.stringValue
            return .cron(expression: expression, timezone: tz)
        default:
            return .unknown
        }
    }

    private static func parseState(_ value: JSONValue?) -> State {
        guard let obj = value?.objectValue else {
            return State(lastStatus: nil, lastRunAt: nil, nextRunAt: nil, lastError: nil, consecutiveErrors: 0)
        }

        let lastRun = (obj["last_run_at"]?.intValue).map {
            Date(timeIntervalSince1970: Double($0) / 1000)
        }
        let nextRun = (obj["next_run_at"]?.intValue).map {
            Date(timeIntervalSince1970: Double($0) / 1000)
        }

        return State(
            lastStatus: obj["last_status"]?.stringValue,
            lastRunAt: lastRun,
            nextRunAt: nextRun,
            lastError: obj["last_error"]?.stringValue,
            consecutiveErrors: obj["consecutive_errors"]?.intValue ?? 0
        )
    }
}

extension CronJobInfo.Schedule {
    var displayString: String {
        switch self {
        case .at(let date):
            let f = DateFormatter()
            f.dateStyle = .short
            f.timeStyle = .short
            return "Once at \(f.string(from: date))"
        case .every(let minutes):
            if minutes >= 1440 { return "Every \(Int(minutes / 1440)) day(s)" }
            if minutes >= 60 { return "Every \(Int(minutes / 60)) hour(s)" }
            return "Every \(Int(minutes)) min"
        case .cron(let expression, let tz):
            return tz.map { "cron: \(expression) (\($0))" } ?? "cron: \(expression)"
        case .unknown:
            return "unknown"
        }
    }
}
