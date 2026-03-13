#version 300 es
precision highp float;
precision highp int;
precision highp sampler2D;
precision highp sampler2DArray;

in vec3 Normal;
in vec2 TexCoord;
in float TextureIndex;
in float LightingFactor;

uniform sampler2DArray uTextureArray;
uniform vec3 uHighlightColor;
uniform float uHighlightIntensity;

out vec4 FragColor;

void main() {
    vec4 color = texture(uTextureArray, vec3(TexCoord, TextureIndex));
    if (color.a < 0.5) discard;
    color.rgb *= LightingFactor;

    if (uHighlightIntensity > 0.0) {
        float rim = 1.0 - abs(dot(normalize(Normal), vec3(0.0, 0.0, 1.0)));
        float edge = smoothstep(0.2, 0.8, rim);
        color.rgb = mix(color.rgb, uHighlightColor, uHighlightIntensity * 0.35);
        color.rgb += uHighlightColor * edge * uHighlightIntensity * 0.6;
    }

    FragColor = color;
}