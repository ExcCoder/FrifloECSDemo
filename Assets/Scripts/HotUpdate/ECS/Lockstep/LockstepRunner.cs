using System.Diagnostics;
using HotUpdate.ECS.FixedPoint;
using UnityEngine;

namespace HotUpdate.ECS.Lockstep
{
    /// <summary>
    /// 对应笔记第 6 篇的“单机回环帧同步演示驱动器（MockRelay）”：
    /// - 支持在 20Hz 固定逻辑帧（FixedDeltaTime = 0.05s）与 60fps 渲染插值（含 1.35 帧短暂外推）之间实时切换；
    /// - 支持一键“无头高速追帧（Headless Catch-Up）”：跳过全部表现层，瞬间在几十毫秒内推演完 200 逻辑帧（10 秒战局），随后对齐画面并输出 64 位 StateHash！
    /// </summary>
    public sealed class LockstepRunner
    {
        public static readonly FP FixedDeltaTimeFP = FP.FromMilli(50); // 20Hz
        public const float FixedDeltaTimeFloat = 0.05f;
        public const float MaxExtrapolationAlpha = 1.35f;

        public bool UseLockstep20Hz { get; set; } = true;
        public bool EnableInterpolation { get; set; } = true;

        public int LogicFrameId { get; private set; }
        public double LastCatchupMs { get; private set; }
        public int LastCatchupFrames { get; private set; }
        public ulong LastStateHash { get; private set; }

        private float _accumulator;
        private readonly Stopwatch _catchupWatch = new();

        public void Reset()
        {
            LogicFrameId = 0;
            _accumulator = 0f;
            LastCatchupMs = 0;
            LastCatchupFrames = 0;
            LastStateHash = 0UL;
        }

        /// <summary>
        /// 每 Unity 渲染帧调用：根据模式驱动逻辑帧、计算插值系数并推进表现层。
        /// </summary>
        public void TickRenderFrame(BattleWorld world, float deltaTime)
        {
            if (world == null) return;

            float clampedDt = Mathf.Clamp(deltaTime, 0f, 0.1f);

            if (!UseLockstep20Hz)
            {
                // 常规单机变长帧模式
                world.SnapshotPreviousPositions();
                world.UpdateLogicTick(FP.FromFloat(Mathf.Clamp(clampedDt, 0f, 0.05f)));
                LogicFrameId++;
                world.RenderInterpolationAlpha = 1f;
                world.UpdatePresentation();
                if ((LogicFrameId & 15) == 0)
                {
                    LastStateHash = world.ComputeStateHash();
                }
                return;
            }

            // 20Hz 帧同步回环模式（MockRelay）
            _accumulator += clampedDt;
            int safetySteps = 0;
            while (_accumulator >= FixedDeltaTimeFloat && safetySteps < 4)
            {
                _accumulator -= FixedDeltaTimeFloat;
                world.SnapshotPreviousPositions();
                world.UpdateLogicTick(FixedDeltaTimeFP);
                LogicFrameId++;
                safetySteps++;

                if ((LogicFrameId % 20) == 0)
                {
                    LastStateHash = world.ComputeStateHash();
                }
            }

            // 60fps 渲染插值开关对比（含最高 1.35 帧短暂外推）
            float alpha = EnableInterpolation
                ? Mathf.Clamp(_accumulator / FixedDeltaTimeFloat, 0f, MaxExtrapolationAlpha)
                : 1f;

            world.RenderInterpolationAlpha = alpha;
            world.UpdatePresentation();
        }

        /// <summary>
        /// 模拟断线重连后的“无头高速追帧”：关闭表现层与视觉特效生成，纯逻辑一口气跑完 N 帧后瞬间同步画面。
        /// </summary>
        public double RunHeadlessCatchup(BattleWorld world, int frames = 200)
        {
            if (world == null || frames <= 0) return 0;

            world.IsCatchingUp = true;
            _catchupWatch.Restart();

            for (int i = 0; i < frames; i++)
            {
                world.UpdateLogicTick(FixedDeltaTimeFP);
                LogicFrameId++;
            }

            _catchupWatch.Stop();
            world.IsCatchingUp = false;

            // 追帧收尾吸附：立刻对齐所有插值历史坐标到最新逻辑状态
            world.SnapshotPreviousPositions();
            _accumulator = 0f;
            world.RenderInterpolationAlpha = 1f;

            LastCatchupFrames = frames;
            LastCatchupMs = _catchupWatch.Elapsed.TotalMilliseconds;
            LastStateHash = world.ComputeStateHash();
            return LastCatchupMs;
        }
    }
}
