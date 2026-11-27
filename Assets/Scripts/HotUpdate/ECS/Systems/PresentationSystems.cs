using System;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using HotUpdate.ECS.Components;
using HotUpdate.ECS.State;
using UnityEngine;

namespace HotUpdate.ECS.Systems
{
    /// <summary>
    /// 视锥裁切层（独立运行在 CullingRoot 中）：
    /// 使用零 GC 的 GeometryUtility.CalculateFrustumPlanes(camera, planes) + 6 平面球体早退测试，
    /// 视野外实体直接标记 CubeVisual.IsVisible = false，跳过后续 GPU Instancing 矩阵打包或关闭 GameObject MeshRenderer。
    /// </summary>
    public sealed class FrustumCullingSystem : QuerySystem<UnitTransform, CubeVisual>
    {
        private readonly BattleWorld _world;
        private readonly Plane[] _frustumPlanes = new Plane[6];
        private readonly float _cullPadding;
        private Camera _cachedCamera;

        /// <summary>可选覆盖相机（用于多相机或单元测试注入）；为空时自动使用 Camera.main。</summary>
        public Camera OverrideCamera { get; set; }

        public int VisibleCount { get; private set; }
        public int CulledCount { get; private set; }

        public FrustumCullingSystem(BattleWorld world, float cullPadding = 1.5f)
        {
            _world = world;
            _cullPadding = cullPadding;
            Filter.WithoutAllTags(Tags.Get<DeadTag>());
        }

        /// <summary>
        /// 纯静态球体-视锥 6 平面早退检测（零 GC，同时便于 EditMode 单元测试直接验证）。
        /// </summary>
        public static bool IsSphereInsideFrustum(Plane[] planes, in Vector3 center, float radius)
        {
            for (int i = 0; i < 6; i++)
            {
                if (planes[i].GetDistanceToPoint(center) < -radius)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>供单元测试或外部指定相机直接更新视锥裁切状态。</summary>
        public void CullWithCamera(Camera cam)
        {
            VisibleCount = 0;
            CulledCount = 0;

            var activeQuery = Query ?? _world.Store.Query<UnitTransform, CubeVisual>().WithoutAllTags(Tags.Get<DeadTag>());

            if (cam == null)
            {
                activeQuery.ForEachEntity((ref UnitTransform _, ref CubeVisual vis, Entity _) =>
                {
                    vis.IsVisible = true;
                    VisibleCount++;
                });
                return;
            }

            GeometryUtility.CalculateFrustumPlanes(cam, _frustumPlanes);
            float pad = _cullPadding;

            activeQuery.ForEachEntity((ref UnitTransform tr, ref CubeVisual vis, Entity _) =>
            {
                Vector3 pos = tr.Position.ToVector3();
                float halfExtent = Mathf.Max(tr.Scale.X.ToFloat(), tr.Scale.Z.ToFloat()) * 0.65f + pad;
                bool visible = IsSphereInsideFrustum(_frustumPlanes, pos, halfExtent);
                vis.IsVisible = visible;
                if (visible) VisibleCount++;
                else CulledCount++;
            });
        }

        protected override void OnUpdate()
        {
            Camera cam = OverrideCamera;
            if (cam == null)
            {
                if (_cachedCamera == null) _cachedCamera = Camera.main;
                cam = _cachedCamera;
            }
            CullWithCamera(cam);
        }
    }

    /// <summary>
    /// 表现层 1：基于 Graphics.DrawMeshInstanced 的零 GameObject 批量 Cube 渲染系统。
    /// 仅在渲染边界将 Q48.16 定点数转为 float，并根据 UnitState 位掩码可视化受击闪白 / 眩晕 / 减速 / 霸体。
    /// </summary>
    public sealed class CubeInstancedRenderSystem : QuerySystem<UnitTransform, CubeVisual>, IDisposable
    {
        private const int MaxBatchSize = 1023;
        private static readonly int ColorPropId = Shader.PropertyToID("_Color");
        private static readonly Color StunTint = new(0.98f, 0.88f, 0.20f);
        private static readonly Color SlowTint = new(0.35f, 0.85f, 1.0f);
        private static readonly Color ArmorTint = new(1.0f, 0.93f, 0.45f);

        private readonly BattleWorld _world;
        private readonly Matrix4x4[] _matrices = new Matrix4x4[MaxBatchSize];
        private readonly Vector4[] _colors = new Vector4[MaxBatchSize];
        private readonly MaterialPropertyBlock _mpb = new();

        private Mesh _cubeMesh;
        private Material _instancedMat;
        private bool _disposed;

        public int SubmittedInstanceCount { get; private set; }

        public CubeInstancedRenderSystem(BattleWorld world)
        {
            _world = world;
            Filter.WithoutAllTags(Tags.Get<DeadTag>());
            InitializeRenderResources();
        }

        private void InitializeRenderResources()
        {
            GameObject temp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var filter = temp.GetComponent<MeshFilter>();
            _cubeMesh = filter != null ? filter.sharedMesh : null;
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(temp);
            else
                UnityEngine.Object.DestroyImmediate(temp);

            Shader shader = Shader.Find("Demo/InstancedCubeLit");
            if (shader == null)
            {
                shader = Shader.Find("Standard");
            }
            _instancedMat = new Material(shader)
            {
                name = "Runtime_InstancedCubeLit",
                enableInstancing = true,
            };
        }

        protected override void OnUpdate()
        {
            if (_disposed || _cubeMesh == null || _instancedMat == null) return;

            int batchCount = 0;
            int totalSubmitted = 0;
            bool cullingEnabled = _world.Config.UseFrustumCulling;

            Query.ForEachEntity((ref UnitTransform tr, ref CubeVisual vis, Entity entity) =>
            {
                if (cullingEnabled && !vis.IsVisible)
                {
                    return;
                }

                float yaw = tr.Yaw.ToFloat();
                Quaternion rot = yaw != 0f ? Quaternion.Euler(0f, yaw, 0f) : Quaternion.identity;
                _matrices[batchCount] = Matrix4x4.TRS(tr.Position.ToVector3(), rot, tr.Scale.ToVector3());

                Color c = vis.BaseColor;
                if (entity.TryGetComponent<UnitState>(out var st))
                {
                    if (st.Has(UnitStateType.HitFlash))
                    {
                        c = Color.white;
                    }
                    else if (st.Has(UnitStateType.Stunned))
                    {
                        c = StunTint;
                    }
                    else if (st.Has(UnitStateType.Slowed))
                    {
                        c = Color.Lerp(c, SlowTint, 0.55f);
                    }
                    else if (st.Has(UnitStateType.SuperArmor))
                    {
                        c = Color.Lerp(c, ArmorTint, 0.35f);
                    }
                }

                _colors[batchCount] = new Vector4(c.r, c.g, c.b, c.a);
                totalSubmitted++;

                if (++batchCount == MaxBatchSize)
                {
                    FlushBatch(batchCount);
                    batchCount = 0;
                }
            });

            if (batchCount > 0)
            {
                FlushBatch(batchCount);
            }

            SubmittedInstanceCount = totalSubmitted;
        }

        private void FlushBatch(int count)
        {
            _mpb.Clear();
            _mpb.SetVectorArray(ColorPropId, _colors);
            Graphics.DrawMeshInstanced(_cubeMesh, 0, _instancedMat, _matrices, count, _mpb);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_instancedMat != null)
            {
                if (Application.isPlaying)
                    UnityEngine.Object.Destroy(_instancedMat);
                else
                    UnityEngine.Object.DestroyImmediate(_instancedMat);
                _instancedMat = null;
            }
        }
    }

    /// <summary>
    /// 表现层 2：俯视角主相机平滑跟随主控定点坐标。
    /// </summary>
    public sealed class CameraFollowSystem : BaseSystem
    {
        private readonly BattleWorld _world;
        private readonly Vector3 _offset = new(0f, 22f, -18f);
        private Camera _cachedCamera;

        public CameraFollowSystem(BattleWorld world)
        {
            _world = world;
        }

        protected override void OnUpdateGroup()
        {
            if (_cachedCamera == null)
            {
                _cachedCamera = Camera.main;
                if (_cachedCamera == null) return;
            }

            Vector3 targetPos = _world.PlayerPosition.ToVector3();
            Vector3 desiredPos = targetPos + _offset;
            float t = 1f - Mathf.Exp(-8f * Time.deltaTime);
            _cachedCamera.transform.position = Vector3.Lerp(_cachedCamera.transform.position, desiredPos, t);
            _cachedCamera.transform.LookAt(targetPos);
        }
    }
}
