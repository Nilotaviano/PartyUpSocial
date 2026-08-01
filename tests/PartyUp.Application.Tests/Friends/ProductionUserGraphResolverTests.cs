using System.Reflection;
using System.Runtime.ExceptionServices;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Tests.Friends;

public sealed class ProductionUserGraphResolverTests
{
    [Fact]
    public void ResolveProfile_HidesABlockedGraphRow_D065()
    {
        User viewer = FriendTestData.Active("viewer", "Viewer");
        User subject = FriendTestData.Active("alex", "Alex");

        Assert.Null(ProductionUserGraphResolver.ResolveBlockedProfile(subject, viewer.Id));
    }
}

internal static class ProductionUserGraphResolver
{
    private static readonly Type ResolverType = LoadResolverType();

    internal static ResolvedUserGraph? ResolveBlockedProfile(User candidate, UserId viewerId)
    {
        Type rowType = ResolverType.GetNestedType("CandidateGraphRow", BindingFlags.NonPublic)!;
        object row = Activator.CreateInstance(
            rowType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [candidate, null, null, null, true],
            culture: null)!;
        MethodInfo method = ResolverType.GetMethod(
            "ResolveProfile",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        return (ResolvedUserGraph?)Invoke(() => method.Invoke(null, [row, viewerId]));
    }

    private static Type LoadResolverType()
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
            "PartyUp.Infrastructure.Persistence.Repositories.UserGraphResolver",
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
