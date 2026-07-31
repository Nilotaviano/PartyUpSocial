using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Tests.Kernel;

public class TextValueTests
{
    [Fact]
    public void TextValueObjects_CarryTheAgreedLengthBounds_D096()
    {
        Assert.Equal((3, 30), (Username.MinLength, Username.MaxLength));
        Assert.Equal((1, 50), (DisplayName.MinLength, DisplayName.MaxLength));
        Assert.Equal((1, 80), (SessionTitle.MinLength, SessionTitle.MaxLength));
        Assert.Equal((0, 500), (SessionNotes.MinLength, SessionNotes.MaxLength));
        Assert.Equal((1, 64), (GamingHandle.MinLength, GamingHandle.MaxLength));
        Assert.Equal((1, 100), (GameName.MinLength, GameName.MaxLength));
    }

    [Fact]
    public void Username_TrimsAndEnforcesItsBounds_D096() => AssertTextContract<Username>();

    [Fact]
    public void DisplayName_TrimsAndEnforcesItsBounds_D096() => AssertTextContract<DisplayName>();

    [Fact]
    public void SessionTitle_TrimsAndEnforcesItsBounds_D057() => AssertTextContract<SessionTitle>();

    [Fact]
    public void SessionNotes_TrimsAndEnforcesItsBounds_D096() => AssertTextContract<SessionNotes>();

    [Fact]
    public void GamingHandle_TrimsAndEnforcesItsBounds_D032() => AssertTextContract<GamingHandle>();

    [Fact]
    public void GameName_TrimsAndEnforcesItsBounds_D058() => AssertTextContract<GameName>();

    [Fact]
    public void Create_TrimsLeadingAndTrailingWhitespaceIncludingTabsAndNewlines()
    {
        Assert.Equal("gamer", Username.Create(" \t gamer \r\n ").Value);
    }

    [Fact]
    public void Create_PreservesInteriorWhitespace()
    {
        Assert.Equal("Friday   raid", SessionTitle.Create("  Friday   raid  ").Value);
        Assert.Equal("line one\nline two", SessionNotes.Create(" line one\nline two ").Value);
    }

    [Fact]
    public void Create_MeasuresLengthAfterTrimming_D096()
    {
        string atMax = new('a', Username.MaxLength);

        Assert.Equal(atMax, Username.Create("   " + atMax + "   ").Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("ab")]
    [InlineData(" ab ")]
    public void Username_RejectsAnythingShorterThanThreeCharacters_D096(string? candidate)
    {
        DomainException error = Assert.Throws<DomainException>(() => Username.Create(candidate));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void Username_AcceptsExactlyThreeAndThirtyCharacters_D096()
    {
        Assert.Equal(3, Username.Create("abc").Value.Length);
        Assert.Equal(30, Username.Create(new string('a', 30)).Value.Length);
    }

    [Fact]
    public void Username_RejectsThirtyOneCharacters_D096()
    {
        Assert.Throws<DomainException>(() => Username.Create(new string('a', 31)));
    }

    [Fact]
    public void DisplayName_RejectsEmptyAndOverFifty_D096()
    {
        Assert.Throws<DomainException>(() => DisplayName.Create("   "));
        Assert.Throws<DomainException>(() => DisplayName.Create(new string('a', 51)));
        Assert.Equal(50, DisplayName.Create(new string('a', 50)).Value.Length);
    }

    [Fact]
    public void SessionTitle_IsRequired_D057()
    {
        DomainException error = Assert.Throws<DomainException>(() => SessionTitle.Create(null));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void SessionNotes_MayBeNullOrEmpty_D096()
    {
        Assert.True(SessionNotes.Create(null).IsEmpty);
        Assert.True(SessionNotes.Create(string.Empty).IsEmpty);
        Assert.True(SessionNotes.Create("    ").IsEmpty);
        Assert.True(SessionNotes.Empty.IsEmpty);
        Assert.Equal(string.Empty, SessionNotes.Empty.Value);
    }

    [Fact]
    public void SessionNotes_RejectFiveHundredAndOneCharacters_D096()
    {
        Assert.Equal(500, SessionNotes.Create(new string('a', 500)).Value.Length);
        Assert.Throws<DomainException>(() => SessionNotes.Create(new string('a', 501)));
    }

    [Fact]
    public void GamingHandle_RejectsSixtyFiveCharacters_D032()
    {
        Assert.Equal(64, GamingHandle.Create(new string('a', 64)).Value.Length);
        Assert.Throws<DomainException>(() => GamingHandle.Create(new string('a', 65)));
    }

    [Fact]
    public void GameName_RejectsOneHundredAndOneCharacters_D058()
    {
        Assert.Equal(100, GameName.Create(new string('a', 100)).Value.Length);
        Assert.Throws<DomainException>(() => GameName.Create(new string('a', 101)));
    }

    [Fact]
    public void Equality_IsOverTheTrimmedValue()
    {
        Assert.Equal(Username.Create("gamer"), Username.Create("  gamer  "));
        Assert.NotEqual(Username.Create("gamer"), Username.Create("gamerx"));
    }

    [Fact]
    public void Equality_IsCaseSensitiveInTheDomain()
    {
        Assert.NotEqual(Username.Create("Gamer"), Username.Create("gamer"));
    }

    [Fact]
    public void DefaultValue_ReadsAsEmptyRatherThanNull()
    {
        Assert.Equal(string.Empty, default(Username).Value);
        Assert.Equal(string.Empty, default(SessionNotes).Value);
    }

    [Fact]
    public void ToString_RendersTheTrimmedValue()
    {
        Assert.Equal("Friday raid", SessionTitle.Create(" Friday raid ").ToString());
    }

    private static void AssertTextContract<T>()
        where T : struct, ITextValue<T>
    {
        string name = typeof(T).Name;
        int min = T.MinLength;
        int max = T.MaxLength;

        string body = new('a', Math.Max(min, 1));
        Assert.True(T.Create("  " + body + "  ").Value == body, name + " did not trim its input.");

        string atMax = new('a', max);
        Assert.True(T.Create(atMax).Value.Length == max, name + " rejected its maximum length.");
        Assert.True(
            T.Create(" " + atMax + " ").Value.Length == max,
            name + " measured length before trimming.");

        DomainException tooLong = Assert.Throws<DomainException>(() => T.Create(new string('a', max + 1)));
        Assert.Equal(DomainError.ValidationFailed, tooLong.Error);

        if (min > 0)
        {
            Assert.True(T.Create(new string('a', min)).Value.Length == min, name + " rejected its minimum length.");

            DomainException tooShort = Assert.Throws<DomainException>(() => T.Create(new string('a', min - 1)));
            Assert.Equal(DomainError.ValidationFailed, tooShort.Error);

            Assert.Throws<DomainException>(() => T.Create(null));
            Assert.Throws<DomainException>(() => T.Create("   "));
        }
        else
        {
            Assert.True(T.Create(null).Value.Length == 0, name + " rejected a null value.");
            Assert.True(T.Create("   ").Value.Length == 0, name + " rejected a whitespace-only value.");
        }
    }
}
