public class UserSeedConfig
{
    public List<GroupConfig> Groups { get; set; } = new();
    public List<UserConfig> Users { get; set; } = new();
}

public class GroupConfig
{
    public string Alias { get; set; } = "";
    public string Name { get; set; } = "";
    public string[] Sections { get; set; } = Array.Empty<string>();
}

public class UserConfig
{
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
    public string[] Groups { get; set; } = Array.Empty<string>();
}