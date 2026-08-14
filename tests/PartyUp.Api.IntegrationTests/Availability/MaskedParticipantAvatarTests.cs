using System.Text.Json;
using PartyUp.Api.Contracts.Availability;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Api.IntegrationTests.Availability;

/// <summary>
/// AVATARS_DESIGN.md §2.4/§5 (D-188/D-076): the calendar's friend layer renders a face, so
/// <c>MaskedParticipant</c> carries an avatar — but only ever alongside a readable display name.
/// <see cref="MaskedParticipantAssigner"/> is the single place that decision is made, so both halves
/// are asserted there and on the serialized shape: "absent" and "null" are different answers on the
/// wire, and only absence keeps a masked person indistinguishable from an unresolvable one.
/// </summary>
public sealed class MaskedParticipantAvatarTests
{
    private static readonly UserId PersonId = new(Guid.CreateVersion7());

    [Fact]
    public void AnUnmaskedParticipantCarriesTheirAvatar_D188()
    {
        MaskedParticipantAssigner assigner = AssignerFor(DisplayName.Create("Ana"), Avatar.FromEmoji("\U0001F642"));

        MaskedParticipant participant = assigner.For(PersonId);

        Assert.Equal("Ana", participant.DisplayName);
        Assert.Equal("emoji", participant.Avatar?.Kind);
        Assert.Equal("\U0001F642", participant.Avatar?.Emoji);
        Assert.Contains("\"avatar\"", Serialize(participant), StringComparison.Ordinal);
    }

    [Fact]
    public void AMaskedParticipantCarriesNoAvatarProperty_D076_D188()
    {
        MaskedParticipantAssigner assigner = AssignerFor(displayName: null, Avatar.FromEmoji("\U0001F642"));

        MaskedParticipant participant = assigner.For(PersonId);

        Assert.Null(participant.DisplayName);
        Assert.Null(participant.Avatar);
        Assert.DoesNotContain("avatar", Serialize(participant), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The picture arm of the same rule: an external avatar's proxy token is a durable, correlatable
    /// handle on the person, so a masked identity must not emit it under any key.
    /// </summary>
    [Fact]
    public void AMaskedParticipantWithAnExternalAvatarLeaksNoProxyToken_D076_D188()
    {
        const string token = "masked-participant-proxy-token";
        MaskedParticipantAssigner assigner = AssignerFor(
            displayName: null,
            Avatar.FromSource(new AvatarSourceId(Guid.CreateVersion7()), token));

        MaskedParticipant participant = assigner.For(PersonId);

        Assert.Null(participant.Avatar);
        Assert.DoesNotContain(token, Serialize(participant), StringComparison.Ordinal);
    }

    /// <summary>
    /// The caller-resolved overload (session organizer, join-request requester) offers no avatar, so
    /// those surfaces keep the ordinal-only shape they shipped with.
    /// </summary>
    [Fact]
    public void ACallerResolvedParticipantWithNoAvatarOfferedCarriesNone_D188()
    {
        var assigner = new MaskedParticipantAssigner();

        MaskedParticipant participant = assigner.For(PersonId, DisplayName.Create("Ana"));

        Assert.Equal("Ana", participant.DisplayName);
        Assert.Null(participant.Avatar);
        Assert.DoesNotContain("avatar", Serialize(participant), StringComparison.OrdinalIgnoreCase);
    }

    private static MaskedParticipantAssigner AssignerFor(DisplayName? displayName, Avatar avatar) =>
        new(new Dictionary<UserId, KnownParticipant>
        {
            [PersonId] = new(displayName, avatar),
        });

    private static string Serialize(MaskedParticipant participant) =>
        JsonSerializer.Serialize(participant, JsonSerializerOptions.Web);
}
