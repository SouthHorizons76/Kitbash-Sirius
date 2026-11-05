// World-space z into R, for an RFloat target. The icon camera looks down +z, and a
// baked part sits at the origin, so this is depth relative to the part's origin -
// the same unit and sign as a slot's "z" in the manifest. Larger is farther.
// Used with Camera.RenderWithShader(shader, "") so every renderer draws with it.
Shader "Kitbash/Depth"
{
    SubShader
    {
        Pass
        {
            Cull Off
            ZWrite On
            ZTest LEqual
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct v2f { float4 pos : SV_POSITION; float z : TEXCOORD0; };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.z = mul(unity_ObjectToWorld, v.vertex).z;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                return float4(i.z, 0, 0, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
