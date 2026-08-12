using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Domain.Tests.Users;

public class ProfileValueObjectTests
{
    [Fact]
    public void Avatar_FromEmoji_TrimsAndKeepsNoAvatarSource_D050()
    {
        Avatar avatar = Avatar.FromEmoji("  " + TestUsers.Emoji + "  ");

        Assert.True(avatar.IsEmoji);
        Assert.False(avatar.IsExternal);
        Assert.Equal(TestUsers.Emoji, avatar.Emoji);
        Assert.Null(avatar.SourceId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Avatar_FromEmoji_RejectsAnEmptyGlyph_D050(string? glyph)
    {
        DomainException error = Assert.Throws<DomainException>(() => Avatar.FromEmoji(glyph));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void Avatar_FromEmoji_RejectsMoreThanTheZwjBudget_D050()
    {
        Assert.Equal(
            Avatar.MaxEmojiLength,
            Avatar.FromEmoji(new string('a', Avatar.MaxEmojiLength)).Emoji.Length);
        Assert.Throws<DomainException>(() => Avatar.FromEmoji(new string('a', Avatar.MaxEmojiLength + 1)));
    }

    [Fact]
    public void Avatar_FromSource_CarriesNoEmoji_D190()
    {
        AvatarSourceId sourceId = AvatarSourceId.New();
        Avatar avatar = Avatar.FromSource(sourceId);

        Assert.True(avatar.IsExternal);
        Assert.Equal(AvatarKind.External, avatar.Kind);
        Assert.Equal(sourceId, avatar.SourceId);
        Assert.Equal(string.Empty, avatar.Emoji);
        Assert.True(avatar.DrawsFrom(sourceId));
        Assert.False(avatar.DrawsFrom(AvatarSourceId.New()));
    }

    [Fact]
    public void Avatar_Default_ReadsAsUnset_D050()
    {
        Assert.True(default(Avatar).IsUnset);
        Assert.False(Avatar.Fallback.IsUnset);
        Assert.False(Avatar.FromSource(AvatarSourceId.New()).IsUnset);
    }

    [Fact]
    public void Avatar_Fallback_IsTheEmojiUnlinkingFallsBackTo_D050()
    {
        Assert.Equal(Avatar.FallbackEmoji, Avatar.Fallback.Emoji);
        Assert.True(Avatar.Fallback.IsEmoji);
    }

    [Fact]
    public void Avatar_EqualityIsByValue_D050()
    {
        AvatarSourceId sourceId = AvatarSourceId.New();

        Assert.Equal(Avatar.FromEmoji(TestUsers.Emoji), Avatar.FromEmoji(TestUsers.Emoji));
        Assert.Equal(Avatar.FromSource(sourceId), Avatar.FromSource(sourceId));
        Assert.NotEqual(Avatar.FromEmoji(TestUsers.Emoji), Avatar.FromSource(sourceId));
    }

    [Fact]
    public void FriendCode_CanonicalisesToUpperCaseSoLookupIsCaseInsensitive_D144()
    {
        Assert.Equal("PU-ABC123", FriendCode.Create("  pu-abc123  ").Value);
        Assert.Equal(FriendCode.Create("PU-ABC123"), FriendCode.Create("pu-abc123"));
    }

    [Fact]
    public void FriendCode_EnforcesItsBounds_D096()
    {
        Assert.Equal(FriendCode.MinLength, FriendCode.Create(new string('a', FriendCode.MinLength)).Value.Length);
        Assert.Equal(FriendCode.MaxLength, FriendCode.Create(new string('a', FriendCode.MaxLength)).Value.Length);
        Assert.Throws<DomainException>(() => FriendCode.Create(new string('a', FriendCode.MinLength - 1)));
        Assert.Throws<DomainException>(() => FriendCode.Create(new string('a', FriendCode.MaxLength + 1)));
        Assert.Throws<DomainException>(() => FriendCode.Create(null));
    }

    [Fact]
    public void FriendCode_DefaultReadsAsEmpty_D031()
    {
        Assert.True(default(FriendCode).IsEmpty);
        Assert.Equal(string.Empty, default(FriendCode).Value);
    }

    [Fact]
    public void Locale_TrimsAndEnforcesItsBounds_D096()
    {
        Assert.Equal("pt-BR", Locale.Create("  pt-BR  ").Value);
        Assert.Throws<DomainException>(() => Locale.Create("e"));
        Assert.Throws<DomainException>(() => Locale.Create(new string('a', Locale.MaxLength + 1)));
        Assert.True(default(Locale).IsEmpty);
    }

    [Fact]
    public void Locale_KeepsItsCasingBecauseBcp47SubtagsAreConventional_D096()
    {
        Assert.Equal("pt-BR", Locale.Create("pt-BR").ToString());
        Assert.NotEqual(Locale.Create("pt-br"), Locale.Create("pt-BR"));
    }
}
