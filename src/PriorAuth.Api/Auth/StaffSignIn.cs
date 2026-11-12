using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PriorAuth.Api.Data;
using PriorAuth.Api.Domain;

namespace PriorAuth.Api.Auth;

/// <summary>How the plan's staff sign in. One setting picks the method; the portal only ever asks for <see cref="Staff"/>.</summary>
public enum StaffSignInMode
{
    /// <summary>Windows Integrated Authentication against the plan's Active Directory. Roles come from AD groups.</summary>
    Windows,
    /// <summary>Pick a test user from configuration. Development only: start-up fails anywhere else.</summary>
    Development,
}

/// <summary>A test user for the Development sign-in.</summary>
public sealed class TestStaffUser
{
    public string Account { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public StaffRole[] Roles { get; init; } = [];
}

/// <summary>Staff sign-in: the Windows handler or the Development stand-in, the AD group to role mapping, and the policies.</summary>
public static class StaffSignIn
{
    /// <summary>The scheme the portal and the internal endpoints authenticate with. It forwards to Windows or the stand-in.</summary>
    public const string Scheme = "Staff";
    public const string DevelopmentScheme = "StaffDevelopment";
    /// <summary>Coordinators, licensed clinicians and physicians: everyone who works the review queue.</summary>
    public const string QueuePolicy = "StaffQueue";
    public const string AdminPolicy = "StaffAdmin";
    /// <summary>Anyone with at least one staff role.</summary>
    public const string AnyRolePolicy = "StaffAnyRole";
    /// <summary>Signed in as staff, with or without a role. Only the no-access page uses it.</summary>
    public const string SignedInPolicy = "StaffSignedIn";
    public const string DevSignInPath = "/staff/dev-sign-in";
    private const string RolesIdentityType = "StaffRoles";
    private const string DisplayNameClaim = "staff_display_name";
    /// <summary>
    /// The portal's own claim for a staff role. Roles are never checked with IsInRole: under Windows sign-in that also
    /// answers for any Windows group with the same name, so a group called "Admin" would grant the Admin role.
    /// </summary>
    public const string RoleClaim = "priorauth_staff_role";

    public static StaffSignInMode Mode(IConfiguration config, IHostEnvironment environment)
    {
        var configured = config["StaffSignIn:Mode"];
        var mode = string.IsNullOrWhiteSpace(configured)
            ? environment.IsDevelopment() ? StaffSignInMode.Development : StaffSignInMode.Windows
            : Enum.TryParse<StaffSignInMode>(configured, ignoreCase: true, out var parsed)
                ? parsed
                : throw new InvalidOperationException($"StaffSignIn:Mode must be Windows or Development; '{configured}' is neither.");
        if (mode == StaffSignInMode.Development && !environment.IsDevelopment())
            throw new InvalidOperationException("StaffSignIn:Mode Development is a test stand-in and is refused outside the Development environment.");
        return mode;
    }

    public static IServiceCollection AddStaffSignIn(this IServiceCollection services, IConfiguration config, IHostEnvironment environment)
    {
        var mode = Mode(config, environment);
        var authentication = services.AddAuthentication();
        if (mode == StaffSignInMode.Development)
        {
            authentication.AddCookie(DevelopmentScheme, options =>
            {
                options.Cookie.Name = "priorauth-dev-staff";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.LoginPath = DevSignInPath;
                options.AccessDeniedPath = "/no-access";
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.Events.OnRedirectToLogin = context => Refuse(context, StatusCodes.Status401Unauthorized);
                options.Events.OnRedirectToAccessDenied = context => Refuse(context, StatusCodes.Status403Forbidden);
            });
        }
        else
        {
            authentication.AddNegotiate();
        }
        authentication.AddPolicyScheme(Scheme, Scheme, options =>
            options.ForwardDefaultSelector = _ => mode == StaffSignInMode.Development ? DevelopmentScheme : NegotiateDefaults.AuthenticationScheme);

        services.AddTransient<IClaimsTransformation, WindowsGroupRoles>();
        services.AddAuthorizationBuilder()
            .AddPolicy(QueuePolicy, policy => policy.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser()
                .RequireClaim(RoleClaim, nameof(StaffRole.Coordinator), nameof(StaffRole.LicensedClinician), nameof(StaffRole.Physician)))
            .AddPolicy(AdminPolicy, policy => policy.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser()
                .RequireClaim(RoleClaim, nameof(StaffRole.Admin)))
            .AddPolicy(AnyRolePolicy, policy => policy.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser()
                .RequireClaim(RoleClaim, Enum.GetNames<StaffRole>()))
            .AddPolicy(SignedInPolicy, policy => policy.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser());
        return services;
    }

    // The JSON endpoints answer with a status, not a redirect to a page.
    private static Task Refuse(RedirectContext<CookieAuthenticationOptions> context, int status)
    {
        if (context.Request.Path.StartsWithSegments("/internal"))
            context.Response.StatusCode = status;
        else
            context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    }

    /// <summary>The signed-in staff member, or null when the caller is not one.</summary>
    public static Staff? CurrentStaff(this ClaimsPrincipal user)
    {
        var account = user.Identity?.IsAuthenticated == true ? user.Identity.Name : null;
        if (string.IsNullOrEmpty(account)) return null;
        var roles = user.FindAll(RoleClaim)
            .Select(c => Enum.TryParse<StaffRole>(c.Value, out var role) ? role : (StaffRole?)null)
            .OfType<StaffRole>().ToHashSet();
        return new Staff(account, user.FindFirstValue(DisplayNameClaim) ?? account, roles);
    }

    /// <summary>
    /// The roles a Windows account has, from the configured AD groups (StaffSignIn:Groups:Coordinator and so on).
    /// A role whose group is not configured is never given.
    /// </summary>
    public static HashSet<StaffRole> RolesFromGroups(Func<string, bool> isMemberOf, IConfiguration config)
    {
        var roles = new HashSet<StaffRole>();
        foreach (var role in Enum.GetValues<StaffRole>())
        {
            var group = config[$"StaffSignIn:Groups:{role}"];
            if (!string.IsNullOrWhiteSpace(group) && isMemberOf(group.Trim())) roles.Add(role);
        }
        return roles;
    }

    /// <summary>The identity the portal reads: the account, its display name and its roles.</summary>
    public static ClaimsPrincipal Principal(string account, string displayName, IEnumerable<StaffRole> roles, string authenticationType) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, account), new Claim(DisplayNameClaim, displayName), .. roles.Select(r => new Claim(RoleClaim, r.ToString()))],
            authenticationType, ClaimTypes.Name, RoleClaim));

    /// <summary>Keeps the list of people who have used the portal, so coordinators can assign requests to them.</summary>
    public static async Task RememberAsync(PriorAuthDb db, Staff staff, DateTime nowUtc)
    {
        var known = await db.StaffMembers.FindAsync(staff.Account);
        if (known is null) db.StaffMembers.Add(StaffMember.Create(staff, nowUtc));
        else known.Seen(staff, nowUtc);
        await db.SaveChangesAsync();
    }

    /// <summary>The Development stand-in: a page to pick a test user, and sign-out. Mapped only in that mode.</summary>
    public static void MapStaffSignIn(this WebApplication app)
    {
        if (Mode(app.Configuration, app.Environment) != StaffSignInMode.Development) return;

        // A plain form post with no antiforgery token, so the clinic demo and the tests can sign in the same way.
        // Development only: it signs in as a configured test user, never as a real account.
        app.MapPost(DevSignInPath, async (HttpContext http, PriorAuthDb db, TimeProvider clock) =>
        {
            var form = await http.Request.ReadFormAsync();
            var user = TestUsers(app.Configuration).FirstOrDefault(u => u.Account.Equals(form["account"].ToString(), StringComparison.OrdinalIgnoreCase));
            if (user is null) return Results.Problem("There is no test user with that account.", statusCode: StatusCodes.Status400BadRequest);

            var principal = Principal(user.Account, user.DisplayName, user.Roles, DevelopmentScheme);
            await http.SignInAsync(DevelopmentScheme, principal);
            await RememberAsync(db, principal.CurrentStaff()!, clock.GetUtcNow().UtcDateTime);
            var returnUrl = form["returnUrl"].ToString();
            return Results.LocalRedirect(Uri.IsWellFormedUriString(returnUrl, UriKind.Relative) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//") ? returnUrl : "/");
        }).DisableAntiforgery();

        app.MapPost("/staff/sign-out", async (HttpContext http) =>
        {
            await http.SignOutAsync(DevelopmentScheme);
            return Results.LocalRedirect(DevSignInPath);
        });
    }

    public static IReadOnlyList<TestStaffUser> TestUsers(IConfiguration config) =>
        config.GetSection("StaffSignIn:TestUsers").Get<TestStaffUser[]>() ?? [];

    /// <summary>
    /// Adds the portal's roles to a Windows sign-in, from the AD groups in configuration, and remembers the person.
    /// Provider tokens and the Development stand-in already carry what they need and are left alone.
    /// </summary>
    private sealed class WindowsGroupRoles(IConfiguration config, PriorAuthDb db, IMemoryCache cache, TimeProvider clock, ILogger<WindowsGroupRoles> logger)
        : IClaimsTransformation
    {
        public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
        {
            var identity = principal.Identity;
            if (identity?.IsAuthenticated != true || identity.AuthenticationType is not ("Negotiate" or "Kerberos" or "NTLM")) return principal;
            if (principal.Identities.Any(i => i.AuthenticationType == RolesIdentityType)) return principal;

            var account = identity.Name!;
            var roles = RolesFromGroups(principal.IsInRole, config);
            var displayName = cache.GetOrCreate($"staff-name:{account}", entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
                return DirectoryDisplayName(account) ?? account;
            })!;
            principal.AddIdentity(new ClaimsIdentity(
                [new Claim(DisplayNameClaim, displayName), .. roles.Select(r => new Claim(RoleClaim, r.ToString()))],
                RolesIdentityType, ClaimTypes.Name, RoleClaim));

            // Windows signs in on every request; remember the person at most every few minutes.
            if (roles.Count > 0 && cache.TryGetValue($"staff-seen:{account}", out _) is false)
            {
                cache.Set($"staff-seen:{account}", true, TimeSpan.FromMinutes(10));
                await RememberAsync(db, new Staff(account, displayName, roles), clock.GetUtcNow().UtcDateTime);
            }
            return principal;
        }

        /// <summary>The person's display name from Active Directory, for example "Jordan Doe". Null when it cannot be read.</summary>
        private string? DirectoryDisplayName(string account)
        {
            if (!OperatingSystem.IsWindows()) return null;
            try
            {
                using var domain = new System.DirectoryServices.AccountManagement.PrincipalContext(System.DirectoryServices.AccountManagement.ContextType.Domain);
                using var user = System.DirectoryServices.AccountManagement.UserPrincipal.FindByIdentity(domain, account);
                return string.IsNullOrWhiteSpace(user?.DisplayName) ? null : user.DisplayName.Trim();
            }
            catch (Exception e) when (e is System.DirectoryServices.AccountManagement.PrincipalException
                or System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                // The account name still identifies them; the name only reads better in lists.
                logger.LogWarning(e, "Could not read the display name of {Account} from Active Directory.", account);
                return null;
            }
        }
    }
}
