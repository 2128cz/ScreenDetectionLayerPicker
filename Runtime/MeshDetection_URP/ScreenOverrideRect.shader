Shader "Hidden/XericLibrary/GeoLayerPicker/ScreenOverrideRect"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline" = "UniversalPipeline" }
        LOD 100

        Pass
        {
            Name "GeoPickerDebugOverlay"
            ZWrite Off
            ZTest Always
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha 

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl" // 引入 Blitter 的标准顶点结构

            // 在 HLSL 中声明变量（不需要在 Properties 中声明）
            TEXTURE2D_X(_MainTex); // 使用 URP 标准的跨平台纹理宏
            SAMPLER(sampler_MainTex);
            float4 _DebugRect; 

            // 使用 Blitter 的标准片元输入结构 (包含完美的屏幕 UV)
            struct v2f
            {
                float4 positionCS : SV_POSITION;
                float2 texcoord   : TEXCOORD0;
            };

            // 【核心】：使用 Blitter 的标准顶点着色器，它会自动生成完美的全屏三角形和 UV
            v2f vert(Attributes input)
            {
                v2f output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
                output.texcoord = GetFullScreenTriangleTexCoord(input.vertexID);
                return output;
            }

            float4 frag (v2f i) : SV_Target
            {
                float2 rectMin = _DebugRect.xy;
                float2 rectMax = rectMin + _DebugRect.zw;

                // 1. 裁剪区域判断 (使用 Blitter 提供的完美屏幕 UV)
                if (i.texcoord.x < rectMin.x || i.texcoord.x > rectMax.x || 
                    i.texcoord.y < rectMin.y || i.texcoord.y > rectMax.y)
                {
                    return float4(0, 0, 0, 0); 
                }

                // 2. UV 映射
                float2 localUV = (i.texcoord - rectMin) / _DebugRect.zw;
                
                // 处理 Y 轴翻转 (Blitter 的 UV 原点在左下角，而 RT 渲染原点在左上角)
                #if UNITY_UV_STARTS_AT_TOP
                    localUV.y = 1.0 - localUV.y;
                #else
                    // 如果平台原点在左上，不需要翻转
                #endif

                // 3. 采样 HDR 纹理 (使用 URP 标准采样宏)
                float4 col = SAMPLE_TEXTURE2D_X(_MainTex, sampler_MainTex, localUV);

                // 4. 数据可视化
                if (col.a < 0.5) 
                {
                    return float4(0.0, 0.0, 0.0, 0.3); // 未拾取区域半透黑
                }
                
                // 将世界坐标映射为可见颜色
                float3 debugColor = frac(col.rgb * 0.1) ^ col.a; 
                return float4(debugColor, 0.9); 
            }
            ENDHLSL
        }
    }
}