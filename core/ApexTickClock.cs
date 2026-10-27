using System;

namespace ApexMovement.Core;

/// <summary>
/// 固定 tick 累加器 —— 把**任意**帧间隔切成等长 tick。
///
/// 用法：
/// <code>
/// clock.Accumulate(renderDelta);
/// while (clock.TryConsumeTick(out float dt)) core.Step(input, dt);
/// float alpha = clock.Alpha;  // 用于渲染插值
/// </code>
///
/// **死亡螺旋防护**：单帧最多追赶 <see cref="MaxCatchUpTicks"/> 个 tick，
/// 超出的时间被**丢弃并计数**（<see cref="DroppedTicks"/>）。
/// 丢弃而不是无限追赶，保证卡顿后不会越补越卡。
/// </summary>
public sealed class ApexTickClock
{
    private readonly float _fixedDelta;
    private readonly int _maxCatchUp;
    private float _accumulator;

    public ApexTickClock(float fixedDelta, int maxCatchUpTicks = 4)
    {
        if (fixedDelta <= 0f) throw new ArgumentOutOfRangeException(nameof(fixedDelta));
        if (maxCatchUpTicks < 1) throw new ArgumentOutOfRangeException(nameof(maxCatchUpTicks));
        _fixedDelta = fixedDelta;
        _maxCatchUp = maxCatchUpTicks;
    }

    /// <summary>本帧还有多少未消费时间（0..fixedDelta），用于渲染插值。</summary>
    public float Alpha => _accumulator / _fixedDelta;

    /// <summary>累计被丢弃的 tick 数（卡顿保护触发次数）。</summary>
    public int DroppedTicks { get; private set; }

    /// <summary>累计执行过的 tick 数。</summary>
    public long TotalTicks { get; private set; }

    public float FixedDelta => _fixedDelta;

    /// <summary>累加一帧的渲染时间。</summary>
    public void Accumulate(float frameDelta)
    {
        if (frameDelta <= 0f) return;

        _accumulator += frameDelta;

        // 死亡螺旋防护：超过上限的整 tick 直接丢弃
        float maxAllowed = _fixedDelta * _maxCatchUp;
        if (_accumulator > maxAllowed)
        {
            int dropped = (int)((_accumulator - maxAllowed) / _fixedDelta);
            if (dropped > 0)
            {
                DroppedTicks += dropped;
                _accumulator -= dropped * _fixedDelta;
            }
        }
    }

    /// <summary>尝试消费一个 tick；返回 false 表示本帧已无完整 tick。</summary>
    public bool TryConsumeTick(out float dt)
    {
        if (_accumulator >= _fixedDelta)
        {
            _accumulator -= _fixedDelta;
            TotalTicks++;
            dt = _fixedDelta;
            return true;
        }
        dt = 0f;
        return false;
    }

    public void Reset()
    {
        _accumulator = 0f;
        DroppedTicks = 0;
        TotalTicks = 0;
    }
}
