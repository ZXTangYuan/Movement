using System;
using ApexMovement.Core;

namespace ApexMovement.Tests;

/// <summary>
/// 纯数学碰撞世界：无限地面 + 若干 AABB。
///
/// 用途：在**无引擎**环境下复现求解器的输入/输出，用于确定性回归。
///
/// 说明：这是**测试替身**，不是通用物理。
/// 它按"轴分离 + 最早命中"处理，胶囊近似为竖直圆柱 + 球端。
/// 它与求解器的契约和 GodotMovementWorld 一致（Sweep/Overlaps/Raycast）。
/// </summary>
public sealed class AnalyticWorld : IMovementWorld, ISlipWorld, IFrictionWorld
{
    public float GroundY;

    public readonly System.Collections.Generic.List<(F3 Min, F3 Max)> Boxes = new();

    /// <summary>
    /// 打滑体积（对应 Godot 侧 <c>C_TriggerSlipSphere</c>）。
    /// 方向为 <see cref="F3.Zero"/> 时，按"从球心往外推"兜底 —— 与
    /// <c>GodotMovementWorld.SampleSlip</c> 的行为一致。
    /// </summary>
    public readonly System.Collections.Generic.List<SlipSphere> SlipSpheres = new();

    public readonly struct SlipSphere
    {
        public readonly F3 Center;
        public readonly float Radius;
        public readonly float Speed;
        public readonly float Accel;
        public readonly F3 Direction;
        public readonly bool LockToPlane;

        public SlipSphere(F3 center, float radius, float speed, float accel,
                          F3 direction, bool lockToPlane)
        {
            Center = center; Radius = radius; Speed = speed; Accel = accel;
            Direction = direction; LockToPlane = lockToPlane;
        }
    }

    /// <summary>
    /// 地面材质摩擦系数 μ。null = 没有材质信息（求解器用配置默认值）。
    /// 测试用它模拟"冰面 / 粗糙水泥"。
    /// </summary>
    public float? SurfaceFriction;

    public AnalyticWorld(float groundY = 0f) { GroundY = groundY; }

    // ═══════════════════════ IFrictionWorld ═══════════════════════

    public bool SampleFriction(F3 position, out float friction)
    {
        friction = -1f;
        if (SurfaceFriction is not float mu) return false;
        // 只在贴近地面时算"踩在这块材质上"
        if (position.Y - GroundY > 0.6f) return false;
        friction = mu;
        return true;
    }

    public void AddBox(F3 min, F3 max) => Boxes.Add((min, max));

    /// <summary>加一个打滑球。direction 留零 = 从球心往外推。</summary>
    public void AddSlipSphere(F3 center, float radius, float speed = 6f, float accel = 30f,
                              F3? direction = null, bool lockToPlane = false)
        => SlipSpheres.Add(new SlipSphere(center, radius, speed, accel,
                                          direction ?? F3.Zero, lockToPlane));

    // ═══════════════════════ ISlipWorld ═══════════════════════

    public bool SampleSlip(F3 pos, out SlipSample sample)
    {
        sample = default;
        if (SlipSpheres.Count == 0) return false;

        bool any = false;
        float bestSpeed = 0f, bestAccel = 0f;
        F3 dirSum = F3.Zero;
        F3 pushAway = F3.Zero;
        bool lockPlane = false;

        foreach (SlipSphere s in SlipSpheres)
        {
            F3 d = pos - s.Center;
            if (d.LengthSq() > s.Radius * s.Radius) continue;

            any = true;
            if (s.Speed > bestSpeed) bestSpeed = s.Speed;
            if (s.Accel > bestAccel) bestAccel = s.Accel;
            if (s.Direction.LengthSq() > 1e-8f) dirSum += s.Direction;
            lockPlane |= s.LockToPlane;

            F3 away = new(d.X, 0f, d.Z);
            if (away.LengthSq() > 1e-8f) pushAway += away;
        }

        if (!any) return false;

        F3 dir;
        if (dirSum.LengthSq() > 1e-8f) dir = dirSum.Normalized();
        else if (pushAway.LengthSq() > 1e-8f) dir = pushAway.Normalized();
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

    /// <summary>
    /// 轴分离扫掠：分别求 X/Y/Z 三轴的最早接触分数，取最小值。
    /// 圆柱近似：水平方向用 radius 外扩，竖直方向用 (halfHeight + radius) 外扩。
    /// </summary>
    public bool Sweep(F3 pos, float radius, float halfHeight, F3 delta,
                      out F3 hitNormal, out float fraction)
    {
        hitNormal = F3.Zero;
        fraction = 1f;

        float feet = pos.Y - halfHeight - radius;
        float head = pos.Y + halfHeight + radius;

        // ── 地面（无限平面） ──
        // 注意"已经在表面上或略微穿透"的情形：必须按 Godot CastMotion 的语义
        // 返回 fraction=0 的命中，而不是当成"没撞到"。
        // 否则角色每 tick 被贴地吸附下压 0.5mm 后就会掉进"探测不到地面"的黑洞，
        // 表现为"站着站着突然掉进虚空"（v0.2 重写时实测踩到过）。
        if (delta.Y < -1e-9f)
        {
            if (feet <= GroundY + 1e-6f)
            {
                fraction = 0f;
                hitNormal = F3.Up;
            }
            else if (feet + delta.Y <= GroundY)
            {
                float f = (GroundY - feet) / delta.Y;
                if (f >= 0f && f < fraction) { fraction = f; hitNormal = F3.Up; }
            }
        }

        // ── AABB ──
        foreach ((F3 min, F3 max) in Boxes)
        {
            // 水平外扩后的包围盒
            float bx0 = min.X - radius, bx1 = max.X + radius;
            float bz0 = min.Z - radius, bz1 = max.Z + radius;
            float by0 = min.Y, by1 = max.Y;

            // 先在**起点**判断当前是否与该盒的某个面水平重叠（决定能否撞到侧面）
            bool overlapZ = pos.Z > bz0 - 1e-6f && pos.Z < bz1 + 1e-6f;
            bool overlapX = pos.X > bx0 - 1e-6f && pos.X < bx1 + 1e-6f;
            bool overlapY = pos.Y + halfHeight + radius > by0 && pos.Y - halfHeight - radius < by1;

            // X 轴侧面
            if (delta.X > 1e-9f && overlapZ && overlapY && pos.X <= bx0)
            {
                float f = (bx0 - pos.X) / delta.X;
                if (f >= 0f && f < fraction) { fraction = f; hitNormal = new F3(-1, 0, 0); }
            }
            else if (delta.X < -1e-9f && overlapZ && overlapY && pos.X >= bx1)
            {
                float f = (bx1 - pos.X) / delta.X;
                if (f >= 0f && f < fraction) { fraction = f; hitNormal = new F3(1, 0, 0); }
            }

            // Z 轴侧面
            if (delta.Z > 1e-9f && overlapX && overlapY && pos.Z <= bz0)
            {
                float f = (bz0 - pos.Z) / delta.Z;
                if (f >= 0f && f < fraction) { fraction = f; hitNormal = new F3(0, 0, -1); }
            }
            else if (delta.Z < -1e-9f && overlapX && overlapY && pos.Z >= bz1)
            {
                float f = (bz1 - pos.Z) / delta.Z;
                if (f >= 0f && f < fraction) { fraction = f; hitNormal = new F3(0, 0, 1); }
            }

            // 顶面（可站立）：用"脚底外扩盒"判断水平重叠
            bool overXZ = pos.X > bx0 - 1e-6f && pos.X < bx1 + 1e-6f &&
                          pos.Z > bz0 - 1e-6f && pos.Z < bz1 + 1e-6f;
            // 顶点/底面同样必须容忍"刚好贴住"：
            // `f = (by1 - feet) / delta.Y` 在胶囊**恰好**停在台面上时会因为 float
            // 舍入算出 -5e-7 这种微负值，被 `f >= 0f` 判成"没撞到" —— 表现为
            // 翻越/攀爬上墙后落在台面上立刻"失重"往下掉（v0.3 实测踩到）。
            // 和上面地面分支一样：贴着就算 fraction=0 的命中（Godot CastMotion 语义）。
            const float contactEps = 1e-4f;   // 0.1mm
            if (delta.Y < -1e-9f && overXZ && feet >= by1 - contactEps)
            {
                float f = (by1 - feet) / delta.Y;
                if (f < 0f) f = 0f;
                if (f < fraction) { fraction = f; hitNormal = F3.Up; }
            }

            // 底面（撞头）
            if (delta.Y > 1e-9f && overXZ && head <= by0 + contactEps)
            {
                float f = (by0 - head) / delta.Y;
                if (f < 0f) f = 0f;
                if (f < fraction) { fraction = f; hitNormal = F3.Up * -1f; }
            }
        }

        if (fraction < 1f && hitNormal.Equals(F3.Zero)) fraction = 1f;
        return fraction < 1f;
    }

    public bool Overlaps(F3 pos, float radius, float halfHeight)
    {
        float feet = pos.Y - halfHeight - radius;
        float head = pos.Y + halfHeight + radius;
        if (feet < GroundY - 1e-4f) return true;

        foreach ((F3 min, F3 max) in Boxes)
        {
            if (pos.X + radius > min.X && pos.X - radius < max.X &&
                pos.Z + radius > min.Z && pos.Z - radius < max.Z &&
                head > min.Y && feet < max.Y)
                return true;
        }
        return false;
    }

    public bool Raycast(F3 origin, F3 dir, float maxDist, out F3 hitPoint, out F3 hitNormal)
    {
        hitPoint = F3.Zero;
        hitNormal = F3.Zero;
        float best = maxDist;
        bool found = false;

        if (dir.Y < -1e-9f)
        {
            float t = (GroundY - origin.Y) / dir.Y;
            // ★ v0.4.3 物理审计 P1-5：t == 0 必须算命中。
            //   原来要求 t > 1e-6，于是"脚底恰好贴地"时向下射线返回**无命中**，
            //   而 TryAttachWallGrab 的 WallGrabMinGroundClearance 守卫正是用
            //   "脚下没地"来放行 ⇒ 站在原地会被墙吸走（实测 z 由 −5.2 拉到 −4.4264）。
            //   射线起点就在表面上时，正确的语义是"距离 0 处有地"。
            if (t > -1e-6f && t < best) { best = MathF.Max(0f, t); found = true; hitNormal = F3.Up; }
        }

        foreach ((F3 min, F3 max) in Boxes)
        {
            if (RayAabb(origin, dir, min, max, out float t, out F3 n) && t > -1e-6f && t < best)
            {
                best = MathF.Max(0f, t); found = true; hitNormal = n;
            }
        }

        if (found) hitPoint = origin + dir * best;
        return found;
    }

    private static bool RayAabb(F3 o, F3 d, F3 min, F3 max, out float tHit, out F3 normal)
    {
        tHit = 0f; normal = F3.Zero;
        float tmin = 0f, tmax = float.MaxValue;
        F3 axis = F3.Zero;

        for (int i = 0; i < 3; i++)
        {
            float oi = i == 0 ? o.X : i == 1 ? o.Y : o.Z;
            float di = i == 0 ? d.X : i == 1 ? d.Y : d.Z;
            float mn = i == 0 ? min.X : i == 1 ? min.Y : min.Z;
            float mx = i == 0 ? max.X : i == 1 ? max.Y : max.Z;

            if (MathF.Abs(di) < 1e-8f)
            {
                if (oi < mn || oi > mx) return false;
                continue;
            }
            float t1 = (mn - oi) / di;
            float t2 = (mx - oi) / di;
            float sign = -1f;
            if (t1 > t2) { (t1, t2) = (t2, t1); sign = 1f; }

            if (t1 > tmin)
            {
                tmin = t1;
                axis = i == 0 ? new F3(sign, 0, 0)
                     : i == 1 ? new F3(0, sign, 0)
                              : new F3(0, 0, sign);
            }
            if (t2 < tmax) tmax = t2;
            if (tmin > tmax) return false;
        }

        tHit = tmin;
        normal = axis;
        return tmin > 0f;
    }
}
