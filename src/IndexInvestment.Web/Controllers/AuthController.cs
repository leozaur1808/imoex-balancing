using IndexInvestment.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IndexInvestment.Web.Controllers;

public class AuthController : ControllerBase
{
    private readonly UserManager<IdentityUser> _users;
    private readonly SignInManager<IdentityUser> _signIn;
    private readonly RoleManager<IdentityRole> _roles;
    private readonly PortfolioService _portfolio;
    private readonly MarketDataService _market;

    public AuthController(
        UserManager<IdentityUser> users,
        SignInManager<IdentityUser> signIn,
        RoleManager<IdentityRole> roles,
        PortfolioService portfolio,
        MarketDataService market)
    {
        _users = users;
        _signIn = signIn;
        _roles = roles;
        _portfolio = portfolio;
        _market = market;
    }

    /// <summary>
    /// Tells the login page which mode to render: 'setup' while no
    /// account exists yet, 'login' afterwards.
    /// </summary>
    [HttpGet("/api/auth-state")]
    public async Task<IActionResult> AuthState() =>
        Ok(new { setup = !await _users.Users.AnyAsync() });

    /// <summary>First run: create the admin account and start a session.</summary>
    [HttpPost("/api/setup")]
    public async Task<IActionResult> Setup([FromBody] SetupRequest req)
    {
        if (await _users.Users.AnyAsync())
            throw new ConflictException("An account already exists — log in instead");
        if (string.IsNullOrWhiteSpace(req.Email))
            throw new ValidationError("Email is required");
        if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 4)
            throw new ValidationError("Password must be at least 4 characters");

        var user = new IdentityUser { UserName = req.Email.Trim(), Email = req.Email.Trim() };
        var created = await _users.CreateAsync(user, req.Password);
        if (!created.Succeeded)
            throw new ValidationError(string.Join("; ", created.Errors.Select(e => e.Description)));

        if (!await _roles.RoleExistsAsync(Roles.Admin))
            await _roles.CreateAsync(new IdentityRole(Roles.Admin));
        await _users.AddToRoleAsync(user, Roles.Admin);
        await _portfolio.SaveSettingsAsync(user.Id, 0.045m, "IMOEX");

        await _signIn.SignInAsync(user, isPersistent: true);
        return Ok(new { ok = true });
    }

    [HttpPost("/api/login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req)
    {
        var name = (req.Email ?? "").Trim();
        var user = await _users.FindByNameAsync(name) ?? await _users.FindByEmailAsync(name);
        if (user is null)
            return Unauthorized(new { detail = "Wrong email or password" });

        var result = await _signIn.PasswordSignInAsync(
            user, req.Password ?? "", isPersistent: true, lockoutOnFailure: true);
        if (result.IsLockedOut)
            return StatusCode(StatusCodes.Status429TooManyRequests,
                new { detail = "Too many failed attempts. Try again in 30 s." });
        if (!result.Succeeded)
            return Unauthorized(new { detail = "Wrong email or password" });

        // refresh market data once per login, like the original app
        var s = await _portfolio.GetSettingsAsync(user.Id);
        if (s is not null) _market.Invalidate(s.IndexId);
        return Ok(new { ok = true });
    }

    [HttpPost("/api/logout")]
    public async Task<IActionResult> Logout()
    {
        await _signIn.SignOutAsync();
        return Ok(new { ok = true });
    }

    [Authorize]
    [HttpGet("/api/me")]
    public async Task<IActionResult> Me()
    {
        var user = await _users.GetUserAsync(User)
                  ?? throw new ConflictException("Not authenticated");
        var s = await _portfolio.GetSettingsAsync(user.Id);
        return Ok(new
        {
            email = user.Email ?? user.UserName,
            is_admin = await _users.IsInRoleAsync(user, Roles.Admin),
            commission_percent = s?.CommissionPercent ?? 0.045m,
            index_id = s?.IndexId ?? "IMOEX",
        });
    }

    public sealed class SetupRequest
    {
        public string? Email { get; init; }
        public string? Password { get; init; }
    }

    public sealed class LoginRequest
    {
        public string? Email { get; init; }
        public string? Password { get; init; }
    }
}
