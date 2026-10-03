using System;

using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_2023_3_OR_NEWER
using UnityEngine.Rendering.RenderGraphModule;
#endif
using UnityEngine.Rendering.RendererUtils;
using UnityEngine.Rendering.Universal;

namespace BoysGameStudio.ShaderLab.Renderer
{
    /// <summary>
    /// Standalone URP ScriptableRendererFeature to render silhouette-only outlines using the shader
    /// "Hidden/Quantum/SceneTimelineOutline". This feature has no dependencies on Quantum or Odin.
    /// It relies on global shader parameters for palette colors and index selection.
    /// </summary>
    public sealed class SceneTimelineOutlineFeature : ScriptableRendererFeature
    {
        [Serializable]
        public sealed class Settings
        {
            public enum ReversedZOverride
            {
                Auto = 0,
                ForceReversed = 1,
                ForceForward = 2
            }

            [Tooltip("URP shader used for the outline extrusion pass.")]
            public Shader outlineShader;

            [Tooltip("Outline thickness in world units or pixels (depending on space).")]
            public float outlineWidth = 0.0125f;

            [Tooltip("Layer mask containing renderers that should be outlined.")]
            public LayerMask layerMask = -1;

            [Tooltip("Whether to run on the SceneView camera.")]
            public bool runInSceneView = true;

            [Tooltip("Whether to run on game cameras.")]
            public bool runInGameCamera = false;

            [Tooltip("Render event for the outline pass.")]
            public RenderPassEvent renderPassEvent = RenderPassEvent.AfterRenderingOpaques;

            [Tooltip("How to set _UseReversedZ: Auto (use SystemInfo.usesReversedZBuffer), ForceReversed (1), or ForceForward (0).")]
            public ReversedZOverride reversedZ = ReversedZOverride.Auto;

            [Tooltip("Thickness space: World (0) extrudes in meters; Screen (1) keeps constant pixel thickness.")]
            [Range(0, 1)] public float thicknessSpace = 1f; // default screen-space for stable look

            [Tooltip("Use palette alpha instead of forcing 1.0. Disable for guaranteed visible outline.")]
            public bool usePaletteAlpha = false;

            [Tooltip("Minimum thickness in pixels when Thickness Space is Screen. Ensures outlines remain visible.")]
            [Min(0f)] public float minScreenThickness = 3f;

            [Header("Silhouette Cleanup")]
            [Tooltip("Clip outlines to near-silhouette only. Higher values reduce noisy interior spikes.")]
            [Range(0f, 1f)] public float silhouetteThreshold = 0.2f;
            [Tooltip("Exponent for silhouette strength weighting in vertex offset. 1 keeps linear weighting; higher tightens to true edge.")]
            [Min(0f)] public float edgePower = 1.0f;
            [Tooltip("Feather width for anti-aliased silhouette gating (in N·V space, 0 = hard edge).")]
            [Range(0f, 1f)] public float silhouetteFeather = 0.1f;

            [Header("Debug")]
            [Tooltip("Bypass stencil/depth and draw overlay passes to validate visibility.")]
            public bool debugOverlay = false;
        }

        private sealed class SceneTimelineOutlinePass : ScriptableRenderPass
        {
            private static readonly int OutlineWidthId = Shader.PropertyToID("_OutlineWidth");
            private static readonly int UseReversedZId = Shader.PropertyToID("_UseReversedZ");
            private static readonly int ThicknessSpaceId = Shader.PropertyToID("_ThicknessSpace");
            private static readonly int UsePaletteAlphaId = Shader.PropertyToID("_UsePaletteAlpha");
            private static readonly int SilhouetteThresholdId = Shader.PropertyToID("_SilhouetteThreshold");
            private static readonly int SilhouetteFeatherId = Shader.PropertyToID("_SilhouetteFeather");
            private static readonly int EdgePowerId = Shader.PropertyToID("_EdgePower");
            private static readonly ProfilingSampler ProfilingSampler = new ProfilingSampler("SceneTimelineOutlinePass");

            private readonly Settings _settings;
            private readonly ShaderTagId[] _shaderTags =
            {
                new ShaderTagId("UniversalForward"),
                new ShaderTagId("UniversalForwardOnly"),
                new ShaderTagId("SRPDefaultUnlit"),
                new ShaderTagId("UniversalGBuffer")
            };

            private FilteringSettings _filteringSettings;
            private Material _outlineMaterial;
            private Material _depthOnlyMaterial;

            public SceneTimelineOutlinePass(Settings settings)
            {
                _settings = settings;
                _filteringSettings = new FilteringSettings(RenderQueueRange.all, settings.layerMask);
            }

            public void ConfigureMaterial()
            {
                if (_settings.outlineShader == null)
                {
                    _outlineMaterial = null;
                    return;
                }

                if (_outlineMaterial == null || _outlineMaterial.shader != _settings.outlineShader)
                {
                    CoreUtils.Destroy(_outlineMaterial);
                    _outlineMaterial = CoreUtils.CreateEngineMaterial(_settings.outlineShader);
                }

                if (_outlineMaterial != null)
                {
                    float w = Mathf.Max(0f, _settings.outlineWidth);
                    if (_settings.thicknessSpace >= 0.5f)
                    {
                        float minPx = Mathf.Max(0f, _settings.minScreenThickness);
                        w = Mathf.Max(minPx, w);
                    }
                    _outlineMaterial.SetFloat(OutlineWidthId, w);
                }

                _filteringSettings.layerMask = _settings.layerMask;

                if (_depthOnlyMaterial == null)
                {
                    var depthShader = Shader.Find("Hidden/Universal Render Pipeline/DepthOnly");
                    if (depthShader != null)
                    {
                        _depthOnlyMaterial = CoreUtils.CreateEngineMaterial(depthShader);
                    }
                }
            }

            public override void OnCameraCleanup(CommandBuffer cmd) { }

#if UNITY_2023_3_OR_NEWER
            private sealed class PassData
            {
                public RendererListHandle RendererList;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                ConfigureMaterial();
                if (_outlineMaterial == null)
                {
                    return;
                }

                var renderingData = frameData.Get<UniversalRenderingData>();
                var universalCameraData = frameData.Get<UniversalCameraData>();
                var camera = universalCameraData.camera;
                if (!ShouldRenderForCamera(camera) || camera == null)
                {
                    return;
                }

                var resourceData = frameData.Get<UniversalResourceData>();
                if (!resourceData.activeColorTexture.IsValid())
                {
                    return;
                }

                if (_depthOnlyMaterial != null && resourceData.activeDepthTexture.IsValid())
                {
                    var depthDesc = CreateRendererListDesc(renderingData.cullResults, camera, _depthOnlyMaterial);
                    var depthList = renderGraph.CreateRendererList(depthDesc);
                    using var depthBuilder = renderGraph.AddRasterRenderPass<PassData>("SceneTimelineOutlineDepthPrepass", out var depthPass, ProfilingSampler);
                    depthBuilder.AllowPassCulling(false);
                    depthBuilder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.Write);
                    depthPass.RendererList = depthList;
                    depthBuilder.UseRendererList(depthPass.RendererList);
                    depthBuilder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                    {
                        context.cmd.DrawRendererList(data.RendererList);
                    });
                }

                var maskDesc = CreateMaskRendererListDesc(renderingData.cullResults, camera);
                var maskHandle = renderGraph.CreateRendererList(maskDesc);
                using (var maskBuilder = renderGraph.AddRasterRenderPass<PassData>("SceneTimelineOutlineMask", out var maskData, ProfilingSampler))
                {
                    maskBuilder.AllowPassCulling(false);
                    maskBuilder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.Read);
                    if (resourceData.activeDepthTexture.IsValid())
                    {
                        maskBuilder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.Write);
                    }
                    maskData.RendererList = maskHandle;
                    maskBuilder.UseRendererList(maskData.RendererList);
                    maskBuilder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                    {
                        context.cmd.DrawRendererList(data.RendererList);
                    });
                }

                var outlineDescRev = CreateRendererListDesc(renderingData.cullResults, camera, _outlineMaterial);
                outlineDescRev.overrideMaterialPassIndex = _settings.debugOverlay ? 3 : 1;
                var outlineDescFwd = CreateRendererListDesc(renderingData.cullResults, camera, _outlineMaterial);
                outlineDescFwd.overrideMaterialPassIndex = _settings.debugOverlay ? 4 : 2;
                var outlineHandleRev = renderGraph.CreateRendererList(outlineDescRev);
                var outlineHandleFwd = renderGraph.CreateRendererList(outlineDescFwd);
                using var builder = renderGraph.AddRasterRenderPass<PassData>("SceneTimelineOutline", out var passData, ProfilingSampler);
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.Write);
                if (resourceData.activeDepthTexture.IsValid())
                {
                    builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.Read);
                }

                passData.RendererList = outlineHandleRev;
                builder.UseRendererList(passData.RendererList);
                builder.UseRendererList(outlineHandleFwd);
                builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    context.cmd.SetGlobalFloat(UseReversedZId, GetUseReversedZFlag());
                    context.cmd.SetGlobalFloat(ThicknessSpaceId, Mathf.Clamp01(_settings.thicknessSpace));
                    context.cmd.SetGlobalFloat(UsePaletteAlphaId, _settings.usePaletteAlpha ? 1f : 0f);
                    context.cmd.SetGlobalFloat(SilhouetteThresholdId, Mathf.Clamp01(_settings.silhouetteThreshold));
                    context.cmd.SetGlobalFloat(SilhouetteFeatherId, Mathf.Clamp01(_settings.silhouetteFeather));
                    context.cmd.SetGlobalFloat(EdgePowerId, Mathf.Max(0f, _settings.edgePower));
                    context.cmd.DrawRendererList(data.RendererList);
                    context.cmd.DrawRendererList(outlineHandleFwd);
                });
            }
#endif

            public void Dispose()
            {
                if (_outlineMaterial != null)
                {
                    CoreUtils.Destroy(_outlineMaterial);
                    _outlineMaterial = null;
                }
            }

            public bool ShouldRenderForCamera(Camera camera)
            {
                if (camera == null)
                {
                    return false;
                }

                if (camera.cameraType == CameraType.SceneView)
                {
                    return _settings.runInSceneView;
                }

                return _settings.runInGameCamera;
            }

            private RendererListDesc CreateRendererListDesc(CullingResults cullResults, Camera camera)
            {
                return CreateRendererListDesc(cullResults, camera, _outlineMaterial);
            }

            private RendererListDesc CreateRendererListDesc(CullingResults cullResults, Camera camera, Material overrideMat)
            {
                var desc = new RendererListDesc(_shaderTags, cullResults, camera)
                {
                    sortingCriteria = SortingCriteria.None,
                    rendererConfiguration = PerObjectData.None,
                    renderQueueRange = _filteringSettings.renderQueueRange,
                    excludeObjectMotionVectors = true,
                    overrideMaterial = overrideMat,
                    overrideMaterialPassIndex = (overrideMat == _outlineMaterial) ? GetSelectedPassIndex() : 0
                };

                desc.layerMask = _filteringSettings.layerMask;
                return desc;
            }

            private int GetSelectedPassIndex()
            {
                // Shader layout: 0 = Mask, 1 = Reversed-Z, 2 = Forward-Z, 3/4 = Overlay debug
                if (_settings.debugOverlay)
                {
                    return 3; // overlay reversed by default; also draw fwd explicitly
                }
                return GetUseReversedZFlag() >= 0.5f ? 1 : 2;
            }

            private RendererListDesc CreateMaskRendererListDesc(CullingResults cullResults, Camera camera)
            {
                var desc = new RendererListDesc(_shaderTags, cullResults, camera)
                {
                    sortingCriteria = SortingCriteria.None,
                    rendererConfiguration = PerObjectData.None,
                    renderQueueRange = _filteringSettings.renderQueueRange,
                    excludeObjectMotionVectors = true,
                    overrideMaterial = _outlineMaterial,
                    overrideMaterialPassIndex = 0 // mask pass
                };
                desc.layerMask = _filteringSettings.layerMask;
                return desc;
            }

            private float GetUseReversedZFlag()
            {
                switch (_settings.reversedZ)
                {
                    case Settings.ReversedZOverride.ForceReversed:
                        return 1f;
                    case Settings.ReversedZOverride.ForceForward:
                        return 0f;
                    default:
                        return SystemInfo.usesReversedZBuffer ? 1f : 0f;
                }
            }
        }

        [SerializeField]
        private Settings _settings = new Settings();

        private SceneTimelineOutlinePass _pass;

        public Settings FeatureSettings => _settings;

        public override void Create()
        {
            if (_settings.outlineShader == null)
            {
                _settings.outlineShader = Shader.Find("Hidden/Quantum/SceneTimelineOutline");
            }

            _pass = new SceneTimelineOutlinePass(_settings);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (_pass == null)
            {
                return;
            }

            var camera = renderingData.cameraData.camera;
            if (camera == null)
            {
                return;
            }

            if (!_pass.ShouldRenderForCamera(camera))
            {
                return;
            }

            _pass.ConfigureMaterial();
            _pass.renderPassEvent = _settings.renderPassEvent;
            if (_settings.outlineShader != null)
            {
                renderer.EnqueuePass(_pass);
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (_pass != null)
            {
                _pass.Dispose();
                _pass = null;
            }
        }
    }
}
