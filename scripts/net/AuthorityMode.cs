using Godot;
using System;

namespace ProjectA.Net;

/// <summary>
/// 权威模式。同一套网络核心在两种模式下工作，仅改变“谁有权改世界”。
/// </summary>
public enum AuthorityMode
{
    /// <summary>好友联机：房主与客户端都能直接改世界，本地立即生效后广播。低延迟、零服务器成本。</summary>
    ClientAuthoritative = 0,

    /// <summary>专用服务器：只有服务器能改世界，客户端发请求、服务器校验后广播。防作弊。</summary>
    ServerAuthoritative = 1,
}

/// <summary>
/// 网络角色，由 NetworkCore 根据 Host/Join 结果推断。
/// </summary>
public enum NetRole
{
    Offline,
    /// <summary>客户端权威模式下的房主（自己就是权威，也是普通 peer）。</summary>
    HostClientAuthoritative,
    /// <summary>客户端权威模式下的加入者。</summary>
    ClientAuthoritative,
    /// <summary>专用服务器。</summary>
    DedicatedServer,
    /// <summary>连接专用服务器的客户端。</summary>
    ServerClient,
}

public static class NetRoleExtensions
{
    public static bool IsServerSide(this NetRole role) =>
        role is NetRole.HostClientAuthoritative or NetRole.DedicatedServer;

    public static bool IsAuthority(this NetRole role) =>
        role is NetRole.HostClientAuthoritative or NetRole.DedicatedServer;
}
