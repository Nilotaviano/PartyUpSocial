using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Email;
using PartyUp.Application.Feedback;
using PartyUp.Domain.Feedback;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Tests.Feedback;

public sealed class FeedbackUseCaseTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 8, 18, 12, 0);

    private static readonly string[] AdminRecipients =
        ["admin-one@example.test", "admin-two@example.test"];

    [Fact]
    public async Task Submit_RecordsTheAuthenticatedSubmitterAndCommitsBeforeMailing_D213_D215()
    {
        var repository = new FakeFeedbackRepository();
        var dispatcher = new FakeEmailDispatcher();
        var unitOfWork = new FakeUnitOfWork();
        List<string> trail = [];
        dispatcher.Trail = trail;
        unitOfWork.Trail = trail;
        UserId submitter = UserId.New();
        SubmitFeedbackUseCase useCase = Create(repository, dispatcher, unitOfWork);

        FeedbackItemId id = await useCase.ExecuteAsync(
            new SubmitFeedbackCommand(
                submitter,
                FeedbackType.Bug,
                "The vote grid drops my taps.",
                "/polls/1",
                "Mozilla/5.0",
                "pt-BR",
                "1.4.2"),
            CancellationToken.None);

        FeedbackItem stored = Assert.Single(repository.Items);
        Assert.Equal(id, stored.Id);
        Assert.Equal(submitter, stored.SubmitterUserId);
        Assert.Equal("The vote grid drops my taps.", stored.Message);
        Assert.Equal(1, unitOfWork.Executions);

        // A-016: the row's transaction ran, and only then the mail — one per admin seed address.
        Assert.Equal(["commit", "mail", "mail"], trail);
        Assert.Equal(2, dispatcher.Messages.Count);
        Assert.Equal(
            AdminRecipients,
            dispatcher.Messages.Select(message => message.ToAddress).ToArray());
        foreach (EmailMessage message in dispatcher.Messages)
        {
            Assert.Equal(EmailTemplate.AdminFeedbackSubmitted, message.Template);
            Assert.Equal("Bug", message.Model["Type"]);
            Assert.Equal("The vote grid drops my taps.", message.Model["Message"]);
            Assert.Equal(submitter.Value.ToString("D"), message.Model["Submitter"]);
        }
    }

    [Fact]
    public async Task Submit_StoresAnAnonymousRowWhenThereIsNoCaller_D213()
    {
        var repository = new FakeFeedbackRepository();
        SubmitFeedbackUseCase useCase = Create(repository, new FakeEmailDispatcher());

        await useCase.ExecuteAsync(
            new SubmitFeedbackCommand(
                SubmitterUserId: null,
                FeedbackType.Other,
                "Just saying hi.",
                Route: null,
                UserAgent: null,
                Locale: null,
                AppVersion: null),
            CancellationToken.None);

        FeedbackItem stored = Assert.Single(repository.Items);
        Assert.Null(stored.SubmitterUserId);
        Assert.Equal(FeedbackType.Other, stored.Type);
    }

    [Fact]
    public async Task Submit_KeepsTheRowWhenTheMailChannelRefusesIt_D215()
    {
        var repository = new FakeFeedbackRepository();
        var dispatcher = new DroppingEmailDispatcher();
        SubmitFeedbackUseCase useCase = Create(repository, dispatcher);

        await useCase.ExecuteAsync(
            new SubmitFeedbackCommand(
                UserId.New(),
                FeedbackType.Idea,
                "Add a dark mode toggle.",
                null,
                null,
                null,
                null),
            CancellationToken.None);

        Assert.Single(repository.Items);
        Assert.Equal(2, dispatcher.Attempts);
    }

    [Fact]
    public async Task Submit_SendsNothingWhenNoAdminAddressIsConfigured_D215()
    {
        var repository = new FakeFeedbackRepository();
        var dispatcher = new FakeEmailDispatcher();
        var useCase = new SubmitFeedbackUseCase(
            repository,
            new FakeUnitOfWork(),
            dispatcher,
            new FeedbackEmailOptions([]),
            new TestClock(Now));

        await useCase.ExecuteAsync(
            new SubmitFeedbackCommand(
                null,
                FeedbackType.Bug,
                "No admins configured.",
                null,
                null,
                null,
                null),
            CancellationToken.None);

        Assert.Single(repository.Items);
        Assert.Empty(dispatcher.Messages);
    }

    [Fact]
    public async Task Submit_RejectsAnEmptyMessageBeforeAnythingIsWritten_D212()
    {
        var repository = new FakeFeedbackRepository();
        var dispatcher = new FakeEmailDispatcher();
        var unitOfWork = new FakeUnitOfWork();
        SubmitFeedbackUseCase useCase = Create(repository, dispatcher, unitOfWork);

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() =>
            useCase.ExecuteAsync(
                new SubmitFeedbackCommand(null, FeedbackType.Bug, "  ", null, null, null, null),
                CancellationToken.None));

        Assert.Equal(DomainError.ValidationFailed, exception.Error);
        Assert.Empty(repository.Items);
        Assert.Equal(0, unitOfWork.Executions);
        Assert.Empty(dispatcher.Messages);
    }

    [Fact]
    public async Task SetHandled_TogglesTheRowAndAnswersNullForAnUnknownId_D214()
    {
        var repository = new FakeFeedbackRepository();
        FeedbackItem item = FeedbackItem.Create(
            null,
            FeedbackType.Bug,
            "Handle me.",
            null,
            null,
            null,
            null,
            new TestClock(Now));
        repository.Add(item);
        var useCase = new SetFeedbackHandledUseCase(
            repository,
            new FakeUnitOfWork(),
            new TestClock(Now));

        FeedbackItem? handled = await useCase.ExecuteAsync(item.Id, true, CancellationToken.None);
        Assert.NotNull(handled);
        Assert.True(handled!.Handled);
        Assert.Equal(Now, handled.HandledAt);

        FeedbackItem? cleared = await useCase.ExecuteAsync(item.Id, false, CancellationToken.None);
        Assert.False(cleared!.Handled);
        Assert.Null(cleared.HandledAt);

        Assert.Null(await useCase.ExecuteAsync(
            FeedbackItemId.New(),
            true,
            CancellationToken.None));
    }

    private static SubmitFeedbackUseCase Create(
        FakeFeedbackRepository repository,
        IEmailDispatcher dispatcher,
        FakeUnitOfWork? unitOfWork = null) =>
        new(
            repository,
            unitOfWork ?? new FakeUnitOfWork(),
            dispatcher,
            new FeedbackEmailOptions(AdminRecipients),
            new TestClock(Now));

    private sealed class DroppingEmailDispatcher : IEmailDispatcher
    {
        internal int Attempts { get; private set; }

        public Task DispatchAsync(EmailMessage message, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Post-commit fan-out must use TryDispatch.");

        public bool TryDispatch(EmailMessage message)
        {
            Attempts++;
            return false;
        }
    }

    private sealed class FakeFeedbackRepository : IFeedbackRepository
    {
        internal List<FeedbackItem> Items { get; } = [];

        public void Add(FeedbackItem item) => Items.Add(item);

        public Task<FeedbackItem?> FindAsync(
            FeedbackItemId id,
            CancellationToken cancellationToken) =>
            Task.FromResult(Items.SingleOrDefault(item => item.Id == id));

        public Task<int> AnonymizeForUserAsync(
            UserId submitterUserId,
            CancellationToken cancellationToken)
        {
            int count = 0;
            foreach (FeedbackItem item in Items.Where(item => item.SubmitterUserId == submitterUserId))
            {
                item.Anonymize();
                count++;
            }

            return Task.FromResult(count);
        }
    }
}
