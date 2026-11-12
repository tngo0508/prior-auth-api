namespace PriorAuth.Api.Domain;

/// <summary>
/// Someone who has signed in to the portal. Kept so coordinators can assign requests to people the app knows,
/// since it does not read the Active Directory user list. Roles are as of their last sign-in.
/// </summary>
public class StaffMember
{
    private StaffMember() { } // for Entity Framework

    public string Account { get; private set; } = "";
    public string DisplayName { get; private set; } = "";
    /// <summary>Role names separated by commas.</summary>
    public string Roles { get; private set; } = "";
    public DateTime LastSeenAtUtc { get; private set; }

    public static StaffMember Create(Staff staff, DateTime nowUtc)
    {
        var member = new StaffMember { Account = staff.Account };
        member.Seen(staff, nowUtc);
        return member;
    }

    public void Seen(Staff staff, DateTime nowUtc)
    {
        DisplayName = staff.DisplayName;
        Roles = string.Join(',', staff.Roles.Order());
        LastSeenAtUtc = nowUtc;
    }

    public Staff AsStaff() => new(Account, DisplayName,
        Roles.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(r => Enum.TryParse<StaffRole>(r, out var role) ? role : (StaffRole?)null).OfType<StaffRole>().ToHashSet());
}
