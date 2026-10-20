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
public sealed class AnalyticWorld : IMovementWorld
{
    public float GroundY;

    public readonly System.Collections.Generic.List<(F3 Min, F3 Max)> Boxes = new();

    public AnalyticWorld(float groundY = 0f) { GroundY = groundY; }

    public void AddBox(F3 min, F3 max) => Boxes.Add((min, max));

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
            if (t > 1e-6f && t < best) { best = t; found = true; hitNormal = F3.Up; }
        }

        foreach ((F3 min, F3 max) in Boxes)
        {
            if (RayAabb(origin, dir, min, max, out float t, out F3 n) && t > 1e-6f && t < best)
            {
                best = t; found = true; hitNormal = n;
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
