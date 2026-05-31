export const SHADER_VERSION = '#version 300 es';

export const VERT_FULLSCREEN = `${SHADER_VERSION}
in vec2 aPosition;
out vec2 vUv;
void main() {
    vUv = aPosition * 0.5 + 0.5;
    gl_Position = vec4(aPosition, 0.0, 1.0);
}
`;

export const FRAG_WAVEFRONT = `${SHADER_VERSION}
precision highp float;
precision highp int;

uniform sampler2D tSVO;
uniform sampler2D tNeighbors1;
uniform sampler2D tNeighbors2;
uniform sampler2D tData;
uniform vec2 uTexSize;
uniform int uNodeCount;

in vec2 vUv;
out vec4 fragColor;

const float INF = 999999.0;
const float STATE_UNREACHED = 0.0;
const float STATE_FRONTIER = 1.0;
const float STATE_VISITED = 2.0;

vec4 sampleByIdx(sampler2D tex, float idx) {
    if (idx < 0.0) return vec4(-1.0, 0.0, -1.0, 0.0);
    float fi = floor(idx + 0.5);
    float tx = mod(fi, uTexSize.x);
    float ty = floor(fi / uTexSize.x);
    vec2 uv = (vec2(tx, ty) + 0.5) / uTexSize;
    return texture(tex, uv);
}

void main() {
    ivec2 coord = ivec2(gl_FragCoord.xy);
    int nodeIdx = int(coord.y) * int(uTexSize.x) + int(coord.x);

    if (nodeIdx >= uNodeCount || nodeIdx < 0) {
        fragColor = vec4(INF, STATE_UNREACHED, -1.0, 0.0);
        return;
    }

    vec4 myData = texture(tData, vUv);
    float myCost = myData.r;
    float myState = myData.g;
    float myParent = myData.b;

    if (myState > 1.5) {
        fragColor = myData;
        return;
    }

    if (myState > 0.5 && myState < 1.5) {
        fragColor = vec4(myCost, STATE_VISITED, myParent, 0.0);
        return;
    }

    vec4 n1 = texture(tNeighbors1, vUv);
    vec4 n2 = texture(tNeighbors2, vUv);

    float nbrs[6];
    nbrs[0] = n1.r;
    nbrs[1] = n1.g;
    nbrs[2] = n1.b;
    nbrs[3] = n1.a;
    nbrs[4] = n2.r;
    nbrs[5] = n2.g;

    float bestCost = INF;
    float bestParent = -1.0;

    for (int i = 0; i < 6; i++) {
        float nIdx = nbrs[i];
        if (nIdx < 0.0) continue;
        vec4 nd = sampleByIdx(tData, nIdx);
        float nState = nd.g;
        if (nState < 0.5 || nState > 1.5) continue;
        float newCost = nd.r + 1.0;
        if (newCost < bestCost) {
            bestCost = newCost;
            bestParent = nIdx;
        }
    }

    if (bestCost < INF) {
        fragColor = vec4(bestCost, STATE_FRONTIER, bestParent, 0.0);
    } else {
        fragColor = vec4(INF, STATE_UNREACHED, -1.0, 0.0);
    }
}
`;

export const FRAG_GOAL_DETECT = `${SHADER_VERSION}
precision highp float;

uniform sampler2D tData;
uniform float uGoalIdx;
uniform vec2 uTexSize;

in vec2 vUv;
out vec4 fragColor;

void main() {
    float fi = floor(uGoalIdx + 0.5);
    float tx = mod(fi, uTexSize.x);
    float ty = floor(fi / uTexSize.x);
    vec2 uv = (vec2(tx, ty) + 0.5) / uTexSize;

    vec4 goalData = texture(tData, uv);
    float state = goalData.g;

    float reached = 0.0;
    if (state > 1.5) reached = 1.0;
    if (state > 0.5 && state < 1.5) reached = 0.5;

    fragColor = vec4(reached, 0.0, 0.0, 1.0);
}
`;
