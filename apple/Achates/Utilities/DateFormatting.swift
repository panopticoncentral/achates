import Foundation

extension Date {
    /// Compact label for agent/session list rows: time today, "Yesterday",
    /// abbreviated weekday within the past week, else a short numeric date.
    /// Uses `.formatted()` throughout so it honors the user's locale and
    /// 24-hour clock preference (the old hardcoded "h:mm a" did not), and
    /// allocates no `DateFormatter` per call.
    ///
    /// `relativeTo` / `calendar` are injectable for deterministic testing.
    func chatListLabel(relativeTo now: Date = Date(), calendar: Calendar = .current) -> String {
        if calendar.isDate(self, inSameDayAs: now) {
            return formatted(date: .omitted, time: .shortened)
        }

        let startOfToday = calendar.startOfDay(for: now)
        if let yesterday = calendar.date(byAdding: .day, value: -1, to: startOfToday),
           calendar.isDate(self, inSameDayAs: yesterday) {
            return "Yesterday"
        }

        if let weekAgo = calendar.date(byAdding: .day, value: -6, to: startOfToday),
           self >= weekAgo {
            return formatted(.dateTime.weekday(.abbreviated))
        }

        return formatted(date: .numeric, time: .omitted)
    }
}
