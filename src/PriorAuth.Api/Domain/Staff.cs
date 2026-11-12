namespace PriorAuth.Api.Domain;

/// <summary>What a member of the plan's staff may do. Each comes from an Active Directory group.</summary>
public enum StaffRole
{
    /// <summary>Works the queue: claims, assigns, asks providers for missing information. Cannot decide.</summary>
    Coordinator,
    /// <summary>Decides prior authorization requests.</summary>
    LicensedClinician,
    /// <summary>Decides prior authorization requests and is the only role that decides concurrent reviews (inpatient, PHF).</summary>
    Physician,
    /// <summary>Registers and deactivates provider systems. Does not see the queue.</summary>
    Admin,
}

public static class StaffRoleNames
{
    /// <summary>The role as staff say it, for pages.</summary>
    public static string Describe(this StaffRole role) => role switch
    {
        StaffRole.LicensedClinician => "licensed clinician",
        _ => role.ToString().ToLowerInvariant(),
    };
}

/// <summary>A signed-in member of the plan's staff, as the rules see them.</summary>
/// <param name="Account">The sign-in account, for example COUNTY\jdoe. What decisions are recorded against.</param>
public sealed record Staff(string Account, string DisplayName, IReadOnlySet<StaffRole> Roles)
{
    public bool Has(StaffRole role) => Roles.Contains(role);

    /// <summary>Sees the queue and the requests in it.</summary>
    public bool WorksQueue => Has(StaffRole.Coordinator) || Has(StaffRole.LicensedClinician) || Has(StaffRole.Physician);

    /// <summary>May approve or deny a request of this review type.</summary>
    public bool MayDecide(ReviewType? review) => review == ReviewType.ConcurrentReview
        ? Has(StaffRole.Physician)
        : Has(StaffRole.LicensedClinician) || Has(StaffRole.Physician);
}
