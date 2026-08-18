using NodaTime;
using PartyUp.Domain.Feedback;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Tests.Feedback;

public sealed class FeedbackItemTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 8, 18, 12, 0);

    [Fact]
    public void Create_RequiresANonEmptyMessageWithinTheCap_D212()
    {
        DomainException empty = Assert.Throws<DomainException>(() =>
            Create(message: "   "));
        Assert.Equal(DomainError.ValidationFailed, empty.Error);

        DomainException tooLong = Assert.Throws<DomainException>(() =>
            Create(message: new string('x', FeedbackItem.MaximumMessageLength + 1)));
        Assert.Equal(DomainError.ValidationFailed, tooLong.Error);

        FeedbackItem atTheCap = Create(
            message: new string('x', FeedbackItem.MaximumMessageLength));
        Assert.Equal(FeedbackItem.MaximumMessageLength, atTheCap.Message.Length);
    }

    [Fact]
    public void Create_AcceptsASubmissionCarryingNoContextAtAll_D212()
    {
        FeedbackItem item = FeedbackItem.Create(
            submitterUserId: null,
            FeedbackType.Idea,
            "  A tidy idea.  ",
            route: null,
            userAgent: "   ",
            locale: null,
            appVersion: string.Empty,
            new TestClock(Now));

        Assert.Null(item.SubmitterUserId);
        Assert.Equal(FeedbackType.Idea, item.Type);
        Assert.Equal("A tidy idea.", item.Message);
        Assert.Null(item.Route);
        Assert.Null(item.UserAgent);
        Assert.Null(item.Locale);
        Assert.Null(item.AppVersion);
        Assert.Equal(Now, item.CreatedAt);
        Assert.False(item.Handled);
        Assert.Null(item.HandledAt);
    }

    [Fact]
    public void Create_TruncatesOverlongContextRatherThanRejectingTheSubmission_D212()
    {
        FeedbackItem item = FeedbackItem.Create(
            UserId.New(),
            FeedbackType.Bug,
            "It broke.",
            new string('r', FeedbackItem.MaximumShortContextLength + 50),
            new string('u', FeedbackItem.MaximumUserAgentLength + 50),
            "pt-BR",
            "1.2.3",
            new TestClock(Now));

        Assert.Equal(FeedbackItem.MaximumShortContextLength, item.Route!.Length);
        Assert.Equal(FeedbackItem.MaximumUserAgentLength, item.UserAgent!.Length);
        Assert.Equal("pt-BR", item.Locale);
        Assert.Equal("1.2.3", item.AppVersion);
    }

    [Fact]
    public void SetHandled_StampsHandledAtAndClearsItOnTheWayBack_D214()
    {
        FeedbackItem item = Create();
        Instant later = Now + Duration.FromHours(3);

        item.SetHandled(handled: true, new TestClock(Now));
        Assert.True(item.Handled);
        Assert.Equal(Now, item.HandledAt);

        // Idempotent: a second mark keeps the first instant.
        item.SetHandled(handled: true, new TestClock(later));
        Assert.Equal(Now, item.HandledAt);

        item.SetHandled(handled: false, new TestClock(later));
        Assert.False(item.Handled);
        Assert.Null(item.HandledAt);

        // Cleared, so a re-mark records the real second instant rather than resurrecting the first.
        item.SetHandled(handled: true, new TestClock(later));
        Assert.Equal(later, item.HandledAt);
    }

    [Fact]
    public void Anonymize_DropsOnlyTheSubmitterAndRetainsTheRow_D216()
    {
        FeedbackItem item = Create(submitterUserId: UserId.New());
        item.SetHandled(handled: true, new TestClock(Now));

        item.Anonymize();

        Assert.Null(item.SubmitterUserId);
        Assert.Equal("It broke.", item.Message);
        Assert.Equal(FeedbackType.Bug, item.Type);
        Assert.Equal("/sessions", item.Route);
        Assert.Equal("en", item.Locale);
        Assert.Equal(Now, item.CreatedAt);
        Assert.True(item.Handled);

        // Idempotent — a repeated executor pass changes nothing further.
        item.Anonymize();
        Assert.Null(item.SubmitterUserId);
    }

    private static FeedbackItem Create(
        UserId? submitterUserId = null,
        string message = "It broke.") =>
        FeedbackItem.Create(
            submitterUserId,
            FeedbackType.Bug,
            message,
            "/sessions",
            "Mozilla/5.0",
            "en",
            "1.0.0",
            new TestClock(Now));

    private sealed class TestClock(Instant current) : IClock
    {
        public Instant GetCurrentInstant() => current;
    }
}
