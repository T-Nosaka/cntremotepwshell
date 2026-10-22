namespace cntremotepwshell;

/// <summary>
/// HTTP 用の接続プロファイル。メモリのみで、再起動すると消える。
/// </summary>
public sealed class ProfileStore
{
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan AbsoluteTimeout = TimeSpan.FromHours(8);

    private readonly object _gate = new();
    private readonly Dictionary<string, Profile> _profiles = new(StringComparer.Ordinal);

    public string Register(string host, string user, string password, bool https = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var now = DateTimeOffset.UtcNow;
        var profile = new Profile
        {
            Id = Guid.NewGuid().ToString("D"),
            Host = host.Trim(),
            UserName = user.Trim(),
            Password = password,
            UseHttps = https,
            CreatedAt = now,
            LastUsedAt = now,
        };

        lock (_gate)
        {
            PurgeExpired(now);
            _profiles[profile.Id] = profile;
        }

        return profile.Id;
    }

    /// <summary>
    /// 実行時だけ最終利用を更新する。期限切れは破棄して null。
    /// </summary>
    public Profile? Use(string id)
    {
        return Touch(id, updateLastUsed: true);
    }

    /// <summary>
    /// 状態確認。期限は延ばさない。
    /// </summary>
    public Profile? Peek(string id)
    {
        return Touch(id, updateLastUsed: false);
    }

    public void Release(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;

        lock (_gate)
            _profiles.Remove(id.Trim());
    }

    private Profile? Touch(string id, bool updateLastUsed)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            PurgeExpired(now);
            if (!_profiles.TryGetValue(id.Trim(), out var profile))
                return null;

            if (updateLastUsed)
                profile.LastUsedAt = now;

            return profile;
        }
    }

    private void PurgeExpired(DateTimeOffset now)
    {
        var expired = _profiles
            .Where(pair => pair.Value.IsExpired(now))
            .Select(pair => pair.Key)
            .ToArray();

        foreach (var id in expired)
            _profiles.Remove(id);
    }

    public sealed class Profile
    {
        public required string Id { get; init; }
        public required string Host { get; init; }
        public required string UserName { get; init; }
        public required string Password { get; init; }
        public bool UseHttps { get; init; } = true;
        public required DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset LastUsedAt { get; set; }

        public DateTimeOffset ExpiresAt =>
            Min(CreatedAt + AbsoluteTimeout, LastUsedAt + IdleTimeout);

        public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

        public WinRmSettings ToSettings()
        {
            return new WinRmSettings
            {
                ConnectionUri = WinRmSettings.CreateConnectionUri(Host, UseHttps),
                UserName = UserName,
                Password = Password,
                SkipCertificateCheck = true,
            };
        }

        private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) =>
            left <= right ? left : right;
    }
}
