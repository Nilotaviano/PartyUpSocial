namespace PartyUp.Domain.Feedback;

// Values are persisted: append only, never renumber.
public enum FeedbackType
{
    Bug = 0,
    Idea = 1,
    Other = 2,
}
