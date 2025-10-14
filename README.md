# Shader Lab  Scene Timeline Outline Feature

This package provides a standalone URP ScriptableRendererFeature for silhouette-only outlines.

- Feature script: `Runtime/SceneTimelineOutlineFeature.cs` (namespace `BoysGameStudio.ShaderLab.Renderer`)
- Shader: `Hidden/Quantum/SceneTimelineOutline` (included under this package)

## Enable in URP

1. Open your URP Renderer asset (Forward Renderer or your custom one).
2. Add Renderer Feature: `BoysGameStudio.ShaderLab.Renderer.SceneTimelineOutlineFeature`.
3. Configure:
   - Layer Mask: set to the layer containing objects to outline
   - Run in Scene View: ON (for editor preview)
   - Run in Game Camera: as needed
   - Render Event: AfterRenderingOpaques recommended
   - Thickness Space: `Screen (1)` for stable pixel width; set Min Screen Thickness if needed

No dependencies on project-specific code. The feature relies on global shader parameters for palette/colors if used; otherwise, the shader can be configured per-material.

## Notes
- If you previously used a project-specific feature, remove it to avoid duplicates.
- If a URP Renderer asset referenced the old feature type, re-add this new one after removing the old script.
