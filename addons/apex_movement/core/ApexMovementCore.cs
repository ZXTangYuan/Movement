using Godot;

namespace ApexMovement;

/// <summary>
/// Apex 风格移动的**确定性求解器**（纯函数式：不碰引擎、不做射线、不分配节点）。
///
/// 算法血统来自 Apex Legends（Source 2 / Titanfall）：
///   - 地面：<c>Friction()</c> + <c>Accelerate()</c>（Source 经典加速，保留 bhop 手感）
///   - 空中：<c>AirAccelerate()</c>（wishspeed 被 AirSpeedCap 夹紧，得到急转 + 加速）
///   - 跳跃：coyote time + 输入缓冲 + 双跳（player_doubleJumpAirControl）
///   - 滑铲：slideRequiredStartSpeed 门槛 + slideAccel/Decel + 坡向重力缩放
///   - 蹬墙跑：wallrunMinSpeed 门槛 + 水平/垂直加速 + gravityRampUp 压制重力
///
/// 确定性契约：同一 (state, input, env, config, dt) 必然产生同一 (state')。
/// 因此客户端权威（本地预测）与服务器权威（重放校验）共用同一份代码。
/// </summary>
[GlobalClass]
public partial class ApexMovementCore : RefCounted
{
    private const float Epsilon = 1e-6f;

    /// <summary>
    /// 推进一帧移动。<paramref name="state"/> 会被**原地修改**（避免每帧分配）。
    /// 作为实例方法暴露，便于 GDScript 通过 <c>ApexMovementCore.new().Step(...)</c> 调用。
    /// </summary>
    /// <param name="state">当前状态（读写）。</param>
    /// <param name="input">本帧输入（确定性）。</param>
    /// <param name="env">本帧物理环境（由玩法层探测）。</param>
    /// <param name="cfg">调参表。</param>
    /// <param name="dt">固定步长（s）。</param>
    public void Step(ApexMoveState state, ApexMoveInput input, ApexMoveEnv env,
        ApexMoveConfig cfg, float dt)
    {
        if (dt <= 0f) return;

        // ── 0. 环境：up / 重力 / 地面 ──
        state.Up = SafeNormalize(env.Up, state.Up);
        // 起跳锁定期内忽略接地探测结果：探测距离内的地面会把刚起跳的玩家重新判为接地，
        // 导致「跳不起来」。锁定期间强制视为空中。
        state.JumpLockTimer = Mathf.Max(0f, state.JumpLockTimer - dt);
        bool lockAirborne = state.JumpLockTimer > 0f;
        state.OnGround = env.Grounded && !lockAirborne;
        state.GroundNormal = state.OnGround ? SafeNormalize(env.GroundNormal, state.Up) : state.Up;

        // ── 1. 计时器 ──
        state.JumpBufferTimer = Mathf.Max(0f, state.JumpBufferTimer - dt);
        state.CoyoteTimer = Mathf.Max(0f, state.CoyoteTimer - dt);
        state.SlideCooldownTimer = Mathf.Max(0f, state.SlideCooldownTimer - dt);
        state.TimeSinceWallrun += dt;
        if (input.JumpPressed) state.JumpBufferTimer = cfg.JumpBufferTime;

        // ── 2. 蹲伏状态（输入 → 目标 → 平滑） ──
        bool wantCrouch = input.IsDown(ApexMoveButton.Crouch);
        state.Crouching = wantCrouch;
        float targetFrac = wantCrouch ? 1f : 0f;
        state.CrouchFraction = Mathf.MoveToward(state.CrouchFraction, targetFrac,
            cfg.CrouchTransitionSpeed * dt);

        // ── 3. 分离 wishdir 的水平/垂直分量（沿 up） ──
        Vector3 wish = input.WishDir;
        // 去掉沿 up 的分量：移动始终在切平面内，升降由重力/跳跃决定
        wish -= state.Up * wish.Dot(state.Up);
        float wishSpeed = wish.Length();
        Vector3 wishDir = wishSpeed > Epsilon ? wish / wishSpeed : Vector3.Zero;

        // 蹲伏/冲刺的速度上限
        float speedCap = cfg.MaxSpeed;
        if (input.IsDown(ApexMoveButton.Sprint) && !state.Crouching)
            speedCap = cfg.SprintSpeed;
        if (state.Crouching)
            speedCap *= cfg.CrouchSpeedScale;

        // 输入强度 ∈ [0,1]（WishDir 已去掉沿 up 分量）。wishSpeed = 目标速度 = 上限 × 强度。
        // 摇杆/手柄不满推时可慢走；键盘满推时为速度上限。
        float inputStrength = Mathf.Clamp(wishSpeed, 0f, 1f);
        if (inputStrength <= Epsilon)
        {
            wishDir = Vector3.Zero;
            wishSpeed = 0f;
        }
        else
        {
            wishDir = wishDir.Normalized();
            wishSpeed = speedCap * inputStrength;
        }

        // ── 4. 蹬墙跑判定与推进 ──
        bool wallrunning = UpdateWallrun(state, input, env, cfg, dt, wishDir, wishSpeed);

        // ── 5. 滑铲判定 ──
        bool sliding = false;
        if (!wallrunning)
            sliding = UpdateSlide(state, input, env, cfg, dt, wishDir, wishSpeed);

        // ── 6. 跳跃（含双跳 / 滑铲跳 / 蹬墙跳） ──
        bool jumped = TryJump(state, input, env, cfg, wallrunning, sliding);

        // ── 7. 速度积分 ──
        if (!jumped)
        {
            if (wallrunning)
            {
                WallrunMove(state, input, env, cfg, dt, wishDir, wishSpeed);
            }
            else if (state.OnGround)
            {
                if (sliding)
                {
                    SlideMove(state, env, cfg, dt);
                }
                else
                {
                    ApplyFriction(state, env, cfg, dt);
                    GroundAccelerate(state, cfg, dt, wishDir, wishSpeed);
                }
                // 贴地：把速度投影到地面切平面（去掉指向地面的分量）。
                // 球面行星上「沿曲面走」由探测层的地面吸附（env.GroundSnap）保证，
                // 不在此处叠加向下速度，否则会污染垂直速度。
                ClampToGround(state, cfg);
            }
            else
            {
                AirAccelerate(state, cfg, dt, wishDir, wishSpeed);
                // 重力（滑铲在空中不减速，但这里只处理非滑铲）
                state.Velocity -= state.Up * (env.GravityMagnitude * dt);
            }
        }

        // ── 8. 限速 ──
        ClampVelocity(state, cfg);

        // ── 9. 积分位置 ──
        state.Position += state.Velocity * dt;

        // 地面吸附：接地且本帧未起跳时，用探测层给的偏移把脚底压回地表
        // （球面行星必需；否则切向运动会让角色沿切线飘离曲面）
        if (state.OnGround && !jumped && !wallrunning)
            state.Position += env.GroundSnap;

        // ── 10. 落地/离地状态迁移 ──
        if (state.OnGround)
        {
            state.AirJumpsUsed = 0;
            state.CoyoteTimer = 0.08f;
            if (state.Sliding && !sliding) state.Sliding = false;
        }
        else
        {
            if (state.Wallrunning && !wallrunning)
            {
                state.Wallrunning = false;
                state.TimeSinceWallrun = 0f;
                state.LastWallNormal = state.WallNormal;
            }
        }
        state.Sliding = sliding && state.OnGround;
        state.Wallrunning = wallrunning;
    }

    // ───────────────────────── 地面 ─────────────────────────

    /// <summary>Source 经典摩擦：低于 stopspeed 时用 stopspeed 封顶，避免低速抖动。</summary>
    private static void ApplyFriction(ApexMoveState s, ApexMoveEnv env, ApexMoveConfig cfg, float dt)
    {
        Vector3 vel = s.Velocity;
        // 只对水平分量施摩擦（垂直分量由贴地处理）
        float speed = vel.Length();
        if (speed < Epsilon) { s.Velocity = Vector3.Zero; return; }

        float control = speed < cfg.StopSpeed ? cfg.StopSpeed : speed;
        float drop = control * cfg.Friction * env.SurfaceFriction * dt;
        float newSpeed = Mathf.Max(0f, speed - drop);
        s.Velocity = vel * (newSpeed / speed);
    }

    /// <summary>
    /// Source 加速：只在「当前沿 wishdir 的速度 &lt; wishspeed」时施加，且增量按 (wishspeed - currentSpeed)
    /// 缩放 —— 这是 bhop/急转手感的核心，不会让玩家超过目标速度。
    /// </summary>
    private static void Accelerate(ApexMoveState s, float accel, float dt,
        Vector3 wishDir, float wishSpeed)
    {
        if (wishSpeed <= Epsilon || wishDir.LengthSquared() <= Epsilon) return;

        float currentSpeed = s.Velocity.Dot(wishDir);
        float addSpeed = wishSpeed - currentSpeed;
        if (addSpeed <= 0f) return;

        float accelSpeed = accel * wishSpeed * dt;
        if (accelSpeed > addSpeed) accelSpeed = addSpeed;

        s.Velocity += wishDir * accelSpeed;
    }

    private static void GroundAccelerate(ApexMoveState s, ApexMoveConfig cfg, float dt,
        Vector3 wishDir, float wishSpeed)
        => Accelerate(s, cfg.GroundAccelerate, dt, wishDir, wishSpeed);

    /// <summary>
    /// 空中加速：wishspeed 被 <see cref="ApexMoveConfig.AirSpeedCap"/> 夹紧，
    /// 于是「当前速度已很快」时 addSpeed 仍为正 → 可持续微调方向（air strafe）。
    /// </summary>
    private static void AirAccelerate(ApexMoveState s, ApexMoveConfig cfg, float dt,
        Vector3 wishDir, float wishSpeed)
    {
        float capped = Mathf.Min(wishSpeed, cfg.AirSpeedCap);
        Accelerate(s, cfg.AirAccelerate, dt, wishDir, capped);

        // airStrafe 额外加速（沿输入方向的水平分量），按速度比例衰减
        if (wishSpeed > Epsilon)
        {
            float horizSpeed = HorizontalSpeed(s);
            float taper = 1f;
            if (horizSpeed > cfg.MaxSpeed * cfg.AirStrafeTaperStart)
            {
                float t0 = cfg.MaxSpeed * cfg.AirStrafeTaperStart;
                float t1 = cfg.MaxSpeed * cfg.AirStrafeTaperFinish;
                taper = Mathf.Clamp(1f - (horizSpeed - t0) / Mathf.Max(t1 - t0, Epsilon), 0f, 1f);
            }
            s.Velocity += wishDir * (cfg.AirStrafeAcceleration * dt * taper);
        }

        // 空气阻力
        if (cfg.AirDrag > 0f)
            s.Velocity *= Mathf.Max(0f, 1f - cfg.AirDrag * dt);
    }

    /// <summary>
    /// 把速度沿地面法线投影（去掉指向地面的分量，允许沿坡滑行）。
    /// 不额外叠加向下速度——接地状态由玩法层的地面探测决定，避免残留垂直速度。
    /// </summary>
    private static void ClampToGround(ApexMoveState s, ApexMoveConfig cfg)
    {
        Vector3 n = s.GroundNormal;
        float vn = s.Velocity.Dot(n);
        if (vn < 0f)
            s.Velocity -= n * vn;      // 去掉指向地面的分量
    }

    // ───────────────────────── 跳跃 ─────────────────────────

    private static bool TryJump(ApexMoveState s, ApexMoveInput input, ApexMoveEnv env,
        ApexMoveConfig cfg, bool wallrunning, bool sliding)
    {
        bool wantJump = s.JumpBufferTimer > 0f;
        if (!wantJump) return false;

        // 蹬墙跳
        if (wallrunning)
        {
            Vector3 wallNormal = SafeNormalize(s.WallNormal, s.Up);
            Vector3 alongWall = s.Up.Cross(wallNormal).Normalized();
            // 沿墙 + 输入方向
            Vector3 dir = input.WishDir;
            dir -= s.Up * dir.Dot(s.Up);
            float inputSign = dir.LengthSquared() > Epsilon ? Mathf.Sign(dir.Dot(alongWall)) : 1f;
            Vector3 jumpVel = s.Velocity;
            jumpVel += s.Up * cfg.WallrunJumpUpSpeed;
            jumpVel += wallNormal * cfg.WallrunJumpOutwardSpeed;
            jumpVel += alongWall * (cfg.WallrunJumpInputDirSpeed * inputSign);
            s.Velocity = jumpVel;
            s.Wallrunning = false;
            s.TimeSinceWallrun = 0f;
            s.LastWallNormal = wallNormal;
            s.AirJumpsUsed = 0;      // 蹬墙跳重置双跳
            s.JumpBufferTimer = 0f;
            s.JumpLockTimer = cfg.JumpLockTime;
            return true;
        }

        // 滑铲跳（Apex 招牌：滑铲中起跳获得速度与高度）
        if (sliding && s.OnGround)
        {
            float v = JumpSpeed(cfg.SlideJumpHeight, env.GravityMagnitude);
            v = Mathf.Min(v, cfg.SlideMaxJumpSpeed);
            s.Velocity += s.Up * v;
            s.Sliding = false;
            s.SlideCooldownTimer = cfg.SlideCooldown;
            s.JumpBufferTimer = 0f;
            s.AirJumpsUsed = 1;      // 滑铲跳后仍保留一次双跳
            s.JumpLockTimer = cfg.JumpLockTime;
            return true;
        }

        // 地面跳 / coyote 跳
        bool canGroundJump = s.OnGround || s.CoyoteTimer > 0f;
        if (canGroundJump)
        {
            if (s.Crouching && !cfg.CanJumpWhileCrouched) return false;
            float v = JumpSpeed(cfg.JumpHeight, env.GravityMagnitude);
            // 保留水平速度，仅替换沿 up 的分量（Bunny-hop 基础）
            Vector3 horiz = s.Velocity - s.Up * s.Velocity.Dot(s.Up);
            s.Velocity = horiz + s.Up * v;
            s.OnGround = false;
            s.CoyoteTimer = 0f;
            s.JumpBufferTimer = 0f;
            s.AirJumpsUsed = 0;
            s.JumpLockTimer = cfg.JumpLockTime;
            return true;
        }

        // 双跳（空中）
        if (s.AirJumpsUsed < cfg.MaxAirJumps)
        {
            float v = JumpSpeed(cfg.DoubleJumpHeight, env.GravityMagnitude);
            Vector3 horiz = s.Velocity - s.Up * s.Velocity.Dot(s.Up);
            // 双跳空中控制：按输入方向微调水平速度
            if (input.WishDir.LengthSquared() > Epsilon)
            {
                Vector3 wish = input.WishDir;
                wish -= s.Up * wish.Dot(s.Up);
                horiz += wish.Normalized() * (cfg.DoubleJumpAirControl * horiz.Length());
            }
            s.Velocity = horiz + s.Up * v;
            s.AirJumpsUsed++;
            s.JumpBufferTimer = 0f;
            s.JumpLockTimer = cfg.JumpLockTime;
            return true;
        }

        return false;
    }

    /// <summary>由跳跃高度与重力反解初速度：v = sqrt(2·g·h)。</summary>
    private static float JumpSpeed(float height, float gravity)
        => Mathf.Sqrt(2f * Mathf.Max(gravity, 0.01f) * Mathf.Max(height, 0f));

    // ───────────────────────── 滑铲 ─────────────────────────

    private static bool UpdateSlide(ApexMoveState s, ApexMoveInput input, ApexMoveEnv env,
        ApexMoveConfig cfg, float dt, Vector3 wishDir, float wishSpeed)
    {
        float horiz = HorizontalSpeed(s);

        // 启动条件
        if (!s.Sliding)
        {
            if (s.SlideCooldownTimer > 0f) return false;
            if (!s.OnGround) return false;
            if (!s.Crouching && !input.IsDown(ApexMoveButton.Crouch)) return false;
            if (horiz < cfg.SlideRequiredStartSpeed) return false;
            // 坡度限制：太陡的坡不能起滑
            if (s.GroundNormal.Dot(s.Up) < cfg.SlideMaxAngleDot) return false;

            s.Sliding = true;
            s.SlideTime = 0f;
            s.SlideBoosted = false;
            return true;
        }

        // 维持条件
        s.SlideTime += dt;
        bool keepCrouch = s.Crouching || input.IsDown(ApexMoveButton.Crouch);
        if (!s.OnGround) return false;
        if (horiz < cfg.SlideStopSpeed && s.SlideTime > cfg.SlideMinDuration) return false;
        if (!keepCrouch && s.SlideTime > cfg.SlideMinDuration) return false;

        // 起滑加速（一次性）：沿当前速度方向叠加，受 cap 限制
        if (!s.SlideBoosted)
        {
            Vector3 dir = s.Velocity - s.Up * s.Velocity.Dot(s.Up);
            if (dir.LengthSquared() > Epsilon)
            {
                dir = dir.Normalized();
                float boosted = Mathf.Min(horiz + cfg.SlideSpeedBoost, cfg.SlideSpeedBoostCap);
                s.Velocity = s.Up * s.Velocity.Dot(s.Up) + dir * boosted;
            }
            s.SlideBoosted = true;
        }

        return true;
    }

    /// <summary>滑铲移动：沿坡向加速（下坡更快、上坡更慢）+ 摩擦减速 + 重力缩放。</summary>
    private static void SlideMove(ApexMoveState s, ApexMoveEnv env, ApexMoveConfig cfg, float dt)
    {
        Vector3 n = s.GroundNormal;

        // 把速度分解到地面切平面
        float vn = s.Velocity.Dot(n);
        s.Velocity -= n * vn;
        Vector3 horizVel = s.Velocity;

        // 坡度：重力沿坡的分量（下坡为正）
        Vector3 gravity = -s.Up * env.GravityMagnitude;
        Vector3 slopeAccel = gravity - n * gravity.Dot(n);
        float downhill = slopeAccel.Dot(SafeNormalize(horizVel, Vector3.Zero));
        float gravMul = downhill >= 0f ? cfg.SlideGravityDownhillMultiplier : cfg.SlideGravityUphillMultiplier;
        s.Velocity += slopeAccel * (gravMul * dt);

        // 滑铲前进加速：仅下坡助力，且总速度不超过 SlideSpeedBoostCap（硬上限，防止无限叠加）
        float speedNow = HorizontalSpeed(s);
        if (downhill > 0f && horizVel.LengthSquared() > Epsilon && speedNow < cfg.SlideSpeedBoostCap)
        {
            Vector3 fwd = horizVel.Normalized();
            float add = cfg.SlideAccel * cfg.SlideAccelForwardFrac * dt;
            add = Mathf.Min(add, cfg.SlideSpeedBoostCap - speedNow);
            s.Velocity += fwd * add;
        }

        // 滑铲减速 + 速度衰减
        float speed = HorizontalSpeed(s);
        float decel = cfg.SlideDecel * dt;
        float decay = speed * cfg.SlideVelocityDecay * dt;
        float drop = decel + decay;
        if (speed > Epsilon)
        {
            float newSpeed = Mathf.Max(0f, speed - drop);
            Vector3 horiz = s.Velocity - s.Up * s.Velocity.Dot(s.Up);
            if (horiz.LengthSquared() > Epsilon)
                s.Velocity = s.Up * s.Velocity.Dot(s.Up) + horiz.Normalized() * newSpeed;
        }
        // 滑铲重力缩放（沿 up 方向）
        s.Velocity -= s.Up * (env.GravityMagnitude * cfg.SlideGravityScale * dt);
    }

    // ───────────────────────── 蹬墙跑 ─────────────────────────

    private static bool UpdateWallrun(ApexMoveState s, ApexMoveInput input, ApexMoveEnv env,
        ApexMoveConfig cfg, float dt, Vector3 wishDir, float wishSpeed)
    {
        if (!cfg.WallrunEnabled) return false;
        if (s.OnGround) return false;

        // 已在墙跑：检查是否应脱离
        if (s.Wallrunning)
        {
            s.WallrunTime += dt;
            bool stillOnWall = env.HasWall && env.WallIsRunable
                && env.WallNormal.Dot(s.WallNormal) > 0.5f;
            bool tooLong = s.WallrunTime > cfg.WallrunTimeLimit;
            bool tooSlow = HorizontalSpeed(s) < cfg.WallrunMinSpeed * 0.5f;
            if (!stillOnWall || tooLong || tooSlow)
            {
                s.Wallrunning = false;
                s.TimeSinceWallrun = 0f;
                s.LastWallNormal = s.WallNormal;
                return false;
            }
            s.WallNormal = env.WallNormal;
            return true;
        }

        // 启动条件
        if (!env.HasWall || !env.WallIsRunable) return false;
        if (env.WallDistance > cfg.WallrunAllowedWallDistance) return false;
        if (HorizontalSpeed(s) < cfg.WallrunMinSpeed) return false;
        // 不能蹬刚蹬过的同一面墙
        if (s.TimeSinceWallrun < 0.3f &&
            s.LastWallNormal.Dot(env.WallNormal) > cfg.WallrunSameWallDot)
            return false;
        // 墙面必须与运动方向有夹角（不能正对墙跑）
        Vector3 vel = s.Velocity - s.Up * s.Velocity.Dot(s.Up);
        if (vel.LengthSquared() > Epsilon)
        {
            Vector3 vd = vel.Normalized();
            if (Mathf.Abs(vd.Dot(env.WallNormal)) > 0.9f) return false;
        }

        s.Wallrunning = true;
        s.WallrunTime = 0f;
        s.WallNormal = env.WallNormal;
        s.AirJumpsUsed = 0;
        return true;
    }

    private static void WallrunMove(ApexMoveState s, ApexMoveInput input, ApexMoveEnv env,
        ApexMoveConfig cfg, float dt, Vector3 wishDir, float wishSpeed)
    {
        Vector3 wallNormal = SafeNormalize(s.WallNormal, s.Up);
        Vector3 alongWall = s.Up.Cross(wallNormal).Normalized();

        // 水平速度：沿墙方向加速，受 wallrunMaxSpeedHorizontal 限制
        Vector3 horizVel = s.Velocity - s.Up * s.Velocity.Dot(s.Up);
        float along = horizVel.Dot(alongWall);
        // 输入方向决定沿墙前进方向
        Vector3 dir = input.WishDir;
        dir -= s.Up * dir.Dot(s.Up);
        float inputSign = dir.LengthSquared() > Epsilon ? Mathf.Sign(dir.Dot(alongWall)) : Mathf.Sign(along);
        if (inputSign == 0f) inputSign = 1f;

        if (Mathf.Abs(along) < cfg.WallrunMaxSpeedHorizontal)
            along += inputSign * cfg.WallrunAccelerateHorizontal * dt;
        along = Mathf.Clamp(along, -cfg.WallrunMaxSpeedHorizontal, cfg.WallrunMaxSpeedHorizontal);
        // 去掉垂直墙面分量
        horizVel -= wallNormal * horizVel.Dot(wallNormal);
        horizVel = alongWall * along;

        // 垂直速度：沿墙加速上升（重力被压制，wallrunGravityRampUpTime 内逐渐恢复）
        float vert = s.Velocity.Dot(s.Up);
        vert += cfg.WallrunAccelerateVertical * dt;
        vert = Mathf.Min(vert, cfg.WallrunMaxSpeedVertical);
        // 重力压制
        float gravScale = Mathf.Clamp(s.WallrunTime / Mathf.Max(cfg.WallrunGravityRampUpTime, Epsilon), 0f, 1f);
        vert -= env.GravityMagnitude * gravScale * dt;

        s.Velocity = horizVel + s.Up * vert;
    }

    // ───────────────────────── 工具 ─────────────────────────

    private static void ClampVelocity(ApexMoveState s, ApexMoveConfig cfg)
    {
        float sp = s.Velocity.Length();
        if (sp > cfg.MaxVelocity)
            s.Velocity = s.Velocity * (cfg.MaxVelocity / sp);
    }

    private static float HorizontalSpeed(ApexMoveState s)
    {
        Vector3 v = s.Velocity;
        v -= s.Up * v.Dot(s.Up);
        return v.Length();
    }

    private static Vector3 SafeNormalize(Vector3 v, Vector3 fallback)
        => v.LengthSquared() > Epsilon ? v.Normalized() : fallback;
}
