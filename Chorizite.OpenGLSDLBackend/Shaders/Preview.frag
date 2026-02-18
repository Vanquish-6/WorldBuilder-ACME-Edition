#version 300 es
precision highp float;
precision highp sampler2DArray;

uniform sampler2DArray xOverlays;
uniform float uAlpha;
uniform float xAmbient;

in vec3 vTexCoord;

out vec4 FragColor;

void main() {
    vec4 color = texture(xOverlays, vTexCoord);
    vec3 litColor = color.rgb * (1.0 + xAmbient); // Simple lighting
    FragColor = vec4(litColor, color.a * uAlpha);
    // Debug fallback: if alpha is unexpectedly 0 but uAlpha > 0, show magenta
    if (color.a == 0.0 && uAlpha > 0.0) {
        // FragColor = vec4(1.0, 0.0, 1.0, uAlpha); // Uncomment for debug if needed
    }
}
