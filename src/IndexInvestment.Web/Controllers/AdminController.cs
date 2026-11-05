using IndexInvestment.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IndexInvestment.Web.Controllers;

/// <summary>Account management — admin only.</summary>
[ApiController]
[Authorize(Roles = Roles.Admin)]
public class AdminController : ControllerBase
{
    private readonly UserManager<IdentityUser> _users;
    private readonly RoleManager<IdentityRole> _roles;
    private readonly PortfolioService _portfolio;

    public AdminController(
        UserManager<IdentityUser> users,
        RoleManager<IdentityRole> roles,
        PortfolioService portfolio)
    {
        _users = users;
        _roles = roles;
        _portfolio = portfolio;
    }

    [HttpGet("/admin/users")]
    public async Task<IActionResult> List()
    {
        var rows = new List<object>();
        await foreach (var u in _users.Users.AsAsyncEnumerable())
        {
            var s = await _portfolio.GetSettingsAsync(u.Id);
            rows.Add(new
            {
                id = u.Id,
                email = u.Email ?? u.UserName,
                is_admin = await _users.IsInRoleAsync(u, Roles.Admin),
                locked = u.LockoutEnabled && u.LockoutEnd is { } le && le > DateTimeOffset.UtcNow,
                commission_percent = s?.CommissionPercent ?? 0.045m,
                index_id = s?.IndexId ?? "IMOEX",
            });
        }
        return Ok(new { rows });
    }

    [HttpPost("/admin/users")]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Email))
            throw new ValidationError("Email is required");
        if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 4)
            throw new ValidationError("Password must be at least 4 characters");

        var email = req.Email.Trim();
        var user = new IdentityUser { UserName = email, Email = email };
        var created = await _users.CreateAsync(user, req.Password);
        if (!created.Succeeded)
            throw new ConflictException(string.Join("; ", created.Errors.Select(e => e.Description)));
        await _portfolio.SaveSettingsAsync(user.Id, 0.045m, "IMOEX");
        return Ok(new { ok = true });
    }

    [HttpPatch("/admin/users/{id}/password")]
    public async Task<IActionResult> ResetPassword(string id, [FromBody] ResetPasswordRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 4)
            throw new ValidationError("Password must be at least 4 characters");
        var user = await _users.FindByIdAsync(id)
                    ?? throw new ConflictException("User not found");
        await _users.RemovePasswordAsync(user);
        var res = await _users.AddPasswordAsync(user, req.Password);
        if (!res.Succeeded)
            throw new ConflictException(string.Join("; ", res.Errors.Select(e => e.Description)));
        return Ok(new { ok = true });
    }

    [HttpPatch("/admin/users/{id}/lock")]
    public async Task<IActionResult> SetLock(string id, [FromBody] SetLockRequest req)
    {
        var user = await _users.FindByIdAsync(id)
                    ?? throw new ConflictException("User not found");
        if (user.Id == (await _users.GetUserAsync(User))!.Id)
            throw new ConflictException("You cannot lock your own account");
        await _users.SetLockoutEndDateAsync(user, req.Locked ? DateTimeOffset.MaxValue : null);
        return Ok(new { ok = true });
    }

    public sealed class CreateUserRequest
    {
        public string? Email { get; init; }
        public string? Password { get; init; }
    }

    public sealed class ResetPasswordRequest
    {
        public string? Password { get; init; }
    }

    public sealed class SetLockRequest
    {
        public bool Locked { get; init; }
    }
}
