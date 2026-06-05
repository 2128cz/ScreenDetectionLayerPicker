Shader "XericLibrary/ScreenDectection/MeshPicking"
{
    Properties
    {
        // 传入材质ID
        _MatID ("Material ID", Int) = 0 
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100

        Pass
        {
            // 关闭光照和雾效，纯数据输出
            Lighting Off
            Fog { Mode Off }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float3 worldPos : TEXCOORD0; // 传递世界坐标给片段着色器
            };

            int _MatID; // 接收材质ID

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                // 计算世界坐标并传递
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // 1. 将世界坐标编码到 RGB 通道
                // 假设你的地质模型世界坐标范围在 -1000 到 1000 之间
                // 加上 1000 变成 0~2000，再除以 2000 变成 0~1
                float3 encodedPos = (i.worldPos + 1000.0) / 2000.0;
                
                // 2. 将材质ID编码到 Alpha 通道 (假设ID不超过255)
                float encodedID = _MatID / 255.0;

                // 输出 RGBA = (PosX, PosY, PosZ, MatID)
                return fixed4(encodedPos.x, encodedPos.y, encodedPos.z, encodedID);
            }
            ENDCG
        }
    }
}