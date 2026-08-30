// Additive gloss stamped on top of every pixel quad.
//
// The texture is premultiplied by its own alpha in the shader, so the rounded
// transparent corners add exactly nothing and the pixel's flat color shows
// through there. Dark parts of the gloss add nothing either — only the rim and
// the highlight brighten what is underneath.
Shader "SawPixel/PixelGloss"
{
    Properties
    {
        _MainTex ("Gloss", 2D) = "black" {}
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" "PreviewType" = "Plane" }

        Blend One One
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
                fixed4 t = tex2D(_MainTex, i.uv);
                return fixed4(t.rgb * t.a * i.color.rgb * i.color.a, 0);
            }
            ENDCG
        }
    }

    Fallback Off
}
