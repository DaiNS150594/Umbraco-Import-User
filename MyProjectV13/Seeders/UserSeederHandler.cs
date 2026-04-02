using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Strings;


public class UserSeederHandler : INotificationHandler<UmbracoApplicationStartedNotification>
{
    private readonly IBackOfficeUserManager _userManager;
    private readonly IUserService _userService;
    private readonly IWebHostEnvironment _env;
    private readonly GlobalSettings _globalSettings;
    private readonly IShortStringHelper _shortStringHelper;
    private readonly Dictionary<string, string> _groupAliasRemap = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public UserSeederHandler(
        IBackOfficeUserManager userManager,
        IUserService userService,
        IOptions<GlobalSettings> globalSettings,
        IShortStringHelper shortStringHelper,
        IWebHostEnvironment env)
    {
        _userManager = userManager;
        _userService = userService;
        _globalSettings = globalSettings.Value;
        _shortStringHelper = shortStringHelper;
        _env = env;
    }

    public void Handle(UmbracoApplicationStartedNotification notification)
        => HandleAsync(notification).GetAwaiter().GetResult();

    private async Task HandleAsync(UmbracoApplicationStartedNotification notification)
    {
        var path = Path.Combine(_env.ContentRootPath, "Config", "users.json");

        if (!File.Exists(path)) return;

        UserSeedConfig? config;
        try
        {
            var json = await File.ReadAllTextAsync(path);
            config = JsonSerializer.Deserialize<UserSeedConfig>(json, _jsonOptions);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Seed users.json deserialize failed: {ex.Message}");
            return;
        }

        if (config == null) return;

        SeedGroups(config.Groups);
        await SeedUsers(config.Users);
    }

    private void SeedGroups(List<GroupConfig> groups)
    {
        var existingGroups = _userService.GetAllUserGroups().ToList();

        foreach (var g in groups)
        {
            var alias = NormalizeGroupAlias(g.Alias);
            var existingGroupByAlias = _userService.GetUserGroupByAlias(alias);
            if (existingGroupByAlias != null) continue;

            // Umbraco ships with default groups (e.g. "Administrators") and the name is unique in DB.
            var existingGroupByName = existingGroups.FirstOrDefault(x =>
                string.Equals(x.Name, g.Name, StringComparison.OrdinalIgnoreCase));
            if (existingGroupByName != null)
            {
                _groupAliasRemap[alias] = existingGroupByName.Alias;
                Console.WriteLine($"⚠️ Seed group skipped (name exists): {g.Name} ({alias})");
                continue;
            }

            var newGroup = new UserGroup(_shortStringHelper)
            {
                Alias = alias,
                Name = g.Name,
            };

            if (g.Sections?.Length > 0)
            {
                if (g.Sections.Contains("*"))
                {
                    // Treat "*" as "no explicit restriction" (allow all).
                }
                else
                {
                    foreach (var section in g.Sections.Where(s => !string.IsNullOrWhiteSpace(s)))
                    {
                        newGroup.AddAllowedSection(section);
                    }
                }
            }

            try
            {
                _userService.Save(newGroup);
                // Alias có thể bị chuẩn hóa (ShortStringHelper), cần map lại để gán user đúng nhóm.
                var persistedAlias = newGroup.Alias;
                if (!string.Equals(persistedAlias, alias, StringComparison.Ordinal))
                {
                    _groupAliasRemap[alias] = persistedAlias;
                    _groupAliasRemap[g.Alias] = persistedAlias;
                }

                existingGroups.Add(newGroup);
                Console.WriteLine($"✅ Seed group created: {newGroup.Name} ({newGroup.Alias})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Seed group failed: {g.Name} ({alias}): {ex.Message}");
            }
        }
    }

    private async Task SeedUsers(List<UserConfig> users)
    {
        var created = 0;
        var existed = 0;
        var failed = 0;

        foreach (var u in users)
        {
            var existingIdentity = await _userManager.FindByEmailAsync(u.Email);
            var membershipUser = _userService.GetByEmail(u.Email);

            if (existingIdentity != null || membershipUser != null)
            {
                existed++;
                if (membershipUser is null)
                {
                    Console.WriteLine($"⚠️ Seed skip groups: backoffice user exists but IUser not found: {u.Email}");
                    continue;
                }

                AddUserToGroups(membershipUser, u.Groups);
                continue;
            }

            var identityUser = BackOfficeIdentityUser.CreateNew(
                _globalSettings,
                u.Email,
                u.Email,
                "en-US",
                u.Name
            );

            var result = await _userManager.CreateAsync(identityUser, u.Password);

            if (!result.Succeeded)
            {
                Console.WriteLine($"❌ Create user failed: {u.Email} - {string.Join("; ", result.Errors.Select(e => e.Description))}");
                failed++;
                continue;
            }

            Console.WriteLine($"✅ Seed user created: {u.Email}");
            created++;

            membershipUser = _userService.GetByEmail(u.Email);
            if (membershipUser is null)
            {
                Console.WriteLine($"⚠️ Seed user created in Identity but IUser not found: {u.Email}");
                failed++;
                continue;
            }

            AddUserToGroups(membershipUser, u.Groups);
        }

        Console.WriteLine($"ℹ️ Seed users done. Created={created}, Existed={existed}, Failed={failed}");
    }

    private void AddUserToGroups(IUser user, IEnumerable<string> groupAliases)
    {
        var anyAdded = false;

        foreach (var groupAlias in groupAliases)
        {
            var alias = NormalizeGroupAlias(groupAlias);
            if (_groupAliasRemap.TryGetValue(alias, out var remappedAlias))
            {
                alias = remappedAlias;
            }

            var group = _userService.GetUserGroupByAlias(alias);
            if (group is null)
            {
                Console.WriteLine($"⚠️ Seed user group missing: {alias}");
                continue;
            }

            if (user.Groups.Any(g => string.Equals(g.Alias, group.Alias, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            user.AddGroup((IReadOnlyUserGroup)group);
            anyAdded = true;
            Console.WriteLine($"✅ Seed user added to group: {alias}");
        }

        if (!anyAdded)
        {
            return;
        }

        try
        {
            _userService.Save(user);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Seed user save failed: {ex.Message}");
        }
    }

    private static string NormalizeGroupAlias(string alias)
    {
        if (string.Equals(alias, "administrators", StringComparison.OrdinalIgnoreCase))
        {
            // Umbraco built-in admin group alias is "admin"
            return Constants.Security.AdminGroupAlias;
        }

        return alias;
    }
}
