Shader "XericLibrary/ScreenDectection/MeshPicking_HDR"
{
    Properties
    {
        [HideInInspector] _MatID ("Material ID", Int) = 0 
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100

        Pass
        {
            Lighting Off
            Fog { Mode Off }
            Cull Back
            ZTest LEqual
            ZWrite On

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
                float3 worldPos : TEXCOORD0; 
            };

            int _MatID; 

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                return float4(i.worldPos.xyz, (float)_MatID);
            }
            ENDCG
        }
    }
}