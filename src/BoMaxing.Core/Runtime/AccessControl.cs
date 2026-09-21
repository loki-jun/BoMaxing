using System.Security.Cryptography;

namespace BoMaxing.Core.Runtime;

public enum UserRole
{
    Operator,
    Engineer,
    Administrator
}

public enum Permission
{
    RunWorkflow,
    EditWorkflow,
    EditDevices,
    EditRecipes,
    AcknowledgeAlarm,
    DeployProject,
    ManageUsers
}

public sealed record UserAccount(
    string UserName,
    UserRole Role,
    byte[] PasswordSalt,
    byte[] PasswordHash,
    bool Enabled = true);

public sealed record UserSession(string UserName, UserRole Role);

public sealed class AccessControlService
{
    private static readonly IReadOnlyDictionary<UserRole, IReadOnlySet<Permission>> RolePermissions =
        new Dictionary<UserRole, IReadOnlySet<Permission>>
        {
            [UserRole.Operator] = new HashSet<Permission>
            {
                Permission.RunWorkflow,
                Permission.AcknowledgeAlarm
            },
            [UserRole.Engineer] = new HashSet<Permission>
            {
                Permission.RunWorkflow,
                Permission.EditWorkflow,
                Permission.EditDevices,
                Permission.EditRecipes,
                Permission.AcknowledgeAlarm,
                Permission.DeployProject
            },
            [UserRole.Administrator] = Enum.GetValues<Permission>().ToHashSet()
        };

    private readonly Dictionary<string, UserAccount> _users =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly AuditTrail _auditTrail;

    public AccessControlService(AuditTrail auditTrail)
    {
        _auditTrail = auditTrail ?? throw new ArgumentNullException(nameof(auditTrail));
    }

    public void AddUser(string userName, string password, UserRole role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ValidatePassword(password);
        if (_users.ContainsKey(userName))
        {
            throw new InvalidOperationException($"User '{userName}' already exists.");
        }

        var salt = RandomNumberGenerator.GetBytes(16);
        _users[userName] = new UserAccount(
            userName,
            role,
            salt,
            HashPassword(password, salt));
        _auditTrail.Append("user.add", "system", userName);
    }

    public bool Authenticate(string userName, string password, out UserSession? session)
    {
        session = null;
        if (!_users.TryGetValue(userName, out var account) || !account.Enabled)
        {
            _auditTrail.Append("user.login.failed", userName, userName);
            return false;
        }

        var candidate = HashPassword(password, account.PasswordSalt);
        if (!CryptographicOperations.FixedTimeEquals(candidate, account.PasswordHash))
        {
            _auditTrail.Append("user.login.failed", userName, userName);
            return false;
        }

        session = new UserSession(account.UserName, account.Role);
        _auditTrail.Append("user.login", account.UserName, account.UserName);
        return true;
    }

    public bool Can(UserSession session, Permission permission)
    {
        ArgumentNullException.ThrowIfNull(session);
        return RolePermissions[session.Role].Contains(permission);
    }

    public void Demand(UserSession session, Permission permission)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!Can(session, permission))
        {
            _auditTrail.Append("permission.denied", session.UserName, permission.ToString());
            throw new UnauthorizedAccessException(
                $"User '{session.UserName}' does not have permission '{permission}'.");
        }
    }

    private static byte[] HashPassword(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations: 100_000,
            HashAlgorithmName.SHA256,
            outputLength: 32);

    private static void ValidatePassword(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        if (password.Length < 8)
        {
            throw new ArgumentException("Password must contain at least 8 characters.", nameof(password));
        }
    }
}
