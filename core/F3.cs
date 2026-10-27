using System;

namespace ApexMovement.Core;

/// <summary>
/// 确定性三维向量（float32）。
///
/// **为什么不用 Godot.Vector3 / System.Numerics.Vector3**：
/// 两者的算术可能走 SIMD 实现（System.Numerics 明确会），不同 CPU/运行时下
/// 归约顺序可能不同，导致同一输入在不同机器上算出不同轨迹。
/// 本结构体只用标量 float 的加/减/乘/除与 MathF.Sqrt（IEEE-754 精确舍入），
/// 保证跨平台、跨帧率逐位一致。
/// </summary>
public struct F3 : IEquatable<F3>
{
    public float X;
    public float Y;
    public float Z;

    public F3(float x, float y, float z) { X = x; Y = y; Z = z; }

    public static readonly F3 Zero = new(0f, 0f, 0f);
    public static readonly F3 Up = new(0f, 1f, 0f);

    public static F3 operator +(F3 a, F3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static F3 operator -(F3 a, F3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static F3 operator -(F3 a) => new(-a.X, -a.Y, -a.Z);
    public static F3 operator *(F3 a, float s) => new(a.X * s, a.Y * s, a.Z * s);
    public static F3 operator *(float s, F3 a) => a * s;
    public static F3 operator /(F3 a, float s) => new(a.X / s, a.Y / s, a.Z / s);

    public float LengthSq() => X * X + Y * Y + Z * Z;
    public float Length() => MathF.Sqrt(LengthSq());

    public F3 Normalized()
    {
        float len = Length();
        return len > 1e-8f ? this / len : Zero;
    }

    public static float Dot(F3 a, F3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public static F3 Cross(F3 a, F3 b) => new(
        a.Y * b.Z - a.Z * b.Y,
        a.Z * b.X - a.X * b.Z,
        a.X * b.Y - a.Y * b.X);

    public static F3 Lerp(F3 a, F3 b, float t) => a + (b - a) * t;

    /// <summary>去掉 v 在单位法线 n 方向的分量（投影到 n 的切平面）。</summary>
    public static F3 ProjectOnPlane(F3 v, F3 n) => v - n * Dot(v, n);

    public bool Equals(F3 o) => X == o.X && Y == o.Y && Z == o.Z;
    public override bool Equals(object? obj) => obj is F3 o && Equals(o);
    public override int GetHashCode() => HashCode.Combine(X, Y, Z);
    public override string ToString() => $"({X:F4}, {Y:F4}, {Z:F4})";

    public Godot.Vector3 ToGodot() => new(X, Y, Z);
    public static F3 From(Godot.Vector3 v) => new(v.X, v.Y, v.Z);
}
