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
    // Use a greenish tint to make it visible against terrain, ignoring lighting for now
    vec3 tintedColor = color.rgb * vec3(0.8, 1.0, 0.8);

    // Ensure we can see something even if texture alpha is low (e.g. grass)
    float alpha = max(color.a, 0.3) * uAlpha;

    FragColor = vec4(tintedColor, alpha);
}
