using Godot;
using System;
using System.Collections.Generic;

namespace ProjectA.Net;

/// <summary>
/// 网络核心门面（autoload 单例）。
///
/// 设计边界（重要）：
///   - GDScript 玩法代码只调用本类的强类型 API，永不直接接触 socket / RPC 底层。
///   - 本类永不理解“体素 / 行星”是什么，只搬运 <see cref="TerrainEdit"/> 等消息。
///
/// 两种权威模式共用这一套实现，仅 <see cref="Mode"/> 不同：
///   - ClientAuthoritative：broadcast 时本地已应用，直接转发给其他 peer。
///   - ServerAuthoritative：broadcast 时发给服务器；服务器校验后由权威方广播。
/// </summary>
public partial class NetworkCore : Node
{
    public static NetworkCore Instance { get; private set; } = null!;

    /// <summary>当前权威模式。</summary>
    public AuthorityMode Mode { get; private set; } = AuthorityMode.ClientAuthoritative;

    /// <summary>当前网络角色。</summary>
    public NetRole Role { get; private set; } = NetRole.Offline;

    /// <summary>本机 peer id。</summary>
    public int LocalPeerId => Multiplayer.GetUniqueId();

    /// <summary>收到远端地形编辑（GDScript 连接此信号）。参数：planetId, TerrainEdit。</summary>
    [Signal]
    public delegate void EditReceivedEventHandler(int planetId, GodotObject edit);

    /// <summary>peer 加入/离开（GDScript 用于生成/销毁远端玩家）。</summary>
    [Signal]
    public delegate void PeerJoinedEventHandler(int peerId);

    [Signal]
    public delegate void PeerLeftEventHandler(int peerId);

    /// <summary>网络状态变化（连接/断开/失败）。</summary>
    [Signal]
    public delegate void NetStateChangedEventHandler(int state, string detail);

    private readonly InterestGrid _interest = new(256.0f);
    private uint _localSequence;

    public override void _EnterTree()
    {
        Instance = this;
        Multiplayer.PeerConnected += OnPeerConnected;
        Multiplayer.PeerDisconnected += OnPeerDisconnected;
        Multiplayer.ConnectedToServer += OnConnectedToServer;
        Multiplayer.ConnectionFailed += OnConnectionFailed;
        Multiplayer.ServerDisconnected += OnServerDisconnected;
    }

    public override void _ExitTree()
    {
        Multiplayer.PeerConnected -= OnPeerConnected;
        Multiplayer.PeerDisconnected -= OnPeerDisconnected;
        Multiplayer.ConnectedToServer -= OnConnectedToServer;
        Multiplayer.ConnectionFailed -= OnConnectionFailed;
        Multiplayer.ServerDisconnected -= OnServerDisconnected;
    }

    // ───────────────────────── 生命周期 API（GDScript 调用） ─────────────────────────

    /// <summary>开房。mode 决定权威模型。好友联机用 ClientAuthoritative，专用服由 headless 启动。</summary>
    public Error Host(AuthorityMode mode, int port = 34567, int maxClients = 8)
    {
        var peer = new ENetMultiplayerPeer();
        var err = peer.CreateServer(port, maxClients);
        if (err != Error.Ok)
        {
            EmitSignal(SignalName.NetStateChanged, (int)err, "CreateServer failed");
            return err;
        }
        Multiplayer.MultiplayerPeer = peer;
        Mode = mode;
        Role = mode == AuthorityMode.ClientAuthoritative
            ? NetRole.HostClientAuthoritative
            : NetRole.DedicatedServer;
        EmitSignal(SignalName.NetStateChanged, (int)Error.Ok, $"hosting:{Role}");
        return Error.Ok;
    }

    public Error Join(string address, int port = 34567, AuthorityMode expectedMode = AuthorityMode.ClientAuthoritative)
    {
        var peer = new ENetMultiplayerPeer();
        var err = peer.CreateClient(address, port);
        if (err != Error.Ok)
        {
            EmitSignal(SignalName.NetStateChanged, (int)err, "CreateClient failed");
            return err;
        }
        Multiplayer.MultiplayerPeer = peer;
        Mode = expectedMode;
        Role = expectedMode == AuthorityMode.ClientAuthoritative
            ? NetRole.ClientAuthoritative
            : NetRole.ServerClient;
        return Error.Ok;
    }

    public void Disconnect()
    {
        Multiplayer.MultiplayerPeer?.Close();
        Multiplayer.MultiplayerPeer = null;
        Role = NetRole.Offline;
        _interest.Clear();
        EmitSignal(SignalName.NetStateChanged, (int)Error.Ok, "disconnected");
    }

    // ───────────────────────── 玩法侧 API ─────────────────────────

    /// <summary>
    /// 广播一条地形编辑。
    /// 调用前玩法代码应已“本地立即应用”（客户端预测），本方法只负责同步。
    /// </summary>
    public void BroadcastEdit(TerrainEdit edit)
    {
        if (Role == NetRole.Offline)
            return; // 单机：本地已应用，无需同步

        edit.OriginPeerId = LocalPeerId;
        edit.Sequence = ++_localSequence;
        var payload = edit.Serialize();

        if (Mode == AuthorityMode.ClientAuthoritative)
        {
            // 房主/客户端都可直接改：转发给除自己外所有 peer。
            Rpc(MethodName.RpcTerrainEdit, edit.PlanetId, payload);
        }
        else
        {
            if (Role == NetRole.DedicatedServer)
            {
                // 服务器自己改：广播给所有客户端。
                Rpc(MethodName.RpcTerrainEdit, edit.PlanetId, payload);
            }
            else
            {
                // 客户端改：发给服务器（服务器校验后再广播）。
                RpcId(1, MethodName.RpcTerrainEdit, edit.PlanetId, payload);
            }
        }
    }

    /// <summary>更新某 peer 的行星局部位置，供兴趣网格使用。</summary>
    public void ReportPeerPosition(int peerId, Vector3 planetLocalPos)
    {
        _interest.SetPeerPosition(peerId, planetLocalPos);
    }

    /// <summary>该位置附近的 peer（供服务器做定向发送 / 玩法做裁剪）。</summary>
    public int[] GetInterestedPeers(Vector3 planetLocalPos, int radiusCells = 1)
    {
        var list = new List<int>(_interest.GetInterestedPeers(planetLocalPos, radiusCells));
        return list.ToArray();
    }

    // ───────────────────────── RPC 落地 ─────────────────────────

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcTerrainEdit(int planetId, byte[] payload)
    {
        var edit = TerrainEdit.Deserialize(payload);

        if (Mode == AuthorityMode.ServerAuthoritative && Role == NetRole.DedicatedServer)
        {
            // 服务器权威：校验后向所有客户端广播（含发起者，便于对账）。
            if (!Validate(edit))
                return;
            Rpc(MethodName.RpcTerrainEdit, planetId, payload);
        }

        // 交给玩法层应用（GDScript 接收信号）。
        EmitSignal(SignalName.EditReceived, planetId, edit);
    }

    /// <summary>服务器权威模式下的编辑校验钩子。默认放行；可由玩法/反作弊扩展。</summary>
    private static bool Validate(TerrainEdit edit)
    {
        if (edit.Radius <= 0f || edit.Radius > 64f) return false;
        if (!float.IsFinite(edit.Position.X) ||
            !float.IsFinite(edit.Position.Y) ||
            !float.IsFinite(edit.Position.Z)) return false;
        return true;
    }

    // ───────────────────────── 事件转发 ─────────────────────────

    private void OnPeerConnected(long id)
    {
        EmitSignal(SignalName.PeerJoined, (int)id);
    }

    private void OnPeerDisconnected(long id)
    {
        _interest.RemovePeer((int)id);
        EmitSignal(SignalName.PeerLeft, (int)id);
    }

    private void OnConnectedToServer()
    {
        EmitSignal(SignalName.NetStateChanged, (int)Error.Ok, "connected");
    }

    private void OnConnectionFailed()
    {
        EmitSignal(SignalName.NetStateChanged, (int)Error.CantConnect, "connection failed");
    }

    private void OnServerDisconnected()
    {
        Role = NetRole.Offline;
        EmitSignal(SignalName.NetStateChanged, (int)Error.ConnectionError, "server disconnected");
    }
}
