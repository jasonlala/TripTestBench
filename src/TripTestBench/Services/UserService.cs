// ============================================================================
// 文件：UserService.cs
// 作用：本地账户管理（登录校验 / 退出 / 首次运行生成默认账户）。
//       安全说明：不保存明文密码，采用“随机盐 + SHA256(盐值 + 密码)”存储，
//       校验时使用 FixedTimeEquals 比较，避免时序攻击。
//       账户文件：%LOCALAPPDATA%\TripTestBench\users.json
// ============================================================================

using System.IO;                        // 文件读写
using System.Security.Cryptography;     // SHA256 / 随机盐 / 定长时间比较
using System.Text;                      // Encoding
using System.Text.Encodings.Web;        // 中文不转义
using System.Text.Json;                 // JSON 序列化
using TripTestBench.Models;             // UserAccount

namespace TripTestBench.Services;

/// <summary>本地账户服务：登录校验、退出、默认账户初始化。</summary>
public sealed class UserService
{
    /// <summary>账户文件序列化选项。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly List<UserAccount> _users = new(); // 内存中的账户列表

    /// <summary>当前登录用户（未登录为 null）。</summary>
    public UserAccount? CurrentUser { get; private set; }

    /// <summary>全部账户（只读视图，供管理界面扩展使用）。</summary>
    public IReadOnlyList<UserAccount> Users => _users;

    /// <summary>
    /// 加载账户：文件不存在或为空时自动创建默认账户。
    /// </summary>
    public void Load()
    {
        try
        {
            AppPaths.EnsureDataDir();

            if (!File.Exists(AppPaths.UsersFile))
            {
                CreateDefaultUsers();
                return;
            }

            var json = File.ReadAllText(AppPaths.UsersFile);
            var users = JsonSerializer.Deserialize<List<UserAccount>>(json, JsonOptions);
            _users.Clear();
            if (users is { Count: > 0 })
            {
                _users.AddRange(users);
            }
            else
            {
                CreateDefaultUsers();
                return;
            }

            // 兼容旧账户文件：确保 root 账户存在（已有 users.json 的现场升级后自动补充）
            EnsureRootUser();
        }
        catch (Exception ex)
        {
            Logger.Error("加载用户失败，使用默认账户", ex);
            CreateDefaultUsers();
        }
    }

    /// <summary>
    /// 校验账号密码。
    /// </summary>
    /// <param name="userName">用户名（忽略大小写、自动去空格）</param>
    /// <param name="password">明文密码（仅用于本地校验，不落盘）</param>
    /// <param name="user">校验通过时返回账户对象</param>
    public bool Validate(string userName, string password, out UserAccount? user)
    {
        user = null;
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
        {
            return false;
        }

        // 账户名忽略大小写匹配
        var account = _users.FirstOrDefault(u =>
            string.Equals(u.UserName, userName.Trim(), StringComparison.OrdinalIgnoreCase));

        if (account is null)
        {
            Logger.Warn($"登录失败，账户不存在：{userName}");
            return false;
        }

        // 计算哈希并使用定长时间比较（防时序攻击）
        var hash = ComputeHash(account.Salt, password);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(hash), Encoding.UTF8.GetBytes(account.PasswordHash)))
        {
            Logger.Warn($"登录失败，密码错误：{userName}");
            return false;
        }

        CurrentUser = account;
        user = account;
        Logger.User($"用户登录成功：{account.UserName}（{account.DisplayName}，角色 {account.Role}）");
        return true;
    }

    /// <summary>退出登录：清空当前用户并记录日志。</summary>
    public void Logout()
    {
        if (CurrentUser is not null)
        {
            Logger.User($"用户退出登录：{CurrentUser.UserName}");
        }

        CurrentUser = null;
    }

    /// <summary>计算密码哈希：SHA256(盐值 + 密码)，输出大写十六进制字符串。</summary>
    public static string ComputeHash(string salt, string password)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(salt + password)));

    /// <summary>首次运行创建默认账户：root / admin / operator，初始密码均为 123456。</summary>
    private void CreateDefaultUsers()
    {
        _users.Clear();
        _users.Add(CreateAccount("root", "超级管理员", "Admin", "123456"));
        _users.Add(CreateAccount("admin", "管理员", "Admin", "123456"));
        _users.Add(CreateAccount("operator", "操作员", "Operator", "123456"));
        SaveUsers();
        Logger.Info("已创建默认账户（root / admin / operator）");
    }

    /// <summary>确保 root 账户存在，旧账户文件缺少时自动补充并回写。</summary>
    private void EnsureRootUser()
    {
        if (_users.Any(u => string.Equals(u.UserName, "root", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _users.Add(CreateAccount("root", "超级管理员", "Admin", "123456"));
        SaveUsers();
        Logger.Info("已补充默认 root 账户（初始密码 123456）");
    }

    /// <summary>把内存账户列表写回 users.json。</summary>
    private void SaveUsers()
    {
        try
        {
            AppPaths.EnsureDataDir();
            File.WriteAllText(AppPaths.UsersFile, JsonSerializer.Serialize(_users, JsonOptions));
        }
        catch (Exception ex)
        {
            Logger.Error("写入账户文件失败", ex);
        }
    }

    /// <summary>创建单个账户：生成 8 字节随机盐并计算密码哈希。</summary>
    private static UserAccount CreateAccount(string userName, string displayName, string role, string password)
    {
        var salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        return new UserAccount
        {
            UserName = userName,
            DisplayName = displayName,
            Role = role,
            Salt = salt,
            PasswordHash = ComputeHash(salt, password)
        };
    }
}
