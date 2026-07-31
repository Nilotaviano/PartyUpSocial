using Microsoft.AspNetCore.Identity;

namespace PartyUp.Infrastructure.Identity;

public sealed class AuthRole : IdentityRole<Guid>
{
    public AuthRole()
    {
    }

    public AuthRole(string roleName)
        : base(roleName)
    {
    }
}
