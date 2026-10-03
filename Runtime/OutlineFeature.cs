using System;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_2023_3_OR_NEWER
using UnityEngine.Rendering.RenderGraphModule;
#endif
using UnityEngine.Rendering.RendererUtils;
using UnityEngine.Rendering.Universal;
using UnityEngine.Serialization;

namespace BoysGameStudio.ShaderLab.Renderer
{
    // Concise, standalone URP renderer feature for silhouette-only outlines.
    public class OutlineFeature : ScriptableRendererFeature
    {
        [Serializable]
        public sealed class Settings
        {
            public enum ZOverride
            {
                Auto = 0,
                ForceReversed = 1,
                ForceForward = 2
            }

            [Tooltip("Shader used for the outline extrusion pass")]
            [FormerlySerializedAs("outlineShader")] public Shader shader;

            [Tooltip("Outline thickness (world or pixels depending on space)")]
            [FormerlySerializedAs("outlineWidth")] public float width = 0.0125f;

            [Tooltip("Layer mask for renderers to outline")]
            [FormerlySerializedAs("layerMask")] public LayerMask layers = -1;

            [Tooltip("Enable for SceneView cameras")] 
            [FormerlySerializedAs("runInSceneView")] public bool sceneView = true;

            [Tooltip("Enable for Game cameras")] 
            [FormerlySerializedAs("runInGameCamera")] public bool gameView = false;

            [Tooltip("Render pass event")] 
            [FormerlySerializedAs("renderPassEvent")] public RenderPassEvent passEvent = RenderPassEvent.AfterRenderingOpaques;

            [Tooltip("How to set _UseReversedZ")] 
            [FormerlySerializedAs("reversedZ")] public ZOverride z = ZOverride.Auto;

            [Tooltip("Thickness space: World (0) or Screen (1)")]
            public float thicknessSpace = 1f;

            [Tooltip("Use palette alpha instead of forcing 1.0")]
            [FormerlySerializedAs("usePaletteAlpha")] public bool useAlpha = false;

            [Tooltip("Min thickness in pixels when Screen space is used")]
            [FormerlySerializedAs("minScreenThickness")] public float minScreenPx = 3f;

            [Header("Silhouette")]
            [Tooltip("Clip to silhouette only (higher = tighter to edge)")] 
            [FormerlySerializedAs("silhouetteThreshold")] public float silhouette = 0.2f;
            [Tooltip("Edge power weighting")] 
            [FormerlySerializedAs("edgePower")] public float edge = 1.0f;
            [Tooltip("Feather width for AA silhouette gating")] 
            [FormerlySerializedAs("silhouetteFeather")] public float feather = 0.1f;

            [Header("Debug")] 
            [FormerlySerializedAs("debugOverlay")] public bool debug = false;
        }

        private sealed class OutlinePass : ScriptableRenderPass
        {
            private static readonly int OutlineWidthId = Shader.PropertyToID("_OutlineWidth");
            private static readonly int UseReversedZId = Shader.PropertyToID("_UseReversedZ");
            private static readonly int ThicknessSpaceId = Shader.PropertyToID("_ThicknessSpace");
            private static readonly int UsePaletteAlphaId = Shader.PropertyToID("_UsePaletteAlpha");
            private static readonly int SilhouetteThresholdId = Shader.PropertyToID("_SilhouetteThreshold");
            private static readonly int SilhouetteFeatherId = Shader.PropertyToID("_SilhouetteFeather");
            private static readonly int EdgePowerId = Shader.PropertyToID("_EdgePower");
            private static readonly ProfilingSampler Sampler = new ProfilingSampler("OutlinePass");

            private readonly Settings _s;
            private readonly ShaderTagId[] _tags =
            {
                new ShaderTagId("UniversalForward"),
                new ShaderTagId("UniversalForwardOnly"),
                new ShaderTagId("SRPDefaultUnlit"),
                new ShaderTagId("UniversalGBuffer")
            };

            private FilteringSettings _filter;
            private Material _outlineMat;
            private Material _depthOnlyMat;

            public OutlinePass(Settings s)
            {
                _s = s;
                _filter = new FilteringSettings(RenderQueueRange.all, s.layers);
            }

            public void ConfigureMaterial()
            {
                if (_s.shader == null)
                {
                    _outlineMat = null;
                }
                else if (_outlineMat == null || _outlineMat.shader != _s.shader)
                {
                    CoreUtils.Destroy(_outlineMat);
                    _outlineMat = CoreUtils.CreateEngineMaterial(_s.shader);
                }

                if (_outlineMat != null)
                {
                    float w = Mathf.Max(0f, _s.width);
                    if (_s.thicknessSpace >= 0.5f)
                    {
                        w = Mathf.Max(Mathf.Max(0f, _s.minScreenPx), w);
                    }
                    _outlineMat.SetFloat(OutlineWidthId, w);
                }

                _filter.layerMask = _s.layers;

                if (_depthOnlyMat == null)
                {
                    var depthShader = Shader.Find("Hidden/Universal Render Pipeline/DepthOnly");
                    if (depthShader != null)
                    {
                        _depthOnlyMat = CoreUtils.CreateEngineMaterial(depthShader);
                    }
                }
            }

            public override void OnCameraCleanup(CommandBuffer cmd) { }

#if UNITY_2023_3_OR_NEWER
            private sealed class PassData { public RendererListHandle List; }

            public override void RecordRenderGraph(RenderGraph rg, ContextContainer frame)
            {
                ConfigureMaterial();
                if (_outlineMat == null) return;

                var urp = frame.Get<UniversalRenderingData>();
                var cam = frame.Get<UniversalCameraData>().camera;
                if (!ShouldRun(cam) || cam == null) return;

                var res = frame.Get<UniversalResourceData>();
                if (!res.activeColorTexture.IsValid()) return;

                if (_depthOnlyMat != null && res.activeDepthTexture.IsValid())
                {
                    var d = Desc(urp.cullResults, cam, _depthOnlyMat);
                    var dl = rg.CreateRendererList(d);
                    using var db = rg.AddRasterRenderPass<PassData>("OutlineDepth", out var pd, Sampler);
                    db.AllowPassCulling(false);
                    db.SetRenderAttachmentDepth(res.activeDepthTexture, AccessFlags.Write);
                    pd.List = dl;
                    db.UseRendererList(pd.List);
                    db.SetRenderFunc((PassData x, RasterGraphContext c) => c.cmd.DrawRendererList(x.List));
                }

                var m = MaskDesc(urp.cullResults, cam);
                var mh = rg.CreateRendererList(m);
                using (var mb = rg.AddRasterRenderPass<PassData>("OutlineMask", out var mpd, Sampler))
                {
                    mb.AllowPassCulling(false);
                    mb.SetRenderAttachment(res.activeColorTexture, 0, AccessFlags.Read);
                    if (res.activeDepthTexture.IsValid()) mb.SetRenderAttachmentDepth(res.activeDepthTexture, AccessFlags.Write);
                    mpd.List = mh;
                    mb.UseRendererList(mpd.List);
                    mb.SetRenderFunc((PassData x, RasterGraphContext c) => c.cmd.DrawRendererList(x.List));
                }

                var rd = Desc(urp.cullResults, cam, _outlineMat); rd.overrideMaterialPassIndex = _s.debug ? 3 : 1;
                var fd = Desc(urp.cullResults, cam, _outlineMat); fd.overrideMaterialPassIndex = _s.debug ? 4 : 2;
                var rh = rg.CreateRendererList(rd);
                var fh = rg.CreateRendererList(fd);
                using var b = rg.AddRasterRenderPass<PassData>("Outline", out var pd2, Sampler);
                b.AllowPassCulling(false);
                b.AllowGlobalStateModification(true);
                b.SetRenderAttachment(res.activeColorTexture, 0, AccessFlags.Write);
                if (res.activeDepthTexture.IsValid()) b.SetRenderAttachmentDepth(res.activeDepthTexture, AccessFlags.Read);
                pd2.List = rh;
                b.UseRendererList(pd2.List);
                b.UseRendererList(fh);
                b.SetRenderFunc((PassData x, RasterGraphContext c) =>
                {
                    c.cmd.SetGlobalFloat(UseReversedZId, UseReversedZ());
                    c.cmd.SetGlobalFloat(ThicknessSpaceId, Mathf.Clamp01(_s.thicknessSpace));
                    c.cmd.SetGlobalFloat(UsePaletteAlphaId, _s.useAlpha ? 1f : 0f);
                    c.cmd.SetGlobalFloat(SilhouetteThresholdId, Mathf.Clamp01(_s.silhouette));
                    c.cmd.SetGlobalFloat(SilhouetteFeatherId, Mathf.Clamp01(_s.feather));
                    c.cmd.SetGlobalFloat(EdgePowerId, Mathf.Max(0f, _s.edge));
                    c.cmd.DrawRendererList(x.List);
                    c.cmd.DrawRendererList(fh);
                });
            }
#endif

            public void Dispose()
            {
                if (_outlineMat != null) { CoreUtils.Destroy(_outlineMat); _outlineMat = null; }
            }

            private bool ShouldRun(Camera c)
            {
                if (c == null) return false;
                if (c.cameraType == CameraType.SceneView) return _s.sceneView;
                return _s.gameView;
            }

            private RendererListDesc Desc(CullingResults c, Camera cam, Material mat)
            {
                var d = new RendererListDesc(_tags, c, cam)
                {
                    sortingCriteria = SortingCriteria.None,
                    rendererConfiguration = PerObjectData.None,
                    renderQueueRange = _filter.renderQueueRange,
                    excludeObjectMotionVectors = true,
                    overrideMaterial = mat,
                    overrideMaterialPassIndex = (mat == _outlineMat) ? SelectPass() : 0
                };
                d.layerMask = _filter.layerMask;
                return d;
            }

            private RendererListDesc MaskDesc(CullingResults c, Camera cam)
            {
                var d = new RendererListDesc(_tags, c, cam)
                {
                    sortingCriteria = SortingCriteria.None,
                    rendererConfiguration = PerObjectData.None,
                    renderQueueRange = _filter.renderQueueRange,
                    excludeObjectMotionVectors = true,
                    overrideMaterial = _outlineMat,
                    overrideMaterialPassIndex = 0
                };
                d.layerMask = _filter.layerMask;
                return d;
            }

            private int SelectPass()
            {
                if (_s.debug) return 3; // overlay (we also draw fwd explicitly)
                return UseReversedZ() >= 0.5f ? 1 : 2;
            }

            private float UseReversedZ()
            {
                switch (_s.z)
                {
                    case Settings.ZOverride.ForceReversed: return 1f;
                    case Settings.ZOverride.ForceForward: return 0f;
                    default: return SystemInfo.usesReversedZBuffer ? 1f : 0f;
                }
            }
        }

        [SerializeField]
        private Settings _settings = new Settings();

        private OutlinePass _pass;

        public Settings FeatureSettings => _settings;

        public override void Create()
        {
            if (_settings.shader == null)
            {
                _settings.shader = Shader.Find("Hidden/Quantum/SceneTimelineOutline");
            }
            _pass = new OutlinePass(_settings);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (_pass == null) return;
            var cam = renderingData.cameraData.camera; if (cam == null) return;
            _pass.ConfigureMaterial();
            _pass.renderPassEvent = _settings.passEvent;
            if (_settings.shader != null)
            {
                renderer.EnqueuePass(_pass);
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (_pass != null) { _pass.Dispose(); _pass = null; }
        }
    }
}
