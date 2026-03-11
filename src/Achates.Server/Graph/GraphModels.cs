namespace Achates.Server.Graph;

/// <summary>
/// Standard Graph API collection response wrapper.
/// </summary>
internal sealed class GraphCollection<T>
{
    public List<T> Value { get; set; } = [];
}

internal sealed class GraphMessage
{
    public string? Id { get; set; }
    public string? Subject { get; set; }
    public GraphEmailAddress? From { get; set; }
    public List<GraphRecipient>? ToRecipients { get; set; }
    public DateTimeOffset? ReceivedDateTime { get; set; }
    public bool IsRead { get; set; }
    public string? BodyPreview { get; set; }
    public GraphBody? Body { get; set; }
}

internal sealed class GraphEmailAddress
{
    public GraphAddress? EmailAddress { get; set; }
}

internal sealed class GraphRecipient
{
    public GraphAddress? EmailAddress { get; set; }
}

internal sealed class GraphAddress
{
    public string? Name { get; set; }
    public string? Address { get; set; }

    public override string ToString() =>
        Name is not null ? $"{Name} <{Address}>" : Address ?? "";
}

internal sealed class GraphBody
{
    public string? ContentType { get; set; }
    public string? Content { get; set; }
}

internal sealed class GraphEvent
{
    public string? Id { get; set; }
    public string? Subject { get; set; }
    public GraphDateTimeZone? Start { get; set; }
    public GraphDateTimeZone? End { get; set; }
    public GraphLocation? Location { get; set; }
    public GraphEmailAddress? Organizer { get; set; }
    public List<GraphAttendee>? Attendees { get; set; }
    public GraphBody? Body { get; set; }
    public bool IsAllDay { get; set; }
    public bool IsCancelled { get; set; }
    public string? ShowAs { get; set; }
    public GraphRecurrence? Recurrence { get; set; }
}

internal sealed class GraphDateTimeZone
{
    public string? DateTime { get; set; }
    public string? TimeZone { get; set; }
}

internal sealed class GraphLocation
{
    public string? DisplayName { get; set; }
}

internal sealed class GraphAttendee
{
    public GraphAddress? EmailAddress { get; set; }
    public GraphResponseStatus? Status { get; set; }
    public string? Type { get; set; }
}

internal sealed class GraphResponseStatus
{
    public string? Response { get; set; }
}

internal sealed class GraphRecurrence
{
    public GraphRecurrencePattern? Pattern { get; set; }
}

internal sealed class GraphRecurrencePattern
{
    public string? Type { get; set; }
    public int Interval { get; set; }
    public List<string>? DaysOfWeek { get; set; }
}

internal sealed class GraphScheduleResponse
{
    public List<GraphScheduleItem>? Value { get; set; }
}

internal sealed class GraphScheduleItem
{
    public string? ScheduleId { get; set; }
    public List<GraphScheduleEntry>? ScheduleItems { get; set; }
}

internal sealed class GraphScheduleEntry
{
    public string? Status { get; set; }
    public GraphDateTimeZone? Start { get; set; }
    public GraphDateTimeZone? End { get; set; }
    public string? Subject { get; set; }
}
