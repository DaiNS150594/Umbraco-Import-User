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
    private readonly IUserGroupService _userGroupService;
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
        IUserGroupService userGroupService,
        IOptions<GlobalSettings> globalSettings,
        IShortStringHelper shortStringHelper,
        IWebHostEnvironment env)
    {
        _userManager = userManager;
        _userGroupService = userGroupService;
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

        await SeedGroups(config.Groups);
        await SeedUsers(config.Users);
    }

    private async Task SeedGroups(List<GroupConfig> groups)
    {
        var existingGroupsPage = await _userGroupService.GetAllAsync(0, int.MaxValue);
        var existingGroups = existingGroupsPage.Items.ToList();

        foreach (var g in groups)
        {
            var alias = NormalizeGroupAlias(g.Alias);
            var existingGroupByAlias = await _userGroupService.GetAsync(alias);
            if (existingGroupByAlias != null) continue;

            // Umbraco ships with default groups (e.g. "Administrators") and the name is unique in DB.
            var existingGroupByName = existingGroups.FirstOrDefault(x =>
                string.Equals(x.Name, g.Name, StringComparison.OrdinalIgnoreCase));
            if (existingGroupByName != null)
            {
                // If a group exists with the same name but a different alias, remember it so we can still
                // add users to the correct group later when config references the expected alias.
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
                await _userGroupService.CreateAsync(newGroup, Constants.Security.SuperUserKey);
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
            var existingUser = await _userManager.FindByEmailAsync(u.Email);
            if (existingUser != null)
            {
                existed++;
                await AddUserToGroups(existingUser.Key, u.Groups);
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
            await AddUserToGroups(identityUser.Key, u.Groups);
        }

        Console.WriteLine($"ℹ️ Seed users done. Created={created}, Existed={existed}, Failed={failed}");
    }

    private async Task AddUserToGroups(Guid userKey, IEnumerable<string> groupAliases)
    {
        foreach (var groupAlias in groupAliases)
        {
            var alias = NormalizeGroupAlias(groupAlias);
            if (_groupAliasRemap.TryGetValue(alias, out var remappedAlias))
            {
                alias = remappedAlias;
            }

            var group = await _userGroupService.GetAsync(alias);
            if (group is null)
            {
                Console.WriteLine($"⚠️ Seed user group missing: {alias}");
                continue;
            }

            var model = new Umbraco.Cms.Core.Models.UsersToUserGroupManipulationModel(group.Key, new[] { userKey });
            try
            {
                await _userGroupService.AddUsersToUserGroupAsync(model, Constants.Security.SuperUserKey);
                Console.WriteLine($"✅ Seed user added to group: {alias}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Seed user add-to-group failed: {alias}: {ex.Message}");
            }
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