// Gives every pixel quad the same rounded-square silhouette as the gloss stamp.
//
// The mask is the gloss texture's own alpha, so the corner radius always matches
// whatever art is plugged into Gloss Texture — no second asset to keep in sync.
// The rounded square runs edge to edge, so neighbouring pixels still touch along
// their sides and only the corners get bitten out.
Shader "SawPixel/PixelShape"
{
    Properties
    {
        _MainTex ("Shape (alpha)", 2D) = "white" {}
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" "PreviewType" = "Plane" }

        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off
        Lighting Off
        Fog { Mode Off }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
                fixed4 color  : COLOR;
            };

            struct v2f
            {
                float4 pos   : SV_POSITION;
                float2 uv    : TEXCOORD0;
                fixed4 color : COLOR;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.color = v.color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed mask = tex2D(_MainTex, i.uv).a;
                return fixed4(i.color.rgb, i.color.a * mask);
            }
            ENDCG
        }
    }

    Fallback Off
}
