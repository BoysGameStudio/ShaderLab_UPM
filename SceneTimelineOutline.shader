Shader "Hidden/Quantum/SceneTimelineOutline"
{
    Properties
    {
        _OutlineWidth ("Outline Width", Float) = 0.0125
        _OutlineIndex ("Outline Index", Float) = 0
        _SilhouetteThreshold ("Silhouette Threshold", Float) = 0.2
        _SilhouetteFeather ("Silhouette Feather", Float) = 0.1
        _EdgePower ("Silhouette Edge Power", Float) = 1.0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry+1" }
        LOD 100
        Cull Front
        ZWrite Off

        // Pass 0: Front-face stencil mask to restrict outlines to outer silhouette only
        Pass
        {
            Name "SceneTimelineOutline_Mask"
            Cull Back
            ColorMask 0
            ZWrite Off
            ZTest Always
            Stencil
            {
                Ref 1
                Comp Always
                Pass Replace
            }
            HLSLPROGRAM
            #pragma vertex MaskVertex
            #pragma fragment MaskFragment
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            Varyings MaskVertex(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                Varyings o; o.positionCS = TransformObjectToHClip(input.positionOS.xyz); return o;
            }
            void MaskFragment()
            {

            }
            ENDHLSL
        }

        // Pass 1: Reversed-Z platforms (URP default on most targets). Use stencil to limit to outer silhouette and ZTest Always for visibility.
        Pass
        {
            Name "SceneTimelineOutline_ReversedZ"
            ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha
            Stencil
            {
                Ref 1
                Comp NotEqual
            }
            HLSLPROGRAM
            #pragma vertex OutlineVertex
            #pragma fragment OutlineFragment
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _OutlineWidth;
                float _SilhouetteThreshold;
                float _SilhouetteFeather;
                float _EdgePower;
            CBUFFER_END

            // Read global single outline index used for sampling the palette color
            float _OutlineIndex;
            float _UseReversedZ; // 1 = reversed Z, 0 = forward Z
            float _ThicknessSpace; // 0 = world units, 1 = screen pixels
            float _UsePaletteAlpha; // 0 = force alpha 1, 1 = use palette alpha

            #define QB_OUTLINE_MAX 16
            float4 _OutlineColors[QB_OUTLINE_MAX];
            float _OutlineColorCount;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float silhouette : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            // Safe normalize to stabilize near-zero projected normals
            float2 SafeNormalize2(float2 v)
            {
                float l = max(length(v), 1e-6);
                return v / l;
            }

            Varyings OutlineVertex(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                Varyings output;
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                // Silhouette factor from N·V
                float3 viewWS = normalize(_WorldSpaceCameraPos - positionWS);
                float ndotv = dot(normalize(normalWS), viewWS);
                float silhouette = saturate(1.0 - abs(ndotv));
                output.silhouette = silhouette;
                if (_ThicknessSpace >= 0.5)
                {
                    // Screen-space thickness: offset in clip space by pixels; do not change depth
                    float4 positionVS = mul(UNITY_MATRIX_V, float4(positionWS, 1.0));
                    float4 positionCS = mul(UNITY_MATRIX_P, positionVS);

                    float3 nVS3 = mul((float3x3)UNITY_MATRIX_V, normalize(normalWS));
                    // Backface rendering with Cull Front: expand outward opposite to projected normal
                    float2 dir = -SafeNormalize2(nVS3.xy);
                    float2 ndcPerPixel = 2.0 / _ScreenParams.xy;
                    float2 clipOffset = dir * (_OutlineWidth) * ndcPerPixel * positionCS.w;
                    positionCS.xy += clipOffset;
                    output.positionCS = positionCS;
                }
                else
                {
                    // World-space thickness: extrude along normal in world units
                    positionWS += normalWS * (_OutlineWidth);
                    output.positionCS = TransformWorldToHClip(positionWS);
                }
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                return output;
            }

            float4 SamplePalette(float index)
            {
                if (_OutlineColorCount <= 0)
                {
                    return float4(0, 0, 0, 0);
                }

                int idx = (int)round(index);
                if (idx < 0 || idx >= (int)_OutlineColorCount)
                {
                    return float4(0, 0, 0, 0);
                }

                idx = clamp(idx, 0, QB_OUTLINE_MAX - 1);
                return _OutlineColors[idx];
            }

            float4 OutlineFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                float colorIndex = _OutlineIndex;
                // If index invalid or palette empty, do not draw outline
                if (colorIndex < 0.0f || _OutlineColorCount <= 0)
                {
                    clip(-1.0f);
                }
                float4 color = SamplePalette(colorIndex);
                // Anti-aliased silhouette gating with derivative-based feather
                float th = saturate(_SilhouetteThreshold);
                float s = saturate(input.silhouette);
                float g = fwidth(s);
                float feather = max(max(1e-5, _SilhouetteFeather), g);
                float a = smoothstep(th, th + feather, s);
                // Edge shaping
                a = saturate(pow(a, max(1e-3, _EdgePower)));
                color.rgb = saturate(color.rgb);
                float baseAlpha = (_UsePaletteAlpha > 0.5) ? color.a : 1.0;
                color.a = a * baseAlpha;
                // Clip only when fully transparent due to gating
                clip(a - 1e-5);
                return color;
            }
            ENDHLSL
        }

        // Pass 2: Forward-Z platforms. Use stencil to limit to outer silhouette and ZTest Always for visibility.
        Pass
        {
            Name "SceneTimelineOutline_ForwardZ"
            ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha
            Stencil
            {
                Ref 1
                Comp NotEqual
            }
            HLSLPROGRAM
            #pragma vertex OutlineVertex
            #pragma fragment OutlineFragmentFwd
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _OutlineWidth;
                float _SilhouetteThreshold;
                float _SilhouetteFeather;
                float _EdgePower;
            CBUFFER_END

            float _OutlineIndex;
            float _UseReversedZ; // 1 = reversed Z, 0 = forward Z
            float _ThicknessSpace; // 0 = world units, 1 = screen pixels
            float _UsePaletteAlpha; // 0 = force alpha 1, 1 = use palette alpha

            #define QB_OUTLINE_MAX 16
            float4 _OutlineColors[QB_OUTLINE_MAX];
            float _OutlineColorCount;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float silhouette : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            float2 SafeNormalize2(float2 v)
            {
                float l = max(length(v), 1e-6);
                return v / l;
            }

            Varyings OutlineVertex(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                Varyings output;
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 viewWS = normalize(_WorldSpaceCameraPos - positionWS);
                float ndotv = dot(normalize(normalWS), viewWS);
                float silhouette = saturate(1.0 - abs(ndotv));
                output.silhouette = silhouette;
                if (_ThicknessSpace >= 0.5)
                {
                    float4 positionVS = mul(UNITY_MATRIX_V, float4(positionWS, 1.0));
                    float4 positionCS = mul(UNITY_MATRIX_P, positionVS);

                    float3 nVS3 = mul((float3x3)UNITY_MATRIX_V, normalize(normalWS));
                    float2 dir = -SafeNormalize2(nVS3.xy);
                    float2 ndcPerPixel = 2.0 / _ScreenParams.xy;
                    float2 clipOffset = dir * (_OutlineWidth) * ndcPerPixel * positionCS.w;
                    positionCS.xy += clipOffset;
                    output.positionCS = positionCS;
                }
                else
                {
                    positionWS += normalWS * (_OutlineWidth);
                    output.positionCS = TransformWorldToHClip(positionWS);
                }
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                return output;
            }

            float4 SamplePalette(float index)
            {
                if (_OutlineColorCount <= 0)
                {
                    return float4(0, 0, 0, 0);
                }

                int idx = (int)round(index);
                if (idx < 0 || idx >= (int)_OutlineColorCount)
                {
                    return float4(0, 0, 0, 0);
                }

                idx = clamp(idx, 0, QB_OUTLINE_MAX - 1);
                return _OutlineColors[idx];
            }

            float4 OutlineFragmentFwd(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                float colorIndex = _OutlineIndex;
                if (colorIndex < 0.0f || _OutlineColorCount <= 0)
                {
                    clip(-1.0f);
                }
                float4 color = SamplePalette(colorIndex);
                float th = saturate(_SilhouetteThreshold);
                float s = saturate(input.silhouette);
                float g = fwidth(s);
                float feather = max(max(1e-5, _SilhouetteFeather), g);
                float a = smoothstep(th, th + feather, s);
                a = saturate(pow(a, max(1e-3, _EdgePower)));
                color.rgb = saturate(color.rgb);
                float baseAlpha = (_UsePaletteAlpha > 0.5) ? color.a : 1.0;
                color.a = a * baseAlpha;
                clip(a - 1e-5);
                return color;
            }
            ENDHLSL
        }

        // Pass 3: Overlay (debug) - bypass stencil, ZTest Always, reversed-Z vertex path
        Pass
        {
            Name "SceneTimelineOutline_OverlayReversedZ"
            ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma vertex OutlineVertex
            #pragma fragment OutlineFragment
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _OutlineWidth;
                float _SilhouetteThreshold;
                float _SilhouetteFeather;
                float _EdgePower;
            CBUFFER_END

            float _OutlineIndex;
            float _UseReversedZ;
            float _ThicknessSpace;
            float _UsePaletteAlpha;

            #define QB_OUTLINE_MAX 16
            float4 _OutlineColors[QB_OUTLINE_MAX];
            float _OutlineColorCount;

            struct Attributes
            {
                float4 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION; float silhouette : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            float2 SafeNormalize2(float2 v)
            {
                float l = max(length(v), 1e-6);
                return v / l;
            }

            Varyings OutlineVertex(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                Varyings output;
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 viewWS = normalize(_WorldSpaceCameraPos - positionWS);
                float ndotv = dot(normalize(normalWS), viewWS);
                float silhouette = saturate(1.0 - abs(ndotv));
                output.silhouette = silhouette;
                if (_ThicknessSpace >= 0.5)
                {
                    float4 positionVS = mul(UNITY_MATRIX_V, float4(positionWS, 1.0));
                    float4 positionCS = mul(UNITY_MATRIX_P, positionVS);

                    float3 nVS3 = mul((float3x3)UNITY_MATRIX_V, normalize(normalWS));
                    float2 dir = -SafeNormalize2(nVS3.xy);
                    float2 ndcPerPixel = 2.0 / _ScreenParams.xy;
                    float2 clipOffset = dir * (_OutlineWidth) * ndcPerPixel * positionCS.w;
                    positionCS.xy += clipOffset;
                    output.positionCS = positionCS;
                }
                else
                {
                    positionWS += normalWS * (_OutlineWidth);
                    output.positionCS = TransformWorldToHClip(positionWS);
                }
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                return output;
            }

            float4 SamplePalette(float index)
            {
                if (_OutlineColorCount <= 0)
                {
                    return float4(0, 0, 0, 0);
                }
                int idx = (int)round(index);
                if (idx < 0 || idx >= (int)_OutlineColorCount)
                {
                    return float4(0, 0, 0, 0);
                }
                idx = clamp(idx, 0, QB_OUTLINE_MAX - 1);
                return _OutlineColors[idx];
            }

            float4 OutlineFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                float colorIndex = _OutlineIndex;
                if (colorIndex < 0.0f || _OutlineColorCount <= 0)
                {
                    clip(-1.0f);
                }
                float4 color = SamplePalette(colorIndex);
                float th = saturate(_SilhouetteThreshold);
                float s = saturate(input.silhouette);
                float g = fwidth(s);
                float feather = max(max(1e-5, _SilhouetteFeather), g);
                float a = smoothstep(th, th + feather, s);
                a = saturate(pow(a, max(1e-3, _EdgePower)));
                color.rgb = saturate(color.rgb);
                float baseAlpha = (_UsePaletteAlpha > 0.5) ? color.a : 1.0;
                color.a = a * baseAlpha;
                clip(a - 1e-5);
                return color;
            }
            ENDHLSL
        }

        // Pass 4: Overlay (debug) - bypass stencil, ZTest Always, forward-Z vertex path
        Pass
        {
            Name "SceneTimelineOutline_OverlayForwardZ"
            ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma vertex OutlineVertex
            #pragma fragment OutlineFragmentFwd
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _OutlineWidth;
                float _SilhouetteThreshold;
                float _SilhouetteFeather;
                float _EdgePower;
            CBUFFER_END

            float _OutlineIndex;
            float _UseReversedZ;
            float _ThicknessSpace;
            float _UsePaletteAlpha;

            #define QB_OUTLINE_MAX 16
            float4 _OutlineColors[QB_OUTLINE_MAX];
            float _OutlineColorCount;

            struct Attributes
            {
                float4 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION; float silhouette : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            float2 SafeNormalize2(float2 v)
            {
                float l = max(length(v), 1e-6);
                return v / l;
            }

            Varyings OutlineVertex(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                Varyings output;
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 viewWS = normalize(_WorldSpaceCameraPos - positionWS);
                float ndotv = dot(normalize(normalWS), viewWS);
                float silhouette = saturate(1.0 - abs(ndotv));
                output.silhouette = silhouette;
                if (_ThicknessSpace >= 0.5)
                {
                    float4 positionVS = mul(UNITY_MATRIX_V, float4(positionWS, 1.0));
                    float4 positionCS = mul(UNITY_MATRIX_P, positionVS);
                    float3 nVS3 = mul((float3x3)UNITY_MATRIX_V, normalize(normalWS));
                    float2 dir = -SafeNormalize2(nVS3.xy);
                    float2 ndcPerPixel = 2.0 / _ScreenParams.xy;
                    float2 clipOffset = dir * (_OutlineWidth) * ndcPerPixel * positionCS.w;
                    positionCS.xy += clipOffset;
                    output.positionCS = positionCS;
                }
                else
                {
                    positionWS += normalWS * (_OutlineWidth);
                    output.positionCS = TransformWorldToHClip(positionWS);
                }
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                return output;
            }

            float4 SamplePalette(float index)
            {
                if (_OutlineColorCount <= 0)
                {
                    return float4(0, 0, 0, 0);
                }
                int idx = (int)round(index);
                if (idx < 0 || idx >= (int)_OutlineColorCount)
                {
                    return float4(0, 0, 0, 0);
                }
                idx = clamp(idx, 0, QB_OUTLINE_MAX - 1);
                return _OutlineColors[idx];
            }

            float4 OutlineFragmentFwd(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                float colorIndex = _OutlineIndex;
                if (colorIndex < 0.0f || _OutlineColorCount <= 0)
                {
                    clip(-1.0f);
                }
                float4 color = SamplePalette(colorIndex);
                float th = saturate(_SilhouetteThreshold);
                float s = saturate(input.silhouette);
                float g = fwidth(s);
                float feather = max(max(1e-5, _SilhouetteFeather), g);
                float a = smoothstep(th, th + feather, s);
                a = saturate(pow(a, max(1e-3, _EdgePower)));
                color.rgb = saturate(color.rgb);
                float baseAlpha = (_UsePaletteAlpha > 0.5) ? color.a : 1.0;
                color.a = a * baseAlpha;
                clip(a - 1e-5);
                return color;
            }
            ENDHLSL
        }
    }
}
