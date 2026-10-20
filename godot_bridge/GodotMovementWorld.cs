using Godot;
using ApexMovement.Core;

namespace ApexMovement.GodotBridge;

/// <summary>
/// <see cref="IMovementWorld"/> 的 Godot 实现：用物理空间查询做胶囊扫掠/射线。
///
/// **确定性前提**（重要）：
/// 1. 所有查询必须发生在**固定 tick 边界**（由 ApexPlayer 保证）。
/// 2. 场景几何必须是**静态**的（StaticBody3D）。动态刚体会让结果依赖物理帧相位。
/// 3. 关闭物理插值（项目设置 physics/common/physics_interpolation=false）以免
///    查询结果受渲染插值影响。
///
/// 只读查询，不修改物理世界。
/// </summary>
public sealed class GodotMovementWorld : IMovementWorld, ISlipWorld
{
    private readonly PhysicsDirectSpaceState3D _space;
    private readonly uint _collisionMask;
    private readonly CapsuleShape3D _capsule = new();
    private readonly PhysicsShapeQueryParameters3D _shapeQuery = new();
    private readonly PhysicsRayQueryParameters3D _rayQuery = new();

    /// <summary>
    /// 打滑体积（Apex 的 <c>C_TriggerSlipSphere</c> 对应物）。
    /// 由 <see cref="SetSlipVolumes"/> 注入：任何带 <see cref="CollisionShape3D"/>
    /// 子节点的 <see cref="Node3D"/> 都行（球 / 盒 / 胶囊）。
    /// </summary>
    private readonly System.Collections.Generic.List<SlipVolume> _slipVolumes = new();

    private struct SlipVolume
    {
        public Node3D Node;
        public CollisionShape3D ShapeNode;
        public float Speed;
        public float Acceleration;
        public F3 Direction;       // 已归一化；零 = 用玩家当前水平速度方向
        public bool LockToPlane;
    }

    public GodotMovementWorld(PhysicsDirectSpaceState3D space, uint collisionMask = 0xFFFFFFFF)
    {
        _space = space;
        _collisionMask = collisionMask;
    }

    // ═══════════════════════ 打滑体积（ISlipWorld） ═══════════════════════

    /// <summary>
    /// 注册打滑体积。节点上的可调参数（用 <c>set_meta</c> 或导出属性都行）：
    /// <list type="bullet">
    /// <item><c>slip_speed</c>：目标水平速度 m/s（默认 6.0，对应 <c>m_slipSpeed</c>）</item>
    /// <item><c>slip_accel</c>：逼近加速度 m/s²（默认 30.0，对应 <c>m_slipAcceleration</c>）</item>
    /// <item><c>slip_dir</c>：推动方向（Vector3；缺省/零向量 = 沿用玩家进入时的水平方向）</item>
    /// <item><c>slip_lock_plane</c>：是否把垂直速度也抹掉（斜槽用，默认 false）</item>
    /// </list>
    /// 一般不用手动调用 —— <see cref="ApexPlayer"/> 会自动收集 <c>slip_volume</c> 组。
    /// </summary>
    public void SetSlipVolumes(System.Collections.Generic.IEnumerable<Node3D> volumes)
    {
        _slipVolumes.Clear();
        foreach (Node3D node in volumes)
        {
            if (node == null || !GodotObject.IsInstanceValid(node)) continue;

            CollisionShape3D? shapeNode = null;
            foreach (Node child in node.GetChildren())
            {
                if (child is CollisionShape3D cs && cs.Shape != null) { shapeNode = cs; break; }
            }
            if (shapeNode == null) continue;

            F3 dir = F3.Zero;
            if (node.HasMeta("slip_dir"))
            {
                Variant v = node.GetMeta("slip_dir");
                if (v.VariantType == Variant.Type.Vector3) dir = F3.From(v.AsVector3()).Normalized();
            }

            _slipVolumes.Add(new SlipVolume
            {
                Node = node,
                ShapeNode = shapeNode,
                Speed = node.HasMeta("slip_speed") ? (float)node.GetMeta("slip_speed").AsDouble() : 6.0f,
                Acceleration = node.HasMeta("slip_accel") ? (float)node.GetMeta("slip_accel").AsDouble() : 30.0f,
                Direction = dir,
                LockToPlane = node.HasMeta("slip_lock_plane") && node.GetMeta("slip_lock_plane").AsBool(),
            });
        }
    }

    /// <summary>当前注册的打滑体积数量（HUD / 自检用）。</summary>
    public int SlipVolumeCount => _slipVolumes.Count;

    public bool SampleSlip(F3 pos, out SlipSample sample)
    {
        sample = default;
        if (_slipVolumes.Count == 0) return false;

        Vector3 p = pos.ToGodot();
        bool any = false;
        float bestSpeed = 0f;
        float bestAccel = 0f;
        F3 dirSum = F3.Zero;
        bool lockPlane = false;
        Vector3 pushAway = Vector3.Zero;

        for (int i = 0; i < _slipVolumes.Count; i++)
        {
            SlipVolume vol = _slipVolumes[i];
            if (vol.Node == null || !GodotObject.IsInstanceValid(vol.Node)) continue;
            if (!ContainsPoint(vol, p)) continue;

            any = true;
            if (vol.Speed > bestSpeed) bestSpeed = vol.Speed;
            if (vol.Acceleration > bestAccel) bestAccel = vol.Acceleration;
            if (vol.Direction.LengthSq() > 1e-8f) dirSum += vol.Direction;
            lockPlane |= vol.LockToPlane;

            // 没配方向时的兜底：从体积中心往外推（对应 C_TriggerSlipSphere
            // "站进去就被往外挤"的语义）。
            Vector3 away = p - vol.Node.GlobalTransform.Origin;
            away.Y = 0f;
            if (away.LengthSquared() > 1e-6f) pushAway += away;
        }

        if (!any) return false;

        // 方向优先级：显式配置 > 从中心往外推 > 零（交给求解器用玩家速度兜底）
        F3 dir;
        if (dirSum.LengthSq() > 1e-8f) dir = dirSum.Normalized();
        else if (pushAway.LengthSquared() > 1e-6f) dir = F3.From(pushAway).Normalized();
        else dir = F3.Zero;

        sample = new SlipSample
        {
            Direction = dir,
            Speed = bestSpeed,
            Acceleration = bestAccel,
            LockToPlane = lockPlane,
        };
        return true;
    }

    private static bool ContainsPoint(in SlipVolume vol, Vector3 p)
    {
        Transform3D xform = vol.ShapeNode.GlobalTransform;
        Vector3 local = xform.AffineInverse() * p;

        switch (vol.ShapeNode.Shape)
        {
            case SphereShape3D sphere:
                return local.LengthSquared() <= sphere.Radius * sphere.Radius;

            case BoxShape3D box:
            {
                Vector3 half = box.Size * 0.5f;
                return Mathf.Abs(local.X) <= half.X &&
                       Mathf.Abs(local.Y) <= half.Y &&
                       Mathf.Abs(local.Z) <= half.Z;
            }

            case CapsuleShape3D capsule:
            {
                float cylHalf = Mathf.Max(0f, capsule.Height * 0.5f - capsule.Radius);
                float y = Mathf.Clamp(local.Y, -cylHalf, cylHalf);
                Vector3 axisPoint = new(0f, y, 0f);
                return (local - axisPoint).LengthSquared() <= capsule.Radius * capsule.Radius;
            }

            default:
                return false;
        }
    }

    private void ConfigureCapsule(float radius, float halfHeight)
    {
        // Godot CapsuleShape3D.height 是**圆柱段 + 两端半球**的总高
        _capsule.Radius = radius;
        _capsule.Height = halfHeight * 2f + radius * 2f;

        _shapeQuery.Shape = _capsule;
        _shapeQuery.CollisionMask = _collisionMask;
        _shapeQuery.CollideWithBodies = true;
        _shapeQuery.CollideWithAreas = false;
    }

    /// <summary>
    /// 探针越过接触点后再取法线的深度 (m)。必须 &gt; 0，否则形状只是"刚好接触"，
    /// GetRestInfo 找不到重叠而返回空字典。
    /// </summary>
    private const float ProbeDepth = 0.02f;

    public bool Sweep(F3 pos, float radius, float halfHeight, F3 delta,
                      out F3 hitNormal, out float fraction)
    {
        hitNormal = F3.Zero;
        fraction = 1f;

        if (delta.LengthSq() < 1e-14f) return false;

        ConfigureCapsule(radius, halfHeight);
        _shapeQuery.Motion = Vector3.Zero;
        _shapeQuery.Transform = new Transform3D(Basis.Identity, pos.ToGodot());

        // ── 先判"起点是否已经贴着几何体" ────────────────────────────
        // 这是本桥接层最容易出错的地方。Godot 的 cast_motion 在起点**已经接触**
        // 时直接返回 safe=0，无论运动方向为何 —— 站在地面上做水平移动也会得到
        // frac=0，于是"永远无法移动"。
        //
        // 正确语义：静止接触只应挡住"压进表面"的分量，切向/离开表面的运动必须放行。
        Godot.Collections.Dictionary restAtStart = _space.GetRestInfo(_shapeQuery);
        if (restAtStart.Count > 0 && restAtStart.TryGetValue("normal", out Variant rn))
        {
            F3 startNormal = F3.From(rn.AsVector3());
            if (startNormal.LengthSq() > 1e-12f)
            {
                startNormal = startNormal.Normalized();

                // 运动没有压进表面 → 视为自由移动，交给正常扫掠（通常也会直接放行）
                if (F3.Dot(delta.Normalized(), startNormal) >= -1e-4f)
                {
                    // 仍然走一次 CastMotion 以捕捉中途新出现的障碍，
                    // 但只有在它给出 safe>0 时才采信（safe==0 只是"起点已接触"的回声）。
                    _shapeQuery.Motion = delta.ToGodot();
                    float[] freeCast = _space.CastMotion(_shapeQuery);
                    _shapeQuery.Motion = Vector3.Zero;

                    if (freeCast == null || freeCast.Length < 2) return false;
                    if (freeCast[0] >= 1f || freeCast[0] <= 1e-6f) return false;

                    fraction = freeCast[0];
                    hitNormal = QueryNormalAt(pos + delta * fraction, radius, halfHeight, startNormal);
                    return true;
                }

                // 正在压进表面 → 立即挡住，法线就是该接触面的法线
                hitNormal = startNormal;
                fraction = 0f;
                return true;
            }
        }

        // ── 起点自由：标准扫掠 ──────────────────────────────────────
        // Godot 4.7: PhysicsShapeQueryParameters3D.Motion 承载运动向量，
        // CastMotion() 不接收参数，返回 [safe_fraction, unsafe_fraction]。
        _shapeQuery.Motion = delta.ToGodot();
        float[] result = _space.CastMotion(_shapeQuery);

        // **立刻清零**：后续 GetRestInfo / IntersectShape 不能带残留的运动向量，
        // 否则查询语义变成"扫掠到哪儿"而不是"此刻在哪儿"。
        _shapeQuery.Motion = Vector3.Zero;

        if (result == null || result.Length < 2) return false;

        float safe = result[0];
        if (safe >= 1f) return false;

        fraction = safe;
        hitNormal = QueryNormalAt(pos + delta * safe, radius, halfHeight, -delta.Normalized());
        return true;
    }

    /// <summary>
    /// 在接触点稍前方取真实法线。
    ///
    /// **必须用 GetRestInfo，不能用 IntersectShape**：Godot 4.7 的 IntersectShape
    /// 返回字典只有 [rid, collider_id, collider, shape]，**没有 normal 键**（实测 dump）。
    /// 用它取法线会静默走兜底分支，得到 -delta.Normalized() 这种伪法线，
    /// 进而 ProjectOnPlane 把水平速度吃光 —— 表现为"角色永远无法移动"。
    /// GetRestInfo 返回 [point, normal, rid, collider_id, shape, linear_velocity]。
    /// </summary>
    private F3 QueryNormalAt(F3 contactPos, float radius, float halfHeight, F3 fallback)
    {
        ConfigureCapsule(radius, halfHeight);
        _shapeQuery.Motion = Vector3.Zero;

        Vector3 dir = (-fallback).ToGodot().Normalized();
        if (dir.LengthSquared() < 1e-12f) dir = Vector3.Down;

        _shapeQuery.Transform = new Transform3D(
            Basis.Identity, contactPos.ToGodot() + dir * ProbeDepth);

        Godot.Collections.Dictionary rest = _space.GetRestInfo(_shapeQuery);
        if (rest.Count > 0 && rest.TryGetValue("normal", out Variant n))
        {
            F3 normal = F3.From(n.AsVector3());
            if (normal.LengthSq() > 1e-12f) return normal.Normalized();
        }

        return fallback.Normalized();
    }

    public bool Overlaps(F3 pos, float radius, float halfHeight)
    {
        ConfigureCapsule(radius, halfHeight);
        _shapeQuery.Motion = Vector3.Zero;
        _shapeQuery.Transform = new Transform3D(Basis.Identity, pos.ToGodot());
        return _space.IntersectShape(_shapeQuery, 1).Count > 0;
    }

    public bool Raycast(F3 origin, F3 dir, float maxDist, out F3 hitPoint, out F3 hitNormal)
    {
        hitPoint = F3.Zero;
        hitNormal = F3.Zero;

        _rayQuery.From = origin.ToGodot();
        _rayQuery.To = (origin + dir.Normalized() * maxDist).ToGodot();
        _rayQuery.CollisionMask = _collisionMask;
        _rayQuery.CollideWithBodies = true;
        _rayQuery.CollideWithAreas = false;

        Godot.Collections.Dictionary hit = _space.IntersectRay(_rayQuery);
        if (hit.Count == 0) return false;

        hitPoint = F3.From(hit["position"].AsVector3());
        hitNormal = F3.From(hit["normal"].AsVector3());
        return true;
    }
}
