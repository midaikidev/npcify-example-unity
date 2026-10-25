// Full-screen ink lines from depth and normal edges, for the URP Full Screen Pass
// renderer feature. The line darkens the color below it (colored lines, as in
// hand-drawn anime) and fades out with distance. Pass 0 draws the level, pass 1 the
// VRM characters (stencil 1 from NpcifyCharacterStencil.shader).
Shader "NpcifyDemo/Ink Outline"
{
    Properties
    {
        _InkColor ("Ink Color (A = strength)", Color) = (0.16, 0.11, 0.15, 1)
        _Thickness ("Thickness (pixels)", Range(0.5, 4)) = 1.6
        _DepthThreshold ("Depth Threshold", Range(0.001, 0.5)) = 0.03
        _NormalThreshold ("Normal Threshold", Range(0.01, 1)) = 0.5
        _FadeStart ("Fade Start", Float) = 25
        _FadeEnd ("Fade End", Float) = 80
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"

        half4 _InkColor;
        float _Thickness;
        float _DepthThreshold;
        float _NormalThreshold;
        float _FadeStart;
        float _FadeEnd;

        float EyeDepth(float2 uv)
        {
            return LinearEyeDepth(SampleSceneDepth(uv), _ZBufferParams);
        }

        half4 Ink(Varyings input, bool normalEdges)
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            float2 uv = input.texcoord;
            half4 color = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_PointClamp, uv);

            float2 step = _ScreenSize.zw * _Thickness;
            float2 offsets[4] = { float2(step.x, 0), float2(-step.x, 0), float2(0, step.y), float2(0, -step.y) };

            float depth = EyeDepth(uv);
            float3 normal = SampleSceneNormals(uv);
            float depthEdge = 0;
            float normalEdge = 0;
            [unroll]
            for (int i = 0; i < 4; i++)
            {
                float2 neighbor = uv + offsets[i];
                // Only a farther neighbor makes a line, so the line is on the near object.
                depthEdge = max(depthEdge, (EyeDepth(neighbor) - depth) / depth);
                normalEdge = max(normalEdge, 1 - dot(normal, SampleSceneNormals(neighbor)));
            }

            float edge = smoothstep(_DepthThreshold, _DepthThreshold * 1.5, depthEdge);
            if (normalEdges)
            {
                edge = max(edge, smoothstep(_NormalThreshold, _NormalThreshold + 0.15, normalEdge));
            }
            float fade = 1 - saturate((depth - _FadeStart) / max(_FadeEnd - _FadeStart, 0.001));
            float ink = edge * fade * _InkColor.a;
            color.rgb = lerp(color.rgb, color.rgb * _InkColor.rgb, ink);
            return color;
        }

        half4 FragLevel(Varyings input) : SV_Target
        {
            return Ink(input, true);
        }

        half4 FragCharacter(Varyings input) : SV_Target
        {
            return Ink(input, false);
        }
        ENDHLSL

        // The level: depth and normal edges.
        Pass
        {
            Name "InkOutline"
            Stencil
            {
                Ref 1
                Comp NotEqual
            }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragLevel
            ENDHLSL
        }

        // The VRM characters (stencil 1): depth edges only, so the silhouette and overlap
        // lines stay, and hair strands and face shapes get no lines.
        Pass
        {
            Name "InkOutlineCharacter"
            Stencil
            {
                Ref 1
                Comp Equal
            }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragCharacter
            ENDHLSL
        }
    }
}
