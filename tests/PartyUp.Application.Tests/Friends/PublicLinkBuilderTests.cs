using System.Reflection;
using System.Runtime.ExceptionServices;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Tests.Friends;

public sealed class PublicLinkBuilderTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("example.test")]
    [InlineData("ftp://example.test")]
    [InlineData("https://example.test?tenant=one")]
    [InlineData("https://example.test#fragment")]
    public void Constructor_RejectsInvalidPublicBaseUrl(string? publicBaseUrl)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => new ProductionPublicLinkBuilder(publicBaseUrl));

        Assert.Equal("publicBaseUrl", exception.ParamName);
    }

    [Fact]
    public void ProfileAndAdd_TrimBaseUrlEscapeUsernameAndKeepCanonicalCode_D144()
    {
        var links = new ProductionPublicLinkBuilder("  https://example.test///  ");

        Assert.Equal(
            "https://example.test/u/alex%2Fparty%3F",
            links.Profile(Username.Create("alex/party?")));
        Assert.Equal(
            "https://example.test/add/23456789AB",
            links.Add(FriendCode.Create("23456789AB")));
    }

    [Fact]
    public void Add_RejectsANonCanonicalFriendCode_D144()
    {
        var links = new ProductionPublicLinkBuilder("https://example.test");

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => links.Add(FriendCode.Create("O3456789AB")));

        Assert.Equal("friendCode", exception.ParamName);
    }
}

internal sealed class ProductionPublicLinkBuilder : IPublicLinkBuilder
{
    private static readonly Type BuilderType = LoadBuilderType();
    private readonly object _builder;

    public ProductionPublicLinkBuilder(string? publicBaseUrl)
    {
        _builder = Invoke(() => Activator.CreateInstance(
            BuilderType,
            new object?[] { publicBaseUrl }))!;
    }

    public string Profile(Username username) =>
        (string)Invoke(() => BuilderType
            .GetMethod(nameof(Profile), [typeof(Username)])!
            .Invoke(_builder, [username]))!;

    public string Add(FriendCode friendCode) =>
        (string)Invoke(() => BuilderType
            .GetMethod(nameof(Add), [typeof(FriendCode)])!
            .Invoke(_builder, [friendCode]))!;

    private static Type LoadBuilderType()
    {
        DirectoryInfo? repository = new(AppContext.BaseDirectory);
        while (repository is not null &&
               !File.Exists(Path.Combine(repository.FullName, "PartyUp.sln")))
        {
            repository = repository.Parent;
        }

        if (repository is null)
        {
            throw new InvalidOperationException("The repository root could not be located.");
        }

        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        string assemblyPath = Path.Combine(
            repository.FullName,
            "src",
            "PartyUp.Infrastructure",
            "bin",
            configuration,
            "net10.0",
            "PartyUp.Infrastructure.dll");
        Assembly assembly = Assembly.LoadFrom(assemblyPath);
        return assembly.GetType(
            "PartyUp.Infrastructure.Friends.PublicLinkBuilder",
            throwOnError: true)!;
    }

    private static object? Invoke(Func<object?> action)
    {
        try
        {
            return action();
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }
}
